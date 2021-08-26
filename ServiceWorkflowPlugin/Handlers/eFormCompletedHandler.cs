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
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.EformAngularFrontendBase.Infrastructure.Data;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace ServiceWorkflowPlugin.Handlers
{
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

            try
            {
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
                    throw new Exception(errorMessage);
                }

                if (!int.TryParse(secondEformIdValue, out var secondEformId))
                {
                    const string errorMessage = "[ERROR] Second eform id not found in setting";
                    Console.WriteLine(errorMessage);
                    throw new Exception(errorMessage);
                }

                await using var sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();

                var cls = await sdkDbContext.Cases.FirstOrDefaultAsync(x =>
                    x.MicrotingUid == message.MicrotingId);

                if (cls.CheckListId == firstEformId)
                {
                    var workflowCase = new WorkflowCase
                    {
                        MicrotingId = message.MicrotingId,
                        CheckMicrotingUid = message.CheckUId,
                        CheckId = message.CheckId
                    };

                    var language = await sdkDbContext.Languages
                        .SingleAsync(x => x.LanguageCode == LocaleNames.Danish);
                    var replyElement = await _sdkCore.CaseRead(message.MicrotingId, message.CheckUId, language);
                    //var doneBy = sdkDbContext.Workers
                    //    .Single(x => x.Id == replyElement.DoneById).full_name();
                    var checkListValue = replyElement.ElementList[0] as CheckListValue;
                    var fields = checkListValue?.DataItemList.Select(di => di as Field).ToList();

                    var picturesOfTasks = new List<FieldValue>();
                    if (fields!.Any())
                    {
                        if (!string.IsNullOrEmpty(fields[0]?.FieldValues[0]?.Value))
                        {
                            workflowCase.DateOfIncident = DateTime.Parse(fields[0].FieldValues[0].Value);
                        }

                        if (!string.IsNullOrEmpty(fields[1]?.FieldValues[0]?.Value))
                        {
                            workflowCase.IncidentTypeId = int.Parse(fields[1].FieldValues[0].Value);
                            workflowCase.IncidentType = fields[1].FieldValues[0].ValueReadable;
                        }


                        if (!string.IsNullOrEmpty(fields[2]?.FieldValues[0]?.Value))
                        {
                            workflowCase.IncidentPlaceId = int.Parse(fields[2].FieldValues[0].Value);
                            workflowCase.IncidentPlace = fields[2].FieldValues[0].ValueReadable;
                        }

                        workflowCase.PhotosExist = fields[3].FieldValues.Any();
                        workflowCase.NumberOfPhotos = 0;

                        if(fields[2].FieldValues.Count > 0)
                        {
                            foreach(FieldValue fieldValue in fields[3].FieldValues)
                            {
                                if (fieldValue.UploadedDataObj != null)
                                {
                                    picturesOfTasks.Add(fieldValue);
                                    //picturesOfTasks.Add($"{fieldValue.UploadedDataObj.Id}_700_{fieldValue.UploadedDataObj.Checksum}{fieldValue.UploadedDataObj.Extension}");
                                    workflowCase.NumberOfPhotos += 1;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(fields[4]?.FieldValues[0]?.Value))
                        {
                            workflowCase.Description = fields[4].FieldValues[0].Value;
                        }
                    }

                    Site site = await sdkDbContext.Sites
                        .SingleOrDefaultAsync(x => x.MicrotingUid == replyElement.SiteMicrotingUuid);
                    workflowCase.CreatedByUserId = replyElement.SiteMicrotingUuid;
                    workflowCase.CreatedBySiteName = site.Name;
                    workflowCase.UpdatedByUserId = replyElement.SiteMicrotingUuid;
                    workflowCase.Status = "Not initiated";
                    await workflowCase.Create(_dbContext);

                    foreach (var picturesOfTask in picturesOfTasks)
                    {
                        var pictureOfTask = new PicturesOfTask
                        {
                            FileName = $"{picturesOfTask.UploadedDataObj.Id}_700_{picturesOfTask.UploadedDataObj.Checksum}{picturesOfTask.UploadedDataObj.Extension}",
                            WorkflowCaseId = workflowCase.Id,
                            UploadedDataId = picturesOfTask.UploadedDataObj.Id,
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
                        .Replace("{{CreatedAt}}", workflowCase.CreatedAt.ToString("dd-MM-yyyy"))
                        .Replace("{{Type}}", workflowCase.IncidentType)
                        .Replace("{{Location}}", workflowCase.IncidentPlace)
                        .Replace("{{Description}}", workflowCase.Description.Replace("&", "&amp;"))
                        .Replace("{{SolvedBy}}", workflowCase.SolvedBy)
                        .Replace("{{ActionPlan}}", workflowCase.ActionPlan);

                    var sendGridKey =
                        _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                    List<string> recepients = await _baseDbContext.Users.Select(x => x.Email).ToListAsync();
                    List<EmailAddress> emailAddresses = new List<EmailAddress>();
                    foreach (string recepient in recepients)
                    {
                        emailAddresses.Add(new EmailAddress(recepient));
                    }
                    var client = new SendGridClient(sendGridKey.Value);
                    string text = "";
                    var fromEmailAddress = new EmailAddress("no-reply@microting.com", "no-reply@microting.com");
                    //var toEmail = new EmailAddress(to.Replace(" ", ""));
                    var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress, emailAddresses,
                        $"Opfølgning: {workflowCase.IncidentType};  {workflowCase.IncidentPlace}; {workflowCase.CreatedAt:dd-MM-yyyy}",
                        "", html);
                    // var bytes = await File.ReadAllBytesAsync(fileName);
                    // var file = Convert.ToBase64String(bytes);
                    // msg.AddAttachment(Path.GetFileName(fileName), file);
                    var response = await client.SendEmailAsync(msg);
                    if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    {
                        throw new Exception($"Status: {response.StatusCode}");
                    }

                    await _emailHelper.GenerateReportAndSendEmail(site.LanguageId, workflowCase.CreatedBySiteName, workflowCase);
                }
                else if(message.CheckId == secondEformId)
                {
                    var workflowCase = await _dbContext.WorkflowCases
                        .Where(x => x.DeployedMicrotingUid == message.MicrotingId)
                        .SingleOrDefaultAsync();

                    var language = await sdkDbContext.Languages
                        .SingleAsync(x => x.LanguageCode == LocaleNames.Danish);
                    var replyElement = await _sdkCore.CaseRead(message.MicrotingId, message.CheckUId, language);
                    var checkListValue = replyElement.ElementList[0] as CheckListValue;
                    var fields = checkListValue?.DataItemList.Select(di => di as Field).ToList();


                    var picturesOfTasks = new List<FieldValue>();
                    if (fields!.Any())
                    {
                        if (!string.IsNullOrEmpty(fields[2]?.FieldValues[0]?.Value))
                        {
                            workflowCase.Description = fields[2].FieldValues[0].Value;
                        }

                        if (!string.IsNullOrEmpty(fields[4]?.FieldValues[0]?.Value))
                        {
                            workflowCase.Description = fields[4].FieldValues[0].Value;
                        }

                        if (!string.IsNullOrEmpty(fields[3]?.FieldValues[0]?.Value))
                        {
                            workflowCase.ActionPlan = fields[3].FieldValues[0].Value;
                        }

                        if(fields[4].FieldValues.Count > 0)
                        {
                            foreach(FieldValue fieldValue in fields[4].FieldValues)
                            {
                                if (fieldValue.UploadedDataObj != null)
                                {
                                    picturesOfTasks.Add(fieldValue);
                                    workflowCase.NumberOfPhotos += 1;
                                }
                            }
                        }

                        foreach (var picturesOfTask in picturesOfTasks)
                        {
                            var pictureOfTask = new PicturesOfTaskDone
                            {
                                FileName = $"{picturesOfTask.UploadedDataObj.Id}_700_{picturesOfTask.UploadedDataObj.Checksum}{picturesOfTask.UploadedDataObj.Extension}",
                                WorkflowCaseId = workflowCase.Id,
                                UploadedDataId = picturesOfTask.UploadedDataObj.Id,
                                Longitude = picturesOfTask.Longitude,
                                Latitude = picturesOfTask.Latitude
                            };

                            await pictureOfTask.Create(_dbContext);
                        }

                        await workflowCase.Update(_dbContext);
                        await _sdkCore.CaseDelete(message.MicrotingId);

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
                        .Replace("{{CreatedAt}}", workflowCase.CreatedAt.ToString("dd-MM-yyyy"))
                        .Replace("{{Type}}", workflowCase.IncidentType)
                        .Replace("{{Location}}", workflowCase.IncidentPlace)
                        .Replace("{{Description}}", workflowCase.Description.Replace("&", "&amp;"))
                        .Replace("{{SolvedBy}}", workflowCase.SolvedBy)
                        .Replace("{{ActionPlan}}", workflowCase.ActionPlan);

                    var sendGridKey =
                        _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                    List<string> recepients = await _baseDbContext.Users.Select(x => x.Email).ToListAsync();
                    List<EmailAddress> emailAddresses = new List<EmailAddress>();
                    foreach (string recepient in recepients)
                    {
                        emailAddresses.Add(new EmailAddress(recepient));
                    }
                    var client = new SendGridClient(sendGridKey.Value);
                    string text = "";
                    var fromEmailAddress = new EmailAddress("no-reply@microting.com", "no-reply@microting.com");
                    //var toEmail = new EmailAddress(to.Replace(" ", ""));
                    var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress, emailAddresses,
                        $"Opfølgning: {workflowCase.IncidentType};  {workflowCase.IncidentPlace}; {workflowCase.CreatedAt:dd-MM-yyyy}",
                        "", html);
                    // var bytes = await File.ReadAllBytesAsync(fileName);
                    // var file = Convert.ToBase64String(bytes);
                    // msg.AddAttachment(Path.GetFileName(fileName), file);
                    var response = await client.SendEmailAsync(msg);
                    if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    {
                        throw new Exception($"Status: {response.StatusCode}");
                    }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERR] ServiceWorkFlowPlugin.CaseCompleted: Got the following error: {ex.Message}");
            }
        }
    }
}
