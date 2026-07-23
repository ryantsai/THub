using Microsoft.Extensions.DependencyInjection;
using THub.Application.Alerts;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Application.Publications;
using THub.Application.Scheduling;
using THub.Application.Security;
using THub.Application.Workflows;
using THub.Application.Workflows.Management;

namespace THub.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddWebApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddWorkflowDefinitionServices(services);
        services.AddSingleton<ConnectionConfigurationSerializer>();
        services.AddScoped<ConnectionManagementService>();
        services.AddScoped<WorkflowSchemaInspectionService>();
        services.AddSingleton<PublicationTokenGenerator>();
        services.AddScoped<PublicationCatalogService>();
        services.AddScoped<PublicationTokenService>();
        services.AddScoped<PublicationAuthorizationService>();
        services.AddScoped<PublicationDataService>();
        services.AddScoped<PublicationEditorService>();
        services.AddScoped<PublicationChangeSetManagementService>();
        services.AddScoped<PublicationGrantManagementService>();
        services.AddScoped<PublicationSourceInspectionService>();
        services.AddScoped<WorkflowCatalogService>();
        services.AddScoped<WorkflowPackageService>();
        services.AddScoped<WorkflowRunService>();
        services.AddScoped<WorkflowRunHistoryService>();
        services.AddScoped<EmailAlertAdministrationService>();
        services.AddScoped<EmailAlertMonitoringService>();
        services.AddScoped<WorkflowTerminalAlertService>();
        services.AddScoped<AccessControlAdministrationService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    public static IServiceCollection AddWorkerApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddWorkflowDefinitionServices(services);
        services.AddSingleton<ConnectionConfigurationSerializer>();
        services.AddSingleton<WorkflowExecutionPlanner>();
        services.AddSingleton<IWorkflowExecutionPreflightValidator, WorkflowExecutionPreflightValidator>();
        services.AddSingleton<ITabularDataSetStore, InMemoryTabularDataSetStore>();
        services.AddSingleton<INodeRetryPolicyProvider, DefaultNodeRetryPolicyProvider>();
        services.AddSingleton<IWorkflowRetryScheduler, ExponentialJitterRetryScheduler>();
        services.AddSingleton<IExecutionErrorClassifier, DefaultExecutionErrorClassifier>();
        services.AddSingleton<IWorkflowVariableResolver, WorkflowVariableResolver>();
        services.AddSingleton<WorkflowExecutionTimeoutOptions>();
        services.AddSingleton<INodeExecutionTimeoutProvider, DefaultNodeExecutionTimeoutProvider>();
        services.AddScoped<EmailActionOutboxService>();
        services.AddScoped<AlertDeliveryDispatcher>();
        services.AddScoped<WorkflowTerminalAlertService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    public static IServiceCollection AddPublicationApiApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ConnectionConfigurationSerializer>();
        services.AddSingleton<PublicationTokenGenerator>();
        services.AddScoped<PublicationCatalogService>();
        services.AddScoped<PublicationTokenService>();
        services.AddScoped<PublicationDataService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    private static void AddWorkflowDefinitionServices(IServiceCollection services)
    {
        services.AddSingleton<WorkflowGraphValidator>();
        services.AddSingleton<WorkflowGraphSerializer>();
        services.AddSingleton<ScheduleCalculator>();
        services.AddSingleton<WorkflowNodeSettingsValidator>();
    }
}
