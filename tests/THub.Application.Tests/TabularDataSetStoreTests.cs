using System.Runtime.CompilerServices;
using THub.Application.Execution;

namespace THub.Application.Tests;

public sealed class TabularDataSetStoreTests
{
    [Fact]
    public async Task RejectsTypeMismatchAndDisposesTheConnectorBatch()
    {
        var owner = new RecordingAsyncDisposable();
        var schema = new TabularSchema(
            [new TabularColumn("Count", TabularDataType.Int64, isNullable: false)]);
        var store = new InMemoryTabularDataSetStore();

        var exception = await Assert.ThrowsAsync<TabularContractException>(async () =>
            await store.MaterializeAsync(
                schema,
                InvalidBatches(owner),
                new TabularExecutionLimits(),
                CancellationToken.None));

        Assert.Equal("tabular.value.type.invalid", exception.Code);
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task MaterializedDataCanBeReadMoreThanOnceForFanOut()
    {
        var schema = new TabularSchema(
            [new TabularColumn("Count", TabularDataType.Int64, isNullable: false)]);
        var store = new InMemoryTabularDataSetStore();
        await using var dataSet = await store.MaterializeAsync(
            schema,
            ValidBatches(),
            new TabularExecutionLimits(),
            CancellationToken.None);

        var first = await CountRowsAsync(dataSet);
        var second = await CountRowsAsync(dataSet);

        Assert.Equal(2, first);
        Assert.Equal(first, second);
        Assert.Equal(2, dataSet.RowCount);
    }

    private static async IAsyncEnumerable<TabularBatch> InvalidBatches(
        RecordingAsyncDisposable owner,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TabularBatch(
            [new TabularRow([TabularValue.From("not-an-integer")])],
            owner);
    }

    private static async IAsyncEnumerable<TabularBatch> ValidBatches(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TabularBatch(
            [
                new TabularRow([TabularValue.From(1L)]),
                new TabularRow([TabularValue.From(2L)])
            ]);
    }

    private static async Task<long> CountRowsAsync(ITabularDataSet dataSet)
    {
        long result = 0;
        await foreach (var batch in dataSet.ReadBatchesAsync(CancellationToken.None))
        {
            await using (batch)
            {
                result += batch.Rows.Count;
            }
        }

        return result;
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
