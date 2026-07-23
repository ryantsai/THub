using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowVariableResolverTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 23, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task ResolvesBuiltInsLiteralsAndDatabaseScalarsOnce()
    {
        var provider = new FakeDatabaseProvider(TabularValue.From(42L));
        var resolver = new WorkflowVariableResolver(provider);
        var runId = Guid.NewGuid();
        var graph = new WorkflowGraph(
            [],
            [],
            [
                new(
                    "region",
                    WorkflowVariableKind.Literal,
                    WorkflowValueType.String,
                    "north"),
                new(
                    "threshold",
                    WorkflowVariableKind.Database,
                    WorkflowValueType.Int64,
                    null,
                    Guid.NewGuid(),
                    "dbo",
                    "Settings",
                    "Value",
                    "Name",
                    "threshold")
            ],
            []);

        var values = await resolver.ResolveAsync(
            runId,
            StartedAt,
            graph,
            CancellationToken.None);

        Assert.Equal(runId, values["runId"].Value);
        Assert.Equal(StartedAt, values["runStartedAtUtc"].Value);
        Assert.Equal("north", values["region"].Value);
        Assert.Equal(42L, values["threshold"].Value);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task RejectsDatabaseValueWithWrongConfiguredType()
    {
        var resolver = new WorkflowVariableResolver(
            new FakeDatabaseProvider(TabularValue.From("not-an-integer")));
        var graph = new WorkflowGraph(
            [],
            [],
            [
                new(
                    "threshold",
                    WorkflowVariableKind.Database,
                    WorkflowValueType.Int64,
                    null,
                    Guid.NewGuid(),
                    "dbo",
                    "Settings",
                    "Value",
                    "Name",
                    "threshold")
            ],
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                Guid.NewGuid(),
                StartedAt,
                graph,
                CancellationToken.None));
    }

    private sealed class FakeDatabaseProvider(TabularValue value)
        : IWorkflowDatabaseVariableProvider
    {
        public int CallCount { get; private set; }

        public Task<TabularValue> ResolveAsync(
            WorkflowVariable variable,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(value);
        }
    }
}
