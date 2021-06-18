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
        private readonly ServiceWorkflowSettings _serviceWorkflowSettings;

        public EFormEmailHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper, ServiceWorkflowSettings serviceWorkflowSettings)
        {
            _dbContext = dbContextHelper.GetDbContext();
            _sdkCore = sdkCore;
            _serviceWorkflowSettings = serviceWorkflowSettings;
        }

        public async Task Handle(QueueEformEmail message)
        {
            Debugger.Break();
            await GenerateReportAndSendEmail(message.CurrentUserLanguage, message.UserName, message.CaseId);
            foreach (var (userName, language) in message.SolvedUser)
            {
                await GenerateReportAndSendEmail(language, userName, message.CaseId);
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
                await using var sqlConnection = new MySqlConnection(_serviceWorkflowSettings.AngularConnectionString);
                var sql = @$"
SELECT * FROM ConfigurationValues WHERE {nameof(PluginConfigurationValue.Id)} = @id
";
                var sendGridKey = sqlConnection.Query<PluginConfigurationValue>(sql, new { id = "EmailSettings:SendGridKey" }).FirstOrDefault()?.Value;
                var client = new SendGridClient(sendGridKey);
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

        private async Task GenerateReportAndSendEmail(Language language, string userName, int caseId)
        {

            await using var userConnection = new MySqlConnection(_serviceWorkflowSettings.AngularConnectionString);
            var sql = @$"
SELECT * FROM EmailRecipients WHERE {nameof(EmailRecipient.Name)} = @name AND {nameof(EmailRecipient.WorkflowState)} <> {Constants.WorkflowStates.Removed}
";
            var emailRecipient = userConnection.Query<EmailRecipient>(sql, new { name = userName }).FirstOrDefault();
            var caseDto = await _sdkCore.CaseLookupCaseId(caseId);
            var replyElement = await _sdkCore.CaseRead((int)caseDto.MicrotingUId, (int)caseDto.CheckUId, language);
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
                    $"{await _sdkCore.GetSdkSetting(Settings.httpServerAddress)}/cases/edit/{caseId}/{caseDto.CheckListId}")
                .Replace("{{text}}", "");

            // Fix for broken SDK not handling empty customXmlContent well
            var customXmlContent = new XElement("FillerElement",
                new XElement("InnerElement", "SomeValue")).ToString();

            // get report file
            var filePath = await _sdkCore.CaseToPdf(
                caseId,
                replyElement.Id.ToString(),
                DateTime.Now.ToString("yyyyMMddHHmmssffff"),
                $"{await _sdkCore.GetSdkSetting(Settings.httpServerAddress)}/" +
                "api/template-files/get-image/",
                "pdf",
                customXmlContent, language);

            if (!File.Exists(filePath))
            {
                throw new Exception("Error while creating report file");
            }

            await SendFileAsync(
                "no-reply@microting.com",
                userName,
                "-",
                emailRecipient?.Email,
                filePath,
                html);
        }
    }
}