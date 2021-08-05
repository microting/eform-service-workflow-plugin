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

using Microsoft.EntityFrameworkCore;
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

            await GenerateReportAndSendEmail(createdBySite.LanguageId, createdBySite.Name, message.CaseId);

            if (!string.IsNullOrEmpty(workflowCase.SolvedBy))
            {
                Site site = await sdkDbContext.Sites.SingleOrDefaultAsync(x =>
                    x.Name == workflowCase.SolvedBy);

                await GenerateReportAndSendEmail(site.LanguageId, site.Name, message.CaseId);
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

        private async Task GenerateReportAndSendEmail(int languageId, string userName, int caseId)
        {
            var emailRecipient = await _baseDbContext.EmailRecipients.SingleOrDefaultAsync(x => x.Name == userName);
            var caseDto = await _sdkCore.CaseLookupCaseId(caseId);
            await using MicrotingDbContext sdkDbConetxt = _sdkCore.DbContextHelper.GetDbContext();
            Language language = await sdkDbConetxt.Languages.SingleOrDefaultAsync(x => x.Id == languageId);
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