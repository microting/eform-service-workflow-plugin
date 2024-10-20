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

using Microting.EformAngularFrontendBase.Infrastructure.Data;
using Microting.EformAngularFrontendBase.Infrastructure.Data.Factories;
using Microting.eFormWorkflowBase.Helpers;
using Sentry;

namespace ServiceWorkflowPlugin;

using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using Infrastructure.Helpers;
using Installers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Dto;
using Microting.eFormWorkflowBase.Infrastructure.Data;
using Microting.eFormWorkflowBase.Infrastructure.Data.Factories;
using Microting.WindowsService.BasePn;
using Rebus.Bus;
using ServiceWorkflowPlugin.Infrastructure;

[Export(typeof(ISdkEventHandler))]
public class Core : ISdkEventHandler
{
    private eFormCore.Core _sdkCore;
    private IWindsorContainer _container;
    private IBus _bus;
    private bool _coreThreadRunning = false;
    private bool _coreStatChanging;
    private bool _coreAvailable;
    private string _serviceLocation;
    private static int _maxParallelism = 1;
    private static int _numberOfWorkers = 1;
    private WorkflowPnDbContext _dbContext;
    private DbContextHelper _dbContextHelper;
    private BaseDbContext _baseDbContext;

    public void CoreEventException(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void UnitActivated(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void eFormProcessed(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void eFormProcessingError(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void eFormRetrived(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void CaseCompleted(object sender, EventArgs args)
    {
        try
        {
            var trigger = (CaseDto)sender;

            if (trigger.MicrotingUId != null && trigger.CheckUId != null)
            {
                _bus.SendLocal(new eFormCompleted(
                    trigger.CaseId,
                    (int)trigger.MicrotingUId,
                    trigger.CheckListId,
                    (int)trigger.CheckUId,
                    trigger.SiteUId)
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR] ServiceWorkOrdersPlugin.CaseCompleted: Got the following error: {ex.Message}");
        }
    }

    public void CaseDeleted(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void NotificationNotFound(object sender, EventArgs args)
    {
        // Do nothing
    }

    public bool Start(string sdkConnectionString, string serviceLocation)
    {
        SentrySdk.Init(options =>
        {
            // A Sentry Data Source Name (DSN) is required.
            // See https://docs.sentry.io/product/sentry-basics/dsn-explainer/
            // You can set it in the SENTRY_DSN environment variable, or you can set it in code here.
            options.Dsn = "https://05d2b8b57f4a14f8d6a49fa33daed9b7@o4506241219428352.ingest.us.sentry.io/4508153867927552";

            // When debug is enabled, the Sentry client will emit detailed debugging information to the console.
            // This might be helpful, or might interfere with the normal operation of your application.
            // We enable it here for demonstration purposes when first trying Sentry.
            // You shouldn't do this in your applications unless you're troubleshooting issues with Sentry.
            options.Debug = false;

            // This option is recommended. It enables Sentry's "Release Health" feature.
            options.AutoSessionTracking = true;

            // Enabling this option is recommended for client applications only. It ensures all threads use the same global scope.
            options.IsGlobalModeEnabled = false;

            // Example sample rate for your transactions: captures 10% of transactions
            options.TracesSampleRate = 0.1;
        });
        Console.WriteLine("ServiceWorkflowPlugin start called");
        try
        {
            var dbNameSection = Regex.Match(sdkConnectionString, @"(Database=\w*;)").Groups[0].Value;
            var dbPrefix = Regex.Match(sdkConnectionString, @"Database=(\d*)_").Groups[1].Value;

            var pluginDbName = $"Database={dbPrefix}_eform-angular-workflow-plugin;";
            var angularDbName = $"Database={dbPrefix}_Angular;";
            var connectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);
            var angularConnectionString = sdkConnectionString.Replace(dbNameSection, angularDbName);
            _baseDbContext = new BaseDbContextFactory().CreateDbContext(new []{angularConnectionString});

            if (!_coreAvailable && !_coreStatChanging)
            {
                _serviceLocation = serviceLocation;
                _coreStatChanging = true;

                if (string.IsNullOrEmpty(_serviceLocation))
                    throw new ArgumentException("serviceLocation is not allowed to be null or empty");

                if (string.IsNullOrEmpty(connectionString))
                    throw new ArgumentException("serverConnectionString is not allowed to be null or empty");

                var contextFactory = new WorkflowPnContextFactory();

                _dbContext = contextFactory.CreateDbContext(new[] { connectionString });
                _dbContext.Database.Migrate();

                _dbContextHelper = new DbContextHelper(connectionString);

                _coreAvailable = true;
                _coreStatChanging = false;

                StartSdkCoreSqlOnly(sdkConnectionString);
                Console.WriteLine($"Connection string: {sdkConnectionString}");

                var rabbitmqHost = _sdkCore.GetSdkSetting(Settings.rabbitMqHost).GetAwaiter().GetResult();
                Console.WriteLine($"rabbitmqHost: {rabbitmqHost}");
                var rabbitMqUser = _sdkCore.GetSdkSetting(Settings.rabbitMqUser).GetAwaiter().GetResult();
                Console.WriteLine($"rabbitMqUser: {rabbitMqUser}");
                var rabbitMqPassword = _sdkCore.GetSdkSetting(Settings.rabbitMqPassword).GetAwaiter().GetResult();
                Console.WriteLine($"rabbitMqPassword: {rabbitMqPassword}");

                var temp = _dbContext.PluginConfigurationValues
                    .SingleOrDefault(x => x.Name == "WorkflowBaseSettings:MaxParallelism")?.Value;
                _maxParallelism = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);

                temp = _dbContext.PluginConfigurationValues
                    .SingleOrDefault(x => x.Name == "WorkflowBaseSettings:NumberOfWorkers")?.Value;
                _numberOfWorkers = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);

                var reportHelper = new WorkflowReportHelper(_sdkCore, _dbContextHelper.GetDbContext());

                _container = new WindsorContainer();
                _container.Register(Component.For<IWindsorContainer>().Instance(_container));
                _container.Register(Component.For<DbContextHelper>().Instance(_dbContextHelper));
                _container.Register(Component.For<eFormCore.Core>().Instance(_sdkCore));
                _container.Register(Component.For<BaseDbContext>().Instance(_baseDbContext));
                _container.Register(Component.For<WorkflowReportHelper>().Instance(reportHelper));

                var emailHelper = new EmailHelper(_sdkCore, _dbContextHelper, _baseDbContext, reportHelper);

                _container.Register(Component.For<EmailHelper>().Instance(emailHelper));
                _container.Install(
                    new RebusHandlerInstaller()
                    , new RebusInstaller(dbPrefix, connectionString, _maxParallelism, _numberOfWorkers, rabbitMqUser, rabbitMqPassword, rabbitmqHost)
                );

                _bus = _container.Resolve<IBus>();
            }
            Console.WriteLine("ServiceWorkflowPlugin started");
            return true;
        }
        catch (Exception ex)
        {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine("Start failed {0}", ex.Message);
            Console.ForegroundColor = oldColor;
            throw;
        }
    }

    public bool Stop(bool shutdownReallyFast)
    {
        if (_coreAvailable && !_coreStatChanging)
        {
            _coreStatChanging = true;

            _coreAvailable = false;

            while (_coreThreadRunning)
            {
                Thread.Sleep(100);
                _bus.Dispose();
            }
            _sdkCore.Close().GetAwaiter().GetResult();

            _coreStatChanging = false;
        }
        return true;
    }

    public bool Restart(int sameExceptionCount, int sameExceptionCountMax, bool shutdownReallyFast)
    {
        return true;
    }

    private void StartSdkCoreSqlOnly(string sdkConnectionString)
    {
        _sdkCore = new eFormCore.Core();

        _sdkCore.StartSqlOnly(sdkConnectionString).GetAwaiter().GetResult();
    }
}