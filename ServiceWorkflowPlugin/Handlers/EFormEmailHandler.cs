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
using Amazon.S3.Model;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Helpers;
using Microting.eForm.Infrastructure;
using Microting.EformAngularFrontendBase.Infrastructure.Data;
using Microting.eFormWorkflowBase.Infrastructure.Data.Entities;
using Microting.eFormWorkflowBase.Messages;

namespace ServiceWorkflowPlugin.Handlers
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using Dapper;
    using Infrastructure;
    using Infrastructure.Helpers;
    using Messages;
    using Microting.eForm.Dto;
    using Microting.eForm.Infrastructure.Constants;
    using Microting.eForm.Infrastructure.Data.Entities;
    using Microting.eFormApi.BasePn.Infrastructure.Database.Entities;
    using Microting.eFormApi.BasePn.Infrastructure.Database.Entities.Malling;
    using Microting.eFormWorkflowBase.Infrastructure.Data;
    using MySqlConnector;
    using Rebus.Handlers;
    using SendGrid;
    using SendGrid.Helpers.Mail;

    public class EFormEmailHandler : IHandleMessages<QueueEformEmail>
    {
        private readonly eFormCore.Core _sdkCore;
        private readonly WorkflowPnDbContext _dbContext;
        private readonly BaseDbContext _baseDbContext;

        public EFormEmailHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper, BaseDbContext baseDbContext)
        {
            _dbContext = dbContextHelper.GetDbContext();
            _sdkCore = sdkCore;
            _baseDbContext = baseDbContext;
        }

        public async Task Handle(QueueEformEmail message)
        {
            WorkflowCase workflowCase = await _dbContext.WorkflowCases.SingleOrDefaultAsync(x => x.Id == message.CaseId);
            await using MicrotingDbContext sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();
            Microting.eForm.Infrastructure.Data.Entities.Case _case = await
                sdkDbContext.Cases.SingleOrDefaultAsync(x => x.MicrotingCheckUid == workflowCase.CheckMicrotingUid);
            Site createdBySite = await sdkDbContext.Sites.SingleOrDefaultAsync(x => x.Id == _case.SiteId);

            await GenerateReportAndSendEmail(createdBySite.LanguageId, createdBySite.Name, message.CaseId, workflowCase, _case);

            if (!string.IsNullOrEmpty(workflowCase.SolvedBy))
            {
                Site site = await sdkDbContext.Sites.SingleOrDefaultAsync(x =>
                    x.Name == workflowCase.SolvedBy);

                await GenerateReportAndSendEmail(site.LanguageId, site.Name, message.CaseId, workflowCase, _case);
            }
        }


        private async Task SendFileAsync(
            string fromEmail,
            string fromName,
            string subject,
            string to,
            string fileName,
            string text = null, string html = null)
        {
            try
            {

                var sendGridKey =
                    _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                var client = new SendGridClient(sendGridKey.Value);
                var fromEmailAddress = new EmailAddress(fromEmail.Replace(" ", ""), fromName);
                var toEmail = new EmailAddress(to.Replace(" ", ""));
                var msg = MailHelper.CreateSingleEmail(fromEmailAddress, toEmail, subject, text, html);
                var bytes = File.ReadAllBytes(fileName);
                var file = Convert.ToBase64String(bytes);
                msg.AddAttachment(Path.GetFileName(fileName), file);
                var response = await client.SendEmailAsync(msg);
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    throw new Exception($"Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to send email message", ex);
            }
            finally
            {
                File.Delete(fileName);
            }
        }

        private async Task GenerateReportAndSendEmail(int languageId, string userName, int caseId, WorkflowCase workflowCase, Microting.eForm.Infrastructure.Data.Entities.Case _case)
        {
            var emailRecipient = await _baseDbContext.EmailRecipients.SingleOrDefaultAsync(x => x.Name == userName);
            //var caseDto = await _sdkCore.CaseLookupCaseId(caseId);
            await using MicrotingDbContext sdkDbConetxt = _sdkCore.DbContextHelper.GetDbContext();
            //Language language = await sdkDbConetxt.Languages.SingleOrDefaultAsync(x => x.Id == languageId);
            //var replyElement = await _sdkCore.CaseRead((int)_case.MicrotingUid, (int)_case.MicrotingCheckUid, language);
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
                    $"{await _sdkCore.GetSdkSetting(Settings.httpServerAddress)}/plugins/workflow-pn/edit-workflow-case/{caseId}")
                .Replace("{{text}}", "");


            List<KeyValuePair<string, List<string>>> pictures = new List<KeyValuePair<string, List<string>>>();
            Site createdBySite = await sdkDbConetxt.Sites.SingleOrDefaultAsync(x => x.Id == _case.SiteId);

            SortedDictionary<string, string> valuePairs = new SortedDictionary<string, string>
            {
                {"{created_by}", createdBySite.Name},
                {"{created_date}", workflowCase.CreatedAt.ToString("dd-MM-yyyy")},
                {"{incident_type}", workflowCase.IncidentType},
                {"{incident_location}", workflowCase.IncidentPlace},
                {"{incident_description}", workflowCase.Description.Replace("&", "&amp;")},
                {"{incident_deadline}", workflowCase.Deadline?.ToString("dd-MM-yyyy")},
                {"{incident_action_plan}", workflowCase.ActionPlan.Replace("&", "&amp;")},
                {"{incident_solved_by}", workflowCase.SolvedBy},
                {"{incident_status}", workflowCase.Status}
            };


            foreach (PicturesOfTask picturesOfTask in await _dbContext.PicturesOfTasks.Where(x => x.WorkflowCaseId == caseId).ToListAsync())
            {
                UploadedData uploadedData =
                    await sdkDbConetxt.UploadedDatas.SingleOrDefaultAsync(x => x.Id == picturesOfTask.UploadedDataId);

                FieldValue fieldValue =
                    await sdkDbConetxt.FieldValues.SingleOrDefaultAsync(x =>
                        x.UploadedDataId == picturesOfTask.UploadedDataId);

                var list = new List<string>();

                string fileName =
                    $"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}";


                string fileContent = "";
                using GetObjectResponse response =
                    await _sdkCore.GetFileFromS3Storage(fileName);
                using var image = new MagickImage(response.ResponseStream);
                fileContent = image.ToBase64();

                string geoTag = "";
                if (fieldValue.Latitude != null)
                {
                    geoTag =
                        $"https://www.google.com/maps/place/{fieldValue.Latitude},{fieldValue.Longitude}";
                }

                list.Add(fileContent);
                list.Add(geoTag);

                pictures.Add(new KeyValuePair<string, List<string>>("Billeder af hændelsen", list));
            }

            foreach (PicturesOfTaskDone picturesOfTask in await _dbContext.PicturesOfTaskDone.Where(x => x.WorkflowCaseId == caseId).ToListAsync())
            {
                UploadedData uploadedData =
                    await sdkDbConetxt.UploadedDatas.SingleOrDefaultAsync(x => x.Id == picturesOfTask.UploadedDataId);

                FieldValue fieldValue =
                    await sdkDbConetxt.FieldValues.SingleOrDefaultAsync(x =>
                        x.UploadedDataId == picturesOfTask.UploadedDataId);

                var list = new List<string>();

                string fileName =
                    $"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}";


                string fileContent = "";
                using GetObjectResponse response =
                    await _sdkCore.GetFileFromS3Storage(fileName);
                using var image = new MagickImage(response.ResponseStream);
                fileContent = image.ToBase64();

                string geoTag = "";
                if (fieldValue.Latitude != null)
                {
                    geoTag =
                        $"https://www.google.com/maps/place/{fieldValue.Latitude},{fieldValue.Longitude}";
                }

                list.Add(fileContent);
                list.Add(geoTag);

                pictures.Add(new KeyValuePair<string, List<string>>("Billeder behandlet hændelse", list));
            }

            stream = assembly.GetManifestResourceStream($"{assemblyName}.Resources.report.docx");

            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "results"));

            string timeStamp = DateTime.Now.ToString("yyyyMMddHHmmssffff");
            string resultDocument = Path.Combine(Path.GetTempPath(), "results",
                $"{timeStamp}_{caseId}.docx");


            using (var fileStream = File.Create(resultDocument))
            {
                stream.CopyTo(fileStream);
            }


            ReportHelper.SearchAndReplace(valuePairs, resultDocument);


            ReportHelper.InsertImages(resultDocument, pictures);
            string outputFolder = Path.Combine(Path.GetTempPath(), "results");

            ReportHelper.ConvertToPdf(resultDocument, outputFolder);

            string filePath = Path.Combine(Path.GetTempPath(), "results",
                $"{timeStamp}_{caseId}.pdf");


            // // Fix for broken SDK not handling empty customXmlContent well
            // var customXmlContent = new XElement("FillerElement",
            //     new XElement("InnerElement", "SomeValue")).ToString();
            //
            // // get report file
            // var filePath = await _sdkCore.CaseToPdf(
            //     caseId,
            //     replyElement.Id.ToString(),
            //     DateTime.Now.ToString("yyyyMMddHHmmssffff"),
            //     $"{await _sdkCore.GetSdkSetting(Settings.httpServerAddress)}/" +
            //     "api/template-files/get-image/",
            //     "pdf",
            //     customXmlContent, language);
            //
            // if (!File.Exists(filePath))
            // {
            //     throw new Exception("Error while creating report file");
            // }

            await SendFileAsync(
                "no-reply@microting.com",
                userName,
                $"{workflowCase.CreatedAt:dd-MM-yyyy}; {workflowCase.IncidentType}; {workflowCase.IncidentPlace}",
                emailRecipient?.Email,
                filePath,
                html);
        }
    }
}