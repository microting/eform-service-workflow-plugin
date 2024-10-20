/*
The MIT License (MIT)
Copyright (c) 2007 - 2021 Microting A/S
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/


using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microting.eForm.Dto;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.EformAngularFrontendBase.Infrastructure.Data;
using Microting.eFormWorkflowBase.Helpers;
using SendGrid;
using SendGrid.Helpers.Mail;
using Sentry;

namespace ServiceWorkflowPlugin.Handlers;

using System;
using System.Linq;
using System.Threading.Tasks;
using eFormCore;
using Infrastructure.Helpers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Models;
using Microting.eFormApi.BasePn.Infrastructure.Consts;
using Microting.eFormWorkflowBase.Infrastructure.Data.Entities;
using Rebus.Handlers;
using Microting.eFormWorkflowBase.Infrastructure.Data;

public class EFormCompletedHandler : IHandleMessages<eFormCompleted>
{
    private readonly Core _sdkCore;
    private readonly WorkflowPnDbContext _dbContext;
    private bool _s3Enabled;
    private bool _swiftEnabled;
    private readonly BaseDbContext _baseDbContext;
    private readonly EmailHelper _emailHelper;

    public EFormCompletedHandler(Core sdkCore, DbContextHelper dbContextHelper, BaseDbContext baseDbContext, EmailHelper emailHelper)
    {
        _sdkCore = sdkCore;
        _dbContext = dbContextHelper.GetDbContext();
        _baseDbContext = baseDbContext;
        _emailHelper = emailHelper;
    }
    public async Task Handle(eFormCompleted message)
    {
        Console.WriteLine("[INF] EFormCompletedHandler.Handle: called");

        _s3Enabled = _sdkCore.GetSdkSetting(Settings.s3Enabled).Result.ToLower() == "true";
        _swiftEnabled = _sdkCore.GetSdkSetting(Settings.swiftEnabled).Result.ToLower() == "true";
        var firstEformIdValue = _dbContext.PluginConfigurationValues
            .SingleOrDefault(x => x.Name == "WorkflowBaseSettings:FirstEformId")?.Value;

        var secondEformIdValue = _dbContext.PluginConfigurationValues
            .SingleOrDefault(x => x.Name == "WorkflowBaseSettings:SecondEformId")?.Value;

        if (!int.TryParse(firstEformIdValue, out var firstEformId))
        {
            const string errorMessage = "[ERROR] First eform id not found in setting";
            Console.WriteLine(errorMessage);
        }

        if (!int.TryParse(secondEformIdValue, out var secondEformId))
        {
            const string errorMessage = "[ERROR] Second eform id not found in setting";
            Console.WriteLine(errorMessage);
        }

        try
        {
            await using var sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();
            var dbCase = await sdkDbContext.Cases
                             .AsNoTracking()
                             .FirstOrDefaultAsync(x => x.Id == message.CaseId) ??
                         await sdkDbContext.Cases
                             .FirstOrDefaultAsync(x => x.MicrotingCheckUid == message.CheckUId);


            if (dbCase.CheckListId == firstEformId)
            {
                var workflowCase = new WorkflowCase
                {
                    MicrotingId = message.MicrotingUId,
                    CheckMicrotingUid = message.CheckUId,
                    CheckId = message.CheckId
                };

                // var eformIdForNewTasks = await sdkDbContext.CheckLists
                //     .Where(x => x.OriginalId == "5769")
                //     .Where(x => x.ParentId == null)
                //     .Select(x => x.Id)
                //     .FirstOrDefaultAsync();

                var subCheckList = await sdkDbContext.CheckLists
                    .FirstOrDefaultAsync(x => x.ParentId == firstEformId)
                    .ConfigureAwait(false);

                var dateOfIncidentField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371265");

                if (dateOfIncidentField == null)
                {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Dato for hændelse" and the field.checkListId set to the subCheckList.Id and set the dateOfIncidentField to the first field
                    dateOfIncidentField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Dato for hændelse")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371265"
                    dateOfIncidentField.OriginalId = "371265";
                    await dateOfIncidentField.Update(sdkDbContext);
                }

                var dateOfIncidentFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(
                        x => x.FieldId == dateOfIncidentField.Id && x.CaseId == dbCase.Id);
                workflowCase.DateOfIncident = Convert.ToDateTime(dateOfIncidentFieldValue.Value);

                var incidentFieldEntityGroupId = await sdkDbContext.EntityGroups
                    .Where(x => x.Name == "eform-angular-workflow-plugin-editable-AccidentType")
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();
                var incidentTypeField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371261");

                if (incidentTypeField == null)
                {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Hændelsestype" and the field.checkListId set to the subCheckList.Id and set the incidentTypeField to the first field
                    incidentTypeField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Type")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371261"
                    incidentTypeField.OriginalId = "371261";
                    await incidentTypeField.Update(sdkDbContext);
                }


                var incidentTypeFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(
                        x => x.FieldId == incidentTypeField.Id && x.CaseId == dbCase.Id);
                var incidentType = await sdkDbContext.EntityItems.FirstAsync(x =>
                    x.EntityGroupId == incidentFieldEntityGroupId && x.Id == int.Parse(incidentTypeFieldValue.Value));
                workflowCase.IncidentTypeId = int.Parse(incidentTypeFieldValue.Value);
                workflowCase.IncidentType = incidentType.Name;

                var locationFieldEntityGroupId = await sdkDbContext.EntityGroups
                    .Where(x => x.Name == "eform-angular-workflow-plugin-editable-AccidentLocations")
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();
                var locationField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371262");

                if (locationField == null)
                {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Lokation" and the field.checkListId set to the subCheckList.Id and set the locationField to the first field
                    locationField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Sted")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371262"
                    locationField.OriginalId = "371262";
                    await locationField.Update(sdkDbContext);
                }


                var locationFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(
                        x => x.FieldId == locationField.Id && x.CaseId == dbCase.Id);
                var location = await sdkDbContext.EntityItems.FirstAsync(x =>
                    x.EntityGroupId == locationFieldEntityGroupId && x.Id == int.Parse(locationFieldValue.Value));
                workflowCase.IncidentPlaceId = int.Parse(locationFieldValue.Value);
                workflowCase.IncidentPlace = location.Name;

                var descriptionField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371264");

                if (descriptionField == null)
                {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Beskrivelse" and the field.checkListId set to the subCheckList.Id and set the descriptionField to the first field
                    descriptionField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Beskrivelse")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371264"
                    descriptionField.OriginalId = "371264";
                    await descriptionField.Update(sdkDbContext);
                }


                var descriptionFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(
                        x => x.FieldId == descriptionField.Id && x.CaseId == dbCase.Id);
                workflowCase.Description = descriptionFieldValue.Value;

                var pictureField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371263");

                if (pictureField == null)
                {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Billeder" and the field.checkListId set to the subCheckList.Id and set the pictureField to the first field
                    pictureField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Foto")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371263"
                    pictureField.OriginalId = "371263";
                    await pictureField.Update(sdkDbContext);
                }

                var pictureFieldValues = await sdkDbContext.FieldValues
                    .Where(x => x.FieldId == pictureField.Id && x.CaseId == dbCase.Id).ToListAsync();

                workflowCase.NumberOfPhotos = 0;
                var picturesOfTasks = new List<Microting.eForm.Infrastructure.Data.Entities.FieldValue>();
                foreach (var pictureFieldValue in pictureFieldValues.Where(pictureFieldValue =>
                             pictureFieldValue.UploadedDataId != null))
                {
                    picturesOfTasks.Add(pictureFieldValue);
                                 workflowCase.NumberOfPhotos += 1;
                }

                Site site = await sdkDbContext.Sites
                    .SingleOrDefaultAsync(x => x.Id == dbCase.SiteId);
                workflowCase.CreatedByUserId = (int)dbCase.WorkerId!;
                workflowCase.CreatedBySiteName = site.Name;
                workflowCase.UpdatedByUserId = (int)dbCase.WorkerId!;
                workflowCase.Status = "Not initiated";
                await workflowCase.Create(_dbContext);

                foreach (var picturesOfTask in picturesOfTasks)
                {
                    var uploadedData =
                        await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == picturesOfTask.UploadedDataId);
                    var pictureOfTask = new PicturesOfTask
                    {
                        FileName = $"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}",
                        WorkflowCaseId = workflowCase.Id,
                        UploadedDataId = uploadedData.Id,
                        Longitude = picturesOfTask.Longitude,
                        Latitude = picturesOfTask.Latitude
                    };

                    await pictureOfTask.Create(_dbContext);
                }

                var assembly = Assembly.GetExecutingAssembly();
                var assemblyName = assembly.GetName().Name;
                var stream = assembly.GetManifestResourceStream($"{assemblyName}.Resources.Email.html");
                string html;
                if (stream == null)
                {
                    throw new InvalidOperationException("Resource not found");
                }
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    html = await reader.ReadToEndAsync();
                }

                html = html
                    .Replace("{{link}}",
                        $"{await _sdkCore.GetSdkSetting(Settings.httpServerAddress)}/plugins/workflow-pn/edit-workflow-case/{workflowCase.Id}")
                    .Replace("{{CreatedBy}}", workflowCase.CreatedBySiteName)
                    .Replace("{{CreatedAt}}", workflowCase.DateOfIncident.ToString("dd-MM-yyyy"))
                    .Replace("{{Type}}", workflowCase.IncidentType)
                    .Replace("{{Location}}", workflowCase.IncidentPlace)
                    .Replace("{{Description}}", workflowCase.Description.Replace("&", "&amp;"))
                    .Replace("{{SolvedBy}}", workflowCase.SolvedBy)
                    .Replace("{{ActionPlan}}", workflowCase.ActionPlan);

                var sendGridKey =
                    _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                List<string> recipients = await _baseDbContext.EmailRecipients
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Join(_baseDbContext.EmailTagRecipients,
                          recipient => recipient.Id,
                          tagRecipient => tagRecipient.EmailRecipientId,
                          (recipient, tagRecipient) => new { recipient, tagRecipient })
                    .Join(_baseDbContext.EmailTags,
                          combined => combined.tagRecipient.EmailTagId,
                          tag => tag.Id,
                          (combined, tag) => new { combined.recipient, tag })
                    .Where(x => x.tag.Name == "Administrationen")
                    .Select(x => x.recipient.Email)
                    .ToListAsync();

                List<EmailAddress> emailAddresses = new List<EmailAddress>();
                foreach (string recipient in recipients)
                {
                    if (!recipient.Contains("admin.com"))
                    {
                        emailAddresses.Add(new EmailAddress(recipient));
                    }
                }
                var client = new SendGridClient(sendGridKey.Value);
                var fromEmailAddress = new EmailAddress("no-reply@microting.com", "no-reply@microting.com");
                if (emailAddresses.Count != 0)
                {
                    var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress, emailAddresses,
                        $"Opfølgning: {workflowCase.IncidentPlace}; {workflowCase.IncidentType}; {workflowCase.DateOfIncident:dd-MM-yyyy}",
                        "", html);
                    var response = await client.SendEmailAsync(msg);
                    if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    {
                        throw new Exception($"Status: {response.StatusCode}");
                    }
                }

                await _emailHelper.GenerateReportAndSendEmail(site.LanguageId, workflowCase.CreatedBySiteName.Replace(" ", ""), workflowCase);
            }
            else if (message.CheckId == secondEformId)
            {
                var workflowCase = await _dbContext.WorkflowCases
                    .Where(x => x.DeployedMicrotingUid == message.MicrotingUId)
                    .SingleOrDefaultAsync();

                var subCheckList = await sdkDbContext.CheckLists
                    .FirstOrDefaultAsync(x => x.ParentId == secondEformId)
                    .ConfigureAwait(false);

                var picturesOfTasks = new List<Microting.eForm.Infrastructure.Data.Entities.FieldValue>();

                var descriptionField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371271");

                if (descriptionField == null)
                {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Beskrivelse" and the field.checkListId set to the subCheckList.Id and set the descriptionField to the first field
                    descriptionField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Beskrivelse")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371271"
                    descriptionField.OriginalId = "371271";
                    await descriptionField.Update(sdkDbContext);
                }

                var descriptionFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(
                        x => x.FieldId == descriptionField.Id && x.CaseId == dbCase.Id);
                workflowCase.Description = descriptionFieldValue.Value;

                var actionPlanField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371272");

                if (actionPlanField == null) {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Beskrivelse" and the field.checkListId set to the subCheckList.Id and set the descriptionField to the first field
                    actionPlanField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Handlingsplan")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371271"
                    actionPlanField.OriginalId = "371272";
                    await actionPlanField.Update(sdkDbContext);
                }


                var actionPlanFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(
                        x => x.FieldId == actionPlanField.Id && x.CaseId == dbCase.Id);
                workflowCase.ActionPlan = actionPlanFieldValue.Value;

                var pictureField =
                    await sdkDbContext.Fields.FirstOrDefaultAsync(x =>
                        x.CheckListId == subCheckList.Id && x.OriginalId == "371270");

                if (pictureField == null) {
                    // join the fields and fieldTranslations with the fieldTranslations.text set to "Billeder" and the field.checkListId set to the subCheckList.Id and set the pictureField to the first field
                    pictureField = await sdkDbContext.Fields
                        .Join(sdkDbContext.FieldTranslations,
                            field => field.Id,
                            fieldTranslation => fieldTranslation.FieldId,
                            (field, fieldTranslation) => new { field, fieldTranslation })
                        .Where(x => x.fieldTranslation.Text == "Billede af udført opgave")
                        .Where(x => x.field.CheckListId == subCheckList.Id)
                        .Select(x => x.field)
                        .FirstOrDefaultAsync();

                    // then update the originalId of the field to "371270"
                    pictureField.OriginalId = "371270";
                    await pictureField.Update(sdkDbContext);
                }
                var pictureFieldValues = await sdkDbContext.FieldValues
                    .Where(x => x.FieldId == pictureField.Id && x.CaseId == dbCase.Id).ToListAsync();

                foreach (var pictureFieldValue in pictureFieldValues.Where(pictureFieldValue =>
                             pictureFieldValue.UploadedDataId != null))
                {
                    picturesOfTasks.Add(pictureFieldValue);
                    workflowCase.NumberOfPhotos += 1;
                }


                foreach (var picturesOfTask in picturesOfTasks)
                {
                    var uploadedData =
                        await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == picturesOfTask.UploadedDataId);
                    var pictureOfTask = new PicturesOfTaskDone
                    {
                        FileName = $"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}",
                        WorkflowCaseId = workflowCase.Id,
                        UploadedDataId = uploadedData.Id,
                        Longitude = picturesOfTask.Longitude,
                        Latitude = picturesOfTask.Latitude
                    };

                    await pictureOfTask.Create(_dbContext);
                }

                await workflowCase.Update(_dbContext);
                await _sdkCore.CaseDelete(message.MicrotingUId);

                var assembly = Assembly.GetExecutingAssembly();
                var assemblyName = assembly.GetName().Name;
                var stream = assembly.GetManifestResourceStream($"{assemblyName}.Resources.Email.html");
                string html;
                if (stream == null)
                {
                    throw new InvalidOperationException("Resource not found");
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    html = await reader.ReadToEndAsync();
                }

                html = html
                    .Replace("{{link}}",
                        $"{await _sdkCore.GetSdkSetting(Settings.httpServerAddress)}/plugins/workflow-pn/edit-workflow-case/{workflowCase.Id}")
                    .Replace("{{CreatedBy}}", workflowCase.CreatedBySiteName)
                    .Replace("{{CreatedAt}}", workflowCase.DateOfIncident.ToString("dd-MM-yyyy"))
                    .Replace("{{Type}}", workflowCase.IncidentType)
                    .Replace("{{Location}}", workflowCase.IncidentPlace)
                    .Replace("{{Description}}", workflowCase.Description.Replace("&", "&amp;"))
                    .Replace("{{SolvedBy}}", workflowCase.SolvedBy)
                    .Replace("{{ActionPlan}}", workflowCase.ActionPlan);

                var sendGridKey =
                    _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                List<string> recepients = await _baseDbContext.Users.Select(x => x.Email).ToListAsync();
                List<EmailAddress> emailAddresses = new List<EmailAddress>();
                foreach (string recipient in recepients)
                {
                    if (!recipient.Contains("admin.com"))
                    {
                        emailAddresses.Add(new EmailAddress(recipient));
                    }
                }

                var client = new SendGridClient(sendGridKey.Value);
                var fromEmailAddress = new EmailAddress("no-reply@microting.com", "no-reply@microting.com");
                var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress, emailAddresses,
                    $"Opfølgning: {workflowCase.IncidentPlace}; {workflowCase.IncidentType}; {workflowCase.DateOfIncident:dd-MM-yyyy}",
                    "", html);
                var response = await client.SendEmailAsync(msg);
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    throw new Exception($"Status: {response.StatusCode}");
                }
                //}
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            Console.WriteLine($"[ERR] ServiceWorkFlowPlugin.CaseCompleted: Got the following error: {ex.Message}");
        }
    }
}