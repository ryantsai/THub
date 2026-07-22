using THub.Application.Alerts;
using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Application.Tests;

public sealed class EmailAlertApplicationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AdministrationCreatesSecretReferenceOnlyProfileAndRejectsUnsafeRule()
    {
        var store = new AdministrationStore { WorkflowExists = true };
        var service = new EmailAlertAdministrationService(store, new FixedTimeProvider(Now));
        var profileResult = await service.CreateProfileAsync(
            new CreateEmailDeliveryProfileCommand(
                "Relay",
                "smtp.example.com",
                587,
                EmailTransportSecurity.StartTlsRequired,
                "thub@example.com",
                ["example.com"],
                "DOMAIN\\admin",
                "smtp/production",
                new EmailDeliveryLimits(5, 200, 2_000, 1)),
            CancellationToken.None);

        Assert.True(profileResult.IsSuccess);
        Assert.Equal("smtp/production", profileResult.Value!.CredentialSecretReference);
        Assert.DoesNotContain("password", profileResult.Value.CredentialSecretReference, StringComparison.OrdinalIgnoreCase);

        var ruleResult = await service.CreateRuleAsync(
            new CreateWorkflowAlertRuleCommand(
                Guid.NewGuid(),
                profileResult.Value.Id,
                "Failures",
                WorkflowAlertTriggers.RunFailed,
                ["ops@outside.test"],
                "{{workflow.name}} failed",
                "{{error.summary}}",
                "DOMAIN\\admin"),
            CancellationToken.None);

        Assert.Equal(AlertResultStatus.ValidationFailed, ruleResult.Status);
        Assert.Empty(store.Rules);
    }

    [Fact]
    public async Task AdministrationValidatesWorstCaseRenderedTemplateAgainstProfileBounds()
    {
        var profile = CreateProfile(new EmailDeliveryLimits(5, 100, 20, 1));
        var store = new AdministrationStore
        {
            WorkflowExists = true,
            Profiles = { profile }
        };
        var service = new EmailAlertAdministrationService(store, new FixedTimeProvider(Now));

        var result = await service.CreateRuleAsync(
            new CreateWorkflowAlertRuleCommand(
                Guid.NewGuid(),
                profile.Id,
                "Failures",
                WorkflowAlertTriggers.RunFailed,
                ["ops@example.com"],
                "Run failed",
                "{{error.summary}}",
                "DOMAIN\\admin"),
            CancellationToken.None);

        Assert.Equal(AlertResultStatus.ValidationFailed, result.Status);
        Assert.Empty(store.Rules);
    }

    [Fact]
    public async Task EmailActionQueuesDurableIntentAndTreatsDuplicateAsSuccess()
    {
        var profile = CreateProfile();
        var deliveryStore = new DeliveryStore(profile);
        var service = new EmailActionOutboxService(
            deliveryStore,
            new FixedTimeProvider(Now));
        var command = new QueueEmailActionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "notify-operations",
            profile.Id,
            ["ops@example.com"],
            "Run {{run.id}} failed",
            "Inspect {{error.summary}}",
            new Dictionary<string, string?>
            {
                ["run.id"] = "42",
                ["error.summary"] = "Timed out"
            });

        var first = await service.QueueAsync(command, CancellationToken.None);
        var second = await service.QueueAsync(
            command with { WorkflowStepRunId = Guid.NewGuid() },
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.False(first.Value!.AlreadyExisted);
        Assert.True(second.Value!.AlreadyExisted);
        Assert.Single(deliveryStore.Deliveries);
        Assert.Equal("Run 42 failed", deliveryStore.Deliveries[0].Message.Subject);
    }

    [Fact]
    public async Task DispatcherSchedulesOnlyTransientFailureAndPersistsTransition()
    {
        var profile = CreateProfile();
        var delivery = AlertDelivery.ForWorkflowRule(
            Guid.NewGuid(),
            Guid.NewGuid(),
            profile.Id,
            AlertDeliveryEvent.RunFailed,
            new EmailMessage(["ops@example.com"], "Failure", "body"),
            Now,
            maximumAttempts: 3);
        var store = new DeliveryStore(profile, delivery);
        var sender = new StubSender(AlertSendResult.Failure(new ExecutionError(
            "smtp.unavailable",
            ExecutionErrorCategory.Connectivity,
            "The relay is unavailable.",
            isRetryable: true)));
        var dispatcher = new AlertDeliveryDispatcher(store, sender, new FixedTimeProvider(Now));

        var result = await dispatcher.DispatchBatchAsync(
            "worker-a",
            new AlertDispatchOptions(
                maximumDeliveriesPerBatch: 1,
                leaseDuration: TimeSpan.FromMinutes(1),
                new AlertRetryPolicy(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(1),
                    jitterRatio: 0)),
            CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.RetryScheduled);
        Assert.Equal(AlertDeliveryStatus.RetryScheduled, delivery.Status);
        Assert.Equal(Now.AddSeconds(30), delivery.NextAttemptAtUtc);
        Assert.Null(delivery.LeaseOwner);
    }

    [Fact]
    public async Task DispatcherPersistsAcceptedSendEvenWhenHostCancellationArrivesAfterAcceptance()
    {
        var profile = CreateProfile();
        var delivery = AlertDelivery.ForWorkflowRule(
            Guid.NewGuid(),
            Guid.NewGuid(),
            profile.Id,
            AlertDeliveryEvent.RunFailed,
            new EmailMessage(["ops@example.com"], "Failure", "body"),
            Now);
        var store = new DeliveryStore(profile, delivery);
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new AlertDeliveryDispatcher(
            store,
            new CancellingSuccessSender(cancellation),
            new FixedTimeProvider(Now));

        var result = await dispatcher.DispatchBatchAsync(
            "worker-a",
            new AlertDispatchOptions(
                maximumDeliveriesPerBatch: 1,
                leaseDuration: TimeSpan.FromMinutes(1)),
            cancellation.Token);

        Assert.Equal(1, result.Delivered);
        Assert.Equal(AlertDeliveryStatus.Delivered, delivery.Status);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task TerminalServicePreparesRenderedRuleAndCommitsWithRunAtomically()
    {
        var workflowId = Guid.NewGuid();
        var profile = CreateProfile();
        var rule = new WorkflowAlertRule(
            workflowId,
            profile.Id,
            "Failures",
            WorkflowAlertTriggers.RunFailed,
            ["ops@example.com"],
            new EmailTemplate("{{workflow.name}} failed", "{{error.summary}}"),
            "DOMAIN\\admin",
            Now);
        var run = new WorkflowRun(
            workflowId,
            Guid.NewGuid(),
            1,
            "DOMAIN\\operator",
            Now);
        Assert.True(run.TryClaim("worker-a", Now, TimeSpan.FromMinutes(5)));
        run.CompleteFailed(
            "worker-a",
            new ExecutionError(
                "source.timeout",
                ExecutionErrorCategory.Timeout,
                "The source timed out.",
                isRetryable: true),
            Now.AddMinutes(1));
        var store = new TerminalStore(new TerminalAlertPreparation(
            "Nightly import",
            [new TerminalAlertRuleSnapshot(rule, profile)]));
        var service = new WorkflowTerminalAlertService(store, new FixedTimeProvider(Now.AddMinutes(1)));

        var result = await service.CommitAsync(
            new CommitTerminalRunWithAlertsCommand(
                run,
                WorkflowRunStatus.Running,
                "worker-a"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(store.PreparedAlerts);
        Assert.Equal("Nightly import failed", store.PreparedAlerts[0].Delivery.Message.Subject);
        Assert.Equal("The source timed out.", store.PreparedAlerts[0].Delivery.Message.Body);
        Assert.Equal(AlertDeliveryStatus.Pending, store.PreparedAlerts[0].Delivery.Status);
    }

    [Fact]
    public async Task MonitoringValidatesBoundsAndForwardsSafeFilters()
    {
        var profileId = Guid.NewGuid();
        var queryStore = new DeliveryQueryStore(totalCount: 7);
        var service = new EmailAlertMonitoringService(queryStore);

        var result = await service.ListAsync(
            new EmailAlertDeliveryListRequest(
                Offset: 5,
                Limit: 25,
                Status: AlertDeliveryStatus.DeadLettered,
                EmailDeliveryProfileId: profileId),
            CancellationToken.None);
        var invalid = await service.ListAsync(
            new EmailAlertDeliveryListRequest(Limit: 201),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value!.TotalCount);
        Assert.Equal(AlertDeliveryStatus.DeadLettered, queryStore.Filter!.Status);
        Assert.Equal(profileId, queryStore.Filter.EmailDeliveryProfileId);
        Assert.Equal(AlertResultStatus.ValidationFailed, invalid.Status);
    }

    private static EmailDeliveryProfile CreateProfile(EmailDeliveryLimits? limits = null) => new(
        "Relay",
        "smtp.example.com",
        587,
        EmailTransportSecurity.StartTlsRequired,
        "thub@example.com",
        ["example.com"],
        "DOMAIN\\admin",
        Now,
        "smtp/production",
        limits);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AdministrationStore : IEmailAlertAdministrationStore
    {
        public List<EmailDeliveryProfile> Profiles { get; init; } = [];
        public List<WorkflowAlertRule> Rules { get; } = [];
        public bool WorkflowExists { get; init; }

        public Task<IReadOnlyList<EmailDeliveryProfile>> ListProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EmailDeliveryProfile>>(Profiles);

        public Task<EmailDeliveryProfile?> FindProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
            Task.FromResult(Profiles.SingleOrDefault(profile => profile.Id == profileId));

        public Task<EmailAlertAdministrationWriteStatus> AddProfileAsync(
            EmailDeliveryProfile profile,
            CancellationToken cancellationToken)
        {
            Profiles.Add(profile);
            return Task.FromResult(EmailAlertAdministrationWriteStatus.Saved);
        }

        public Task<EmailAlertAdministrationWriteStatus> SaveProfileAsync(
            EmailDeliveryProfile profile,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmailAlertAdministrationWriteStatus.Saved);

        public Task<bool> WorkflowExistsAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowExists);

        public Task<IReadOnlyList<WorkflowAlertRule>> ListRulesAsync(
            Guid workflowId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowAlertRule>>(
                Rules.Where(rule => rule.WorkflowId == workflowId).ToArray());

        public Task<IReadOnlyList<WorkflowAlertRule>> ListRulesForProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowAlertRule>>(
                Rules.Where(rule => rule.EmailDeliveryProfileId == profileId).ToArray());

        public Task<WorkflowAlertRule?> FindRuleAsync(Guid ruleId, CancellationToken cancellationToken) =>
            Task.FromResult(Rules.SingleOrDefault(rule => rule.Id == ruleId));

        public Task<EmailAlertAdministrationWriteStatus> AddRuleAsync(
            WorkflowAlertRule rule,
            CancellationToken cancellationToken)
        {
            Rules.Add(rule);
            return Task.FromResult(EmailAlertAdministrationWriteStatus.Saved);
        }

        public Task<EmailAlertAdministrationWriteStatus> SaveRuleAsync(
            WorkflowAlertRule rule,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmailAlertAdministrationWriteStatus.Saved);
    }

    private sealed class DeliveryStore : IAlertDeliveryStore
    {
        private readonly EmailDeliveryProfile profile;
        private bool claimed;

        public DeliveryStore(EmailDeliveryProfile profile, params AlertDelivery[] deliveries)
        {
            this.profile = profile;
            Deliveries.AddRange(deliveries);
        }

        public List<AlertDelivery> Deliveries { get; } = [];

        public Task<AlertEnqueueStoreResult> EnqueueEmailActionAsync(
            AlertDelivery delivery,
            CancellationToken cancellationToken)
        {
            var existing = Deliveries.SingleOrDefault(candidate =>
                candidate.DeduplicationKey == delivery.DeduplicationKey);
            if (existing is not null)
            {
                return Task.FromResult(new AlertEnqueueStoreResult(
                    AlertEnqueueStatus.AlreadyEnqueued,
                    existing.Id));
            }

            Deliveries.Add(delivery);
            return Task.FromResult(new AlertEnqueueStoreResult(
                AlertEnqueueStatus.Enqueued,
                delivery.Id));
        }

        public Task<ClaimedAlertDelivery?> TryClaimNextAsync(
            string leaseOwner,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            if (claimed || Deliveries.Count == 0)
            {
                return Task.FromResult<ClaimedAlertDelivery?>(null);
            }

            claimed = true;
            var delivery = Deliveries[0];
            return Task.FromResult(delivery.TryClaim(leaseOwner, claimedAtUtc, leaseDuration)
                ? new ClaimedAlertDelivery(delivery, profile)
                : null);
        }

        public Task<AlertDeliveryTransitionStatus> RecordDeliveredAsync(
            Guid deliveryId,
            string leaseOwner,
            DateTimeOffset deliveredAtUtc,
            string? providerMessageId,
            CancellationToken cancellationToken)
        {
            Deliveries.Single(delivery => delivery.Id == deliveryId)
                .RecordDelivered(leaseOwner, deliveredAtUtc, providerMessageId);
            return Task.FromResult(AlertDeliveryTransitionStatus.Saved);
        }

        public Task<AlertDeliveryTransitionStatus> RecordFailureAsync(
            Guid deliveryId,
            string leaseOwner,
            ExecutionError error,
            DateTimeOffset failedAtUtc,
            DateTimeOffset? nextAttemptAtUtc,
            CancellationToken cancellationToken)
        {
            Deliveries.Single(delivery => delivery.Id == deliveryId)
                .RecordFailure(leaseOwner, error, failedAtUtc, nextAttemptAtUtc);
            return Task.FromResult(AlertDeliveryTransitionStatus.Saved);
        }
    }

    private sealed class StubSender(AlertSendResult result) : IAlertSender
    {
        public ValueTask<AlertSendResult> SendAsync(
            EmailDeliveryProfile profile,
            AlertDelivery delivery,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class CancellingSuccessSender(CancellationTokenSource cancellation) : IAlertSender
    {
        public ValueTask<AlertSendResult> SendAsync(
            EmailDeliveryProfile profile,
            AlertDelivery delivery,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(AlertSendResult.Success());
        }
    }

    private sealed class TerminalStore(TerminalAlertPreparation preparation)
        : IWorkflowTerminalAlertStore
    {
        public IReadOnlyList<PreparedTerminalAlert> PreparedAlerts { get; private set; } = [];

        public Task<TerminalAlertPreparation?> LoadPreparationAsync(
            Guid workflowId,
            WorkflowRunStatus terminalStatus,
            CancellationToken cancellationToken) =>
            Task.FromResult<TerminalAlertPreparation?>(preparation);

        public Task<TerminalAlertCommitStatus> CommitTerminalRunAsync(
            WorkflowRun terminalRun,
            WorkflowRunStatus expectedPreviousStatus,
            string? expectedLeaseOwner,
            IReadOnlyList<PreparedTerminalAlert> alerts,
            CancellationToken cancellationToken)
        {
            PreparedAlerts = alerts;
            return Task.FromResult(TerminalAlertCommitStatus.Saved);
        }
    }

    private sealed class DeliveryQueryStore(int totalCount) : IEmailAlertDeliveryQueryStore
    {
        public EmailAlertDeliveryListFilter? Filter { get; private set; }

        public Task<(IReadOnlyList<EmailAlertDeliveryListItem> Items, int TotalCount)> ListAsync(
            EmailAlertDeliveryListFilter filter,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Filter = filter;
            return Task.FromResult<(IReadOnlyList<EmailAlertDeliveryListItem>, int)>(([], totalCount));
        }
    }
}
