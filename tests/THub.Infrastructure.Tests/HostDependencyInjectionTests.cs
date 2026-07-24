using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using THub.Application;
using THub.Application.Alerts;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Application.Publications;
using THub.Application.Scheduling;
using THub.Application.Workflows.Management;
using THub.Infrastructure.Alerts;

namespace THub.Infrastructure.Tests;

public sealed class HostDependencyInjectionTests
{
    [Fact]
    public void PublicationApiProfile_RegistersOnlyReadAndMeteringCapabilities()
    {
        var services = new ServiceCollection();
        services.AddPublicationApiApplication();
        services.AddPublicationApiInfrastructure(CreateConfiguration());

        AssertRegistered<PublicationCatalogService>(services);
        AssertRegistered<PublicationTokenService>(services);
        AssertRegistered<PublicationDataService>(services);
        AssertRegistered<IPublicationCatalogStore>(services);
        AssertRegistered<IPublicationConnectionPolicy>(services);
        AssertRegistered<IPublicationTokenStore>(services);
        AssertRegistered<IPublicationSourceDataReader>(services);
        AssertRegistered<IPublicationSourceSchemaInspector>(services);

        AssertNotRegistered<PublicationAuthorizationService>(services);
        AssertNotRegistered<PublicationEditorService>(services);
        AssertNotRegistered<PublicationChangeSetManagementService>(services);
        AssertNotRegistered<PublicationGrantManagementService>(services);
        AssertNotRegistered<IPublicationGrantStore>(services);
        AssertNotRegistered<IPublicationGrantManagementStore>(services);
        AssertNotRegistered<IPublicationChangeSetStore>(services);
        AssertNotRegistered<IPublicationChangeSetQueryStore>(services);
        AssertNotRegistered<IPublicationChangeSetClaimStore>(services);
        AssertNotRegistered<IPublicationChangeSetProcessor>(services);
        AssertNotRegistered<IWorkflowNodeExecutor>(services);
        AssertNotRegistered<IWorkflowRunExecutionStore>(services);
        AssertNotRegistered<IWorkflowManagementRepository>(services);
        AssertNotRegistered<IWorkflowScheduleSource>(services);
        AssertNotRegistered<IAlertSender>(services);
        AssertNotRegistered<IAlertDeliveryStore>(services);
        AssertNotRegistered<ConnectionManagementService>(services);
        AssertNotRegistered<EmailAlertAdministrationService>(services);
    }

    [Fact]
    public void WebProfile_RegistersManagementAndEditorWithoutBackgroundExecution()
    {
        var services = new ServiceCollection();
        services.AddWebApplication();
        services.AddWebInfrastructure(CreateConfiguration());

        AssertRegistered<WorkflowCatalogService>(services);
        AssertRegistered<WorkflowRunService>(services);
        AssertRegistered<ConnectionManagementService>(services);
        AssertRegistered<EmailAlertAdministrationService>(services);
        AssertRegistered<PublicationEditorService>(services);
        AssertRegistered<PublicationGrantManagementService>(services);
        AssertRegistered<IWorkflowManagementRepository>(services);
        AssertRegistered<IPublicationChangeSetStore>(services);
        AssertRegistered<IPublicationSourceDataReader>(services);

        AssertNotRegistered<IWorkflowNodeExecutor>(services);
        AssertNotRegistered<IWorkflowRunExecutionStore>(services);
        AssertNotRegistered<IWorkflowExecutionEventSinkFactory>(services);
        AssertNotRegistered<IWorkflowScheduleSource>(services);
        AssertNotRegistered<IScheduledWorkflowRunEnqueuer>(services);
        AssertNotRegistered<IAlertSender>(services);
        AssertNotRegistered<IAlertDeliveryStore>(services);
        AssertNotRegistered<IPublicationChangeSetClaimStore>(services);
        AssertNotRegistered<IPublicationChangeSetProcessor>(services);
        AssertNotRegistered<AlertDeliveryDispatcher>(services);
    }

    [Fact]
    public void WorkerProfile_RegistersExecutionDeliveryAndApplyWithoutManagement()
    {
        var services = new ServiceCollection();
        services.AddWorkerApplication();
        services.AddWorkerInfrastructure(CreateConfiguration());

        AssertRegistered<WorkflowExecutionPlanner>(services);
        AssertRegistered<IWorkflowNodeExecutor>(services);
        AssertRegistered<IWorkflowRunExecutionStore>(services);
        AssertRegistered<IWorkflowScheduleSource>(services);
        AssertRegistered<IScheduledWorkflowRunEnqueuer>(services);
        AssertRegistered<IAlertSender>(services);
        AssertRegistered<IAlertDeliveryStore>(services);
        AssertRegistered<AlertDeliveryDispatcher>(services);
        AssertRegistered<IPublicationChangeSetClaimStore>(services);
        AssertRegistered<IPublicationChangeSetProcessor>(services);
        AssertRegistered<IDataConnectionStore>(services);
        AssertRegistered<IPublicationConnectionPolicy>(services);

        AssertNotRegistered<ConnectionManagementService>(services);
        AssertNotRegistered<IDataConnectionProbe>(services);
        AssertNotRegistered<WorkflowCatalogService>(services);
        AssertNotRegistered<WorkflowRunService>(services);
        AssertNotRegistered<IWorkflowManagementRepository>(services);
        AssertNotRegistered<IWorkflowRunHistoryStore>(services);
        AssertNotRegistered<EmailAlertAdministrationService>(services);
        AssertNotRegistered<EmailAlertMonitoringService>(services);
        AssertNotRegistered<PublicationCatalogService>(services);
        AssertNotRegistered<PublicationTokenService>(services);
        AssertNotRegistered<PublicationEditorService>(services);
        AssertNotRegistered<IPublicationCatalogStore>(services);
        AssertNotRegistered<IPublicationTokenStore>(services);
        AssertNotRegistered<IPublicationGrantStore>(services);
        AssertNotRegistered<IPublicationChangeSetStore>(services);
        AssertNotRegistered<IPublicationChangeSetQueryStore>(services);
        AssertNotRegistered<IPublicationSourceDataReader>(services);
    }

    private static IConfiguration CreateConfiguration()
        => new TestConfiguration(
            "Server=127.0.0.1,1;Database=THub_DI_Composition_Only;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=1");

    private static void AssertRegistered<TService>(IServiceCollection services) =>
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TService));

    private static void AssertNotRegistered<TService>(IServiceCollection services) =>
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TService));

    private sealed class TestConfiguration(string connectionString) : IConfiguration
    {
        public string? this[string key]
        {
            get => string.Equals(key, "ConnectionStrings:THub", StringComparison.Ordinal)
                ? connectionString
                : null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => StaticChangeToken.Instance;

        public IConfigurationSection GetSection(string key) => new TestConfigurationSection(this, key);
    }

    private sealed class TestConfigurationSection(
        TestConfiguration root,
        string path) : IConfigurationSection
    {
        public string? this[string key]
        {
            get => root[$"{Path}:{key}"];
            set => throw new NotSupportedException();
        }

        public string Key { get; } = path.Split(':')[^1];

        public string Path { get; } = path;

        public string? Value
        {
            get => root[Path];
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => StaticChangeToken.Instance;

        public IConfigurationSection GetSection(string key) =>
            new TestConfigurationSection(root, $"{Path}:{key}");
    }

    private sealed class StaticChangeToken : IChangeToken
    {
        public static StaticChangeToken Instance { get; } = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
