using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Dapper;
using Microting.eForm.Dto;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eFormApi.BasePn.Infrastructure.Database.Entities;
using Microting.eFormApi.BasePn.Infrastructure.Database.Entities.Malling;
using Microting.eFormWorkflowBase.Infrastructure.Data;
using Microting.eFormWorkflowBase.Infrastructure.Data.Entities;
using MySqlConnector;
using Rebus.Handlers;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Collections.Generic;
using Amazon.S3.Model;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Helpers;
using Microting.eForm.Infrastructure;
using Microting.EformAngularFrontendBase.Infrastructure.Data;
using Microting.eFormWorkflowBase.Infrastructure.Data.Entities;
using Microting.eFormWorkflowBase.Messages;

namespace ServiceWorkflowPlugin.Infrastructure.Helpers
{
    public class EmailHelper
    {
        private readonly eFormCore.Core _sdkCore;
        private readonly WorkflowPnDbContext _dbContext;
        private readonly BaseDbContext _baseDbContext;

        public EmailHelper(eFormCore.Core sdkCore, DbContextHelper dbContextHelper, BaseDbContext baseDbContext)
        {
            _dbContext = dbContextHelper.GetDbContext();
            _sdkCore = sdkCore;
            _baseDbContext = baseDbContext;

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
                var bytes = await File.ReadAllBytesAsync(fileName);
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

        public async Task GenerateReportAndSendEmail(int languageId, string userName, WorkflowCase workflowCase)
        {
            var emailRecipient = await _baseDbContext.EmailRecipients.SingleOrDefaultAsync(x => x.Name == userName);
            await using MicrotingDbContext sdkDbConetxt = _sdkCore.DbContextHelper.GetDbContext();
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
                .Replace(
                    "<a href=\"{{link}}\">Link til sag</a>", "")
                .Replace("{{CreatedBy}}", workflowCase.CreatedBySiteName)
                .Replace("{{CreatedAt}}", workflowCase.CreatedAt.ToString("dd-MM-yyyy"))
                .Replace("{{Type}}", workflowCase.IncidentType)
                .Replace("{{Location}}", workflowCase.IncidentPlace)
                .Replace("{{Description}}", workflowCase.Description.Replace("&", "&amp;"))
                .Replace("<p>Ansvarlig: {{SolvedBy}}</p>", "")
                .Replace("<p>Handlingsplan: {{ActionPlan}}</p>", "");

            List<KeyValuePair<string, List<string>>> pictures = new List<KeyValuePair<string, List<string>>>();

            SortedDictionary<string, string> valuePairs = new SortedDictionary<string, string>
            {
                {"{created_by}", workflowCase.CreatedBySiteName},
                {"{created_date}", workflowCase.CreatedAt.ToString("dd-MM-yyyy")},
                {"{incident_type}", workflowCase.IncidentType},
                {"{incident_location}", workflowCase.IncidentPlace},
                {"{incident_description}", workflowCase.Description.Replace("&", "&amp;")},
                {"{incident_deadline}", workflowCase.Deadline?.ToString("dd-MM-yyyy")},
                {"{incident_action_plan}", workflowCase.ActionPlan?.Replace("&", "&amp;")},
                {"{incident_solved_by}", workflowCase.SolvedBy},
                {"{incident_status}", GetStatusTranslated(workflowCase.Status)}
            };

            foreach (PicturesOfTask picturesOfTask in await _dbContext.PicturesOfTasks.Where(x => x.WorkflowCaseId == workflowCase.Id).ToListAsync())
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

            foreach (PicturesOfTaskDone picturesOfTask in await _dbContext.PicturesOfTaskDone.Where(x => x.WorkflowCaseId == workflowCase.Id).ToListAsync())
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
                $"{timeStamp}_{workflowCase.Id}.docx");


            await using (var fileStream = File.Create(resultDocument))
            {
                if (stream != null) await stream.CopyToAsync(fileStream);
            }

            ReportHelper.SearchAndReplace(valuePairs, resultDocument);

            ReportHelper.InsertImages(resultDocument, pictures);
            string outputFolder = Path.Combine(Path.GetTempPath(), "results");

            ReportHelper.ConvertToPdf(resultDocument, outputFolder);

            string filePath = Path.Combine(Path.GetTempPath(), "results",
                $"{timeStamp}_{workflowCase.Id}.pdf");

            await SendFileAsync(
                "no-reply@microting.com",
                userName,
                $"Kvittering: {workflowCase.IncidentType};  {workflowCase.IncidentPlace}; {workflowCase.CreatedAt:dd-MM-yyyy}",
                emailRecipient?.Email,
                filePath,
                null,
                html);
        }

        private string GetStatusTranslated(string constant)
        {
            switch (constant)
            {
                case "Not initiated":
                    return "Ikke igangsat";
                case "Ongoing":
                    return "Igangværende";
                case "No status":
                    return "Vælg status";
                case "Closed":
                    return "Afsluttet";
                case "Canceled":
                    return "Annulleret";
                default:
                    return "Ikke igangsat";
            }
        }
    }
}