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

        public EFormCompletedHandler(Core sdkCore, DbContextHelper dbContextHelper)
        {
            _sdkCore = sdkCore;
            _dbContext = dbContextHelper.GetDbContext();
        }
        public async Task Handle(eFormCompleted message)
        {
            Console.WriteLine("[INF] EFormCompletedHandler.Handle: called");

            try
            {
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

                if (message.CheckId == firstEformId)
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

                    if (fields!.Any())
                    {
                        if (!string.IsNullOrEmpty(fields[0]?.FieldValues[0]?.Value))
                        {
                            workflowCase.DateOfIncedent = DateTime.Parse(fields[0].FieldValues[0].Value);
                        }

                        if (!string.IsNullOrEmpty(fields[1]?.FieldValues[0]?.Value))
                        {
                            workflowCase.IncedentType = fields[1].FieldValues[0].Value;
                        }


                        if (!string.IsNullOrEmpty(fields[2]?.FieldValues[0]?.Value))
                        {
                            workflowCase.IncedentPlace = fields[2].FieldValues[0].Value;
                        }

                        workflowCase.PhotosExist = fields[3].FieldValues.Any();

                        if (!string.IsNullOrEmpty(fields[4]?.FieldValues[0]?.Value))
                        {
                            workflowCase.Description = fields[4].FieldValues[0].Value;
                        }
                    }

                    workflowCase.CreatedByUserId = replyElement.SiteMicrotingUuid;
                    workflowCase.UpdatedByUserId = replyElement.SiteMicrotingUuid;
                    await workflowCase.Create(_dbContext);
                }
                else if(message.CheckId == secondEformId)
                {
                    var workflowCase = await _dbContext.WorkflowCases
                        .Where(x => x.Status == "Ongoing" && x.ActionPlan == "")
                        .LastOrDefaultAsync();

                    var language = await sdkDbContext.Languages
                        .SingleAsync(x => x.LanguageCode == LocaleNames.Danish);
                    var replyElement = await _sdkCore.CaseRead(message.MicrotingId, message.CheckUId, language);
                    var checkListValue = replyElement.ElementList[0] as CheckListValue;
                    var fields = checkListValue?.DataItemList.Select(di => di as Field).ToList();


                    if (fields!.Any())
                    {

                        if (!string.IsNullOrEmpty(fields[0]?.FieldValues[0]?.Value))
                        {
                            workflowCase.DateOfIncedent = DateTime.Parse(fields[0].FieldValues[0].Value);
                        }

                        if (!string.IsNullOrEmpty(fields[2]?.FieldValues[0]?.Value))
                        {
                            workflowCase.IncedentPlace = fields[2].FieldValues[0].Value;
                        }

                        workflowCase.PhotosExist = fields[3].FieldValues.Any();

                        if (!string.IsNullOrEmpty(fields[4]?.FieldValues[0]?.Value))
                        {
                            workflowCase.Description = fields[4].FieldValues[0].Value;
                        }

                        if (!string.IsNullOrEmpty(fields[5]?.FieldValues[0]?.Value))
                        {
                            workflowCase.Deadline = DateTime.Parse(fields[5].FieldValues[0].Value);
                        }

                        if (!string.IsNullOrEmpty(fields[6]?.FieldValues[0]?.Value))
                        {
                            workflowCase.ActionPlan = fields[6].FieldValues[0].Value;
                        }

                        if (!string.IsNullOrEmpty(fields[8]?.FieldValues[0]?.Value))
                        {
                            workflowCase.Status = fields[8].FieldValues[0].Value;
                        }

                        var doneBy = sdkDbContext.Workers
                            .Single(x => x.Id == replyElement.DoneById).full_name();

                        workflowCase.SolvedBy = doneBy;

                        await workflowCase.Update(_dbContext);
                        await _sdkCore.CaseDelete(message.MicrotingId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERR] ServiceWorkOrdersPlugin.CaseCompleted: Got the following error: {ex.Message}");
            }
        }
    }
}
