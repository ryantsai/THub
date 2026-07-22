using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using THub.Application.Alerts;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Application.Scheduling;
using THub.Application.Publications;
using THub.Application.Workflows.Management;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Alerts;
using THub.Infrastructure.Execution;
using THub.Infrastructure.Files;
using THub.Infrastructure.Persistence;
using THub.Infrastructure.Publications;
using THub.Infrastructure.Scheduling;
using THub.Infrastructure.Workflows;

namespace THub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddWebInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddControlPlanePersistence(services, configuration);

        services.AddSingleton<ApprovedPathResolver>();
        services.AddSingleton<IDataConnectionStore, SqlDataConnectionStore>();
        services.AddSingleton<IDataConnectionProbe, DataConnectionProbe>();
        services.AddScoped<IWorkflowManagementRepository, SqlWorkflowManagementRepository>();
        services.AddScoped<IWorkflowRunHistoryStore, SqlWorkflowRunHistoryStore>();
        services.AddSingleton<IEmailAlertAdministrationStore, SqlEmailAlertAdministrationStore>();
        services.AddSingleton<IEmailAlertDeliveryQueryStore, SqlEmailAlertDeliveryQueryStore>();
        services.AddSingleton<IWorkflowTerminalAlertStore, SqlWorkflowTerminalAlertStore>();
        services.AddScoped<IPublicationCatalogStore, SqlPublicationCatalogStore>();
        services.AddScoped<IPublicationTokenStore, SqlPublicationTokenStore>();
        services.AddScoped<IPublicationGrantStore, SqlPublicationGrantStore>();
        services.AddScoped<IPublicationGrantManagementStore, SqlPublicationGrantManagementStore>();
        services.AddScoped<IPublicationChangeSetStore, SqlPublicationChangeSetStore>();
        services.AddScoped<IPublicationChangeSetQueryStore, SqlPublicationChangeSetQueryStore>();
        services.AddScoped<IPublicationSourceDataReader, SqlPublicationSourceDataReader>();
        services.AddScoped<IPublicationSourceSchemaInspector, SqlPublicationSourceSchemaInspector>();
        return services;
    }

    public static IServiceCollection AddWorkerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddControlPlanePersistence(services, configuration);

        services.AddSingleton<IWorkflowScheduleSource, SqlWorkflowScheduleSource>();
        services.AddSingleton<IScheduledWorkflowRunEnqueuer, SqlScheduledWorkflowRunEnqueuer>();
        services.AddSingleton<ApprovedPathResolver>();
        services.AddSingleton<IWorkflowRunExecutionStore, SqlWorkflowRunExecutionStore>();
        services.AddSingleton<IWorkflowExecutionEventSinkFactory, SqlWorkflowExecutionEventSinkFactory>();
        services.AddSingleton<IWorkflowStepRunLocator, SqlWorkflowStepRunLocator>();
        services.AddSingleton<IWorkflowNodeResourceValidator, InfrastructureWorkflowNodeResourceValidator>();
        services.AddSingleton<IEmailAlertAdministrationStore, SqlEmailAlertAdministrationStore>();
        services.AddSingleton<IAlertDeliveryStore, SqlAlertDeliveryStore>();
        services.AddSingleton<IWorkflowTerminalAlertStore, SqlWorkflowTerminalAlertStore>();
        ConfigureSmtpDelivery(services, configuration);
        services.AddSingleton<ExecutionConnectionResolver>();
        services.AddScoped<IWorkflowNodeExecutor, SqlSourceNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, CsvSourceNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, ExcelSourceNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, SelectColumnsNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, FilterRowsNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, JoinNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, SqlTargetNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, CsvTargetNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, ExcelTargetNodeExecutor>();
        services.AddScoped<IWorkflowNodeExecutor, EmailAlertNodeExecutor>();
        services.AddScoped<IPublicationChangeSetClaimStore, SqlPublicationChangeSetClaimStore>();
        services.AddScoped<IPublicationChangeSetProcessor, SqlPublicationChangeSetProcessor>();
        services.AddScoped<IPublicationSourceSchemaInspector, SqlPublicationSourceSchemaInspector>();
        return services;
    }

    public static IServiceCollection AddPublicationApiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddControlPlanePersistence(services, configuration);

        services.AddScoped<IPublicationCatalogStore, SqlPublicationCatalogStore>();
        services.AddScoped<IPublicationTokenStore, SqlPublicationTokenStore>();
        services.AddScoped<IPublicationSourceDataReader, SqlPublicationSourceDataReader>();
        services.AddScoped<IPublicationSourceSchemaInspector, SqlPublicationSourceSchemaInspector>();
        return services;
    }

    private static void AddControlPlanePersistence(
        IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("THub")
            ?? throw new InvalidOperationException("Connection string 'THub' is not configured.");

        services.AddPooledDbContextFactory<THubDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.EnableRetryOnFailure(maxRetryCount: 5)));
    }

    private static void ConfigureSmtpDelivery(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var smtpOptions = new SmtpAlertSenderOptions();
        var smtpTimeout = configuration["EmailDelivery:Smtp:OperationTimeoutSeconds"];
        if (smtpTimeout is not null)
        {
            if (!int.TryParse(
                    smtpTimeout,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedTimeout))
            {
                throw new InvalidOperationException(
                    "EmailDelivery:Smtp:OperationTimeoutSeconds must be an integer.");
            }

            smtpOptions.OperationTimeoutSeconds = parsedTimeout;
        }
        _ = smtpOptions.GetValidatedOperationTimeout();
        services.AddSingleton(smtpOptions);
        services.AddSingleton<ISecretResolver, UnavailableSmtpSecretResolver>();
        services.AddScoped<IAlertSender, MailKitAlertSender>();
    }
}
