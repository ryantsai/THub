using Quartz;
using Serilog;
using THub.Application;
using THub.Infrastructure;
using THub.Infrastructure.Alerts;
using THub.Worker;
using THub.Worker.Alerts;
using THub.Worker.Execution;
using THub.Worker.Publications;
using THub.Worker.Scheduling;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "THub Orchestration Worker");
    SerilogConfiguration.Configure(builder);
    builder.Services.AddWorkerApplication();
    builder.Services.AddWorkerInfrastructure(builder.Configuration);
    builder.Services.AddOptions<SchedulerOptions>()
        .Bind(builder.Configuration.GetSection(SchedulerOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddOptions<WorkflowExecutionWorkerOptions>()
        .Bind(builder.Configuration.GetSection(WorkflowExecutionWorkerOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(
            options =>
            {
                try
                {
                    options.ValidateCrossFieldBounds();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            },
            "Execution options contain inconsistent resource, lease, or timeout bounds.")
        .ValidateOnStart();
    builder.Services.AddScoped<WorkflowExecutionEngineFactory>();
    builder.Services.AddHostedService<WorkflowExecutionWorker>();
    builder.Services.AddOptions<PublicationChangeSetWorkerOptions>()
        .Bind(builder.Configuration.GetSection(PublicationChangeSetWorkerOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddHostedService<PublicationChangeSetWorker>();
    builder.Services.AddOptions<EmailAlertDispatchWorkerOptions>()
        .Bind(builder.Configuration.GetSection(EmailAlertDispatchWorkerOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(
            options =>
            {
                try
                {
                    var smtpTimeoutSeconds = builder.Configuration.GetValue<int?>(
                        "EmailDelivery:Smtp:OperationTimeoutSeconds")
                        ?? new SmtpAlertSenderOptions().OperationTimeoutSeconds;
                    options.ValidateCrossFieldBounds(smtpTimeoutSeconds);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
                catch (OverflowException)
                {
                    return false;
                }
            },
            "Email dispatcher options contain inconsistent retry, lease, or timeout bounds.")
        .ValidateOnStart();
    builder.Services.AddHostedService<EmailAlertDispatchWorker>();

    var schedulerOptions = builder.Configuration
        .GetSection(SchedulerOptions.SectionName)
        .Get<SchedulerOptions>() ?? new SchedulerOptions();
    var connectionString = builder.Configuration.GetConnectionString("THub")
        ?? throw new InvalidOperationException("Connection string 'THub' is not configured.");

    builder.Services.AddQuartz(quartz =>
    {
        quartz.SchedulerName = "THub";
        quartz.SchedulerId = "AUTO";
        quartz.UseDefaultThreadPool(pool => pool.MaxConcurrency = schedulerOptions.MaxConcurrency);
        quartz.UsePersistentStore(store =>
        {
            store.PerformSchemaValidation = true;
            store.UseProperties = true;
            store.RetryInterval = TimeSpan.FromSeconds(schedulerOptions.DatabaseRetryIntervalSeconds);
            store.UseSqlServer(sqlServer =>
            {
                sqlServer.ConnectionString = connectionString;
                sqlServer.TablePrefix = "[quartz].QRTZ_";
            });
            store.UseSystemTextJsonSerializer();
            store.UseClustering(clustering =>
            {
                clustering.CheckinInterval = TimeSpan.FromSeconds(
                    schedulerOptions.ClusterCheckinIntervalSeconds);
                clustering.CheckinMisfireThreshold = TimeSpan.FromSeconds(
                    schedulerOptions.ClusterCheckinMisfireThresholdSeconds);
            });
        });

        quartz.AddJob<WorkflowScheduleReconciliationJob>(job =>
            job.WithIdentity(QuartzWorkflowKeys.ReconciliationJob));
        quartz.AddTrigger(trigger => trigger
            .WithIdentity("reconcile-workflow-schedules", QuartzWorkflowKeys.ReconciliationGroup)
            .ForJob(QuartzWorkflowKeys.ReconciliationJob)
            .StartNow()
            .WithSimpleSchedule(schedule => schedule
                .WithIntervalInSeconds(schedulerOptions.ReconciliationIntervalSeconds)
                .RepeatForever()
                .WithMisfireHandlingInstructionNextWithExistingCount()));
    });
    builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

    var host = builder.Build();
    host.Run();
}
catch (Exception exception)
{
    Log.Fatal(exception, "THub.Worker terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
