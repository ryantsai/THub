using Quartz;
using Serilog;
using THub.Application;
using THub.Infrastructure;
using THub.Worker;
using THub.Worker.Scheduling;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "THub Orchestration Worker");
    SerilogConfiguration.Configure(builder);
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddOptions<SchedulerOptions>()
        .Bind(builder.Configuration.GetSection(SchedulerOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

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
