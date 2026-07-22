using System.Runtime.CompilerServices;
using System.Text;

namespace THub.Application.Execution;

public enum TabularDataType
{
    Boolean,
    Int64,
    Decimal,
    Double,
    String,
    DateTimeOffset,
    Guid,
    Binary
}

public enum TabularValueKind
{
    Null,
    Boolean,
    Int64,
    Decimal,
    Double,
    String,
    DateTimeOffset,
    Guid,
    Binary
}

public readonly struct TabularValue
{
    public const int AbsoluteMaximumStringCharacters = 16_000_000;
    public const int AbsoluteMaximumBinaryBytes = 64 * 1024 * 1024;

    private TabularValue(TabularValueKind kind, object? value)
    {
        Kind = kind;
        Value = value;
    }

    public TabularValueKind Kind { get; }

    public object? Value { get; }

    public static TabularValue Null { get; } = new(TabularValueKind.Null, null);

    public static TabularValue From(bool value) => new(TabularValueKind.Boolean, value);

    public static TabularValue From(long value) => new(TabularValueKind.Int64, value);

    public static TabularValue From(decimal value) => new(TabularValueKind.Decimal, value);

    public static TabularValue From(double value) => new(TabularValueKind.Double, value);

    public static TabularValue From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > AbsoluteMaximumStringCharacters)
        {
            throw new TabularLimitExceededException(
                "tabular.value.string.absolute_limit",
                $"A tabular string cannot exceed {AbsoluteMaximumStringCharacters} characters.");
        }

        return new(TabularValueKind.String, value);
    }

    public static TabularValue From(DateTimeOffset value) =>
        new(TabularValueKind.DateTimeOffset, value);

    public static TabularValue From(Guid value) => new(TabularValueKind.Guid, value);

    public static TabularValue From(ReadOnlyMemory<byte> value)
    {
        if (value.Length > AbsoluteMaximumBinaryBytes)
        {
            throw new TabularLimitExceededException(
                "tabular.value.binary.absolute_limit",
                $"A tabular binary value cannot exceed {AbsoluteMaximumBinaryBytes} bytes.");
        }

        return new(TabularValueKind.Binary, new ReadOnlyMemory<byte>(value.ToArray()));
    }

    public static TabularValue FromObject(object? value) => value switch
    {
        null => Null,
        bool typed => From(typed),
        byte typed => From((long)typed),
        short typed => From((long)typed),
        int typed => From((long)typed),
        long typed => From(typed),
        float typed => From((double)typed),
        double typed => From(typed),
        decimal typed => From(typed),
        string typed => From(typed),
        DateTimeOffset typed => From(typed),
        Guid typed => From(typed),
        byte[] typed => From(typed),
        ReadOnlyMemory<byte> typed => From(typed),
        _ => throw new ArgumentException(
            $"Values of type '{value.GetType().Name}' are not supported by the tabular contract.",
            nameof(value))
    };

    internal long EstimateSizeInBytes() => Kind switch
    {
        TabularValueKind.Null => 1,
        TabularValueKind.Boolean => sizeof(bool),
        TabularValueKind.Int64 => sizeof(long),
        TabularValueKind.Decimal => 16,
        TabularValueKind.Double => sizeof(double),
        TabularValueKind.String => Encoding.UTF8.GetByteCount((string)Value!),
        TabularValueKind.DateTimeOffset => 16,
        TabularValueKind.Guid => 16,
        TabularValueKind.Binary => ((ReadOnlyMemory<byte>)Value!).Length,
        _ => throw new InvalidOperationException($"Unsupported tabular value kind '{Kind}'.")
    };
}

public sealed record TabularColumn
{
    public const int MaximumNameLength = 128;

    public TabularColumn(string name, TabularDataType dataType, bool isNullable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();
        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                $"A tabular column name cannot exceed {MaximumNameLength} characters.");
        }

        if (normalizedName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A tabular column name cannot contain control characters.",
                nameof(name));
        }

        if (!Enum.IsDefined(dataType))
        {
            throw new ArgumentOutOfRangeException(nameof(dataType));
        }

        Name = normalizedName;
        DataType = dataType;
        IsNullable = isNullable;
    }

    public string Name { get; }

    public TabularDataType DataType { get; }

    public bool IsNullable { get; }
}

public sealed class TabularSchema
{
    public const int AbsoluteMaximumColumns = 512;

    public TabularSchema(IEnumerable<TabularColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var materialized = new List<TabularColumn>();
        foreach (var column in columns)
        {
            if (materialized.Count == AbsoluteMaximumColumns)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columns),
                    $"A tabular schema cannot contain more than {AbsoluteMaximumColumns} columns.");
            }

            materialized.Add(column);
        }

        if (materialized.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columns),
                $"A tabular schema must contain between 1 and {AbsoluteMaximumColumns} columns.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in materialized)
        {
            ArgumentNullException.ThrowIfNull(column);
            if (!names.Add(column.Name))
            {
                throw new ArgumentException(
                    $"Tabular column name '{column.Name}' is duplicated.",
                    nameof(columns));
            }
        }

        Columns = materialized.AsReadOnly();
    }

    public IReadOnlyList<TabularColumn> Columns { get; }
}

public sealed class TabularRow
{
    public TabularRow(IEnumerable<TabularValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var materialized = new List<TabularValue>();
        foreach (var value in values)
        {
            if (materialized.Count == TabularSchema.AbsoluteMaximumColumns)
            {
                throw new TabularLimitExceededException(
                    "tabular.row.columns.absolute_limit",
                    $"A tabular row cannot contain more than {TabularSchema.AbsoluteMaximumColumns} values.");
            }

            materialized.Add(value);
        }

        Values = materialized.AsReadOnly();
    }

    public IReadOnlyList<TabularValue> Values { get; }
}

/// <summary>
/// A bounded transfer unit. The optional owner enables zero-copy connector buffers to be
/// released promptly after a consumer has copied or processed the batch.
/// </summary>
public sealed class TabularBatch : IAsyncDisposable
{
    public const long AbsoluteMaximumBytes = 256L * 1024 * 1024;

    private IAsyncDisposable? _owner;

    public TabularBatch(IEnumerable<TabularRow> rows, IAsyncDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var materialized = new List<TabularRow>();
        foreach (var row in rows)
        {
            if (materialized.Count == TabularExecutionLimits.AbsoluteMaximumRowsPerBatch)
            {
                throw new TabularLimitExceededException(
                    "tabular.batch.rows.absolute_limit",
                    $"A tabular batch cannot contain more than {TabularExecutionLimits.AbsoluteMaximumRowsPerBatch} rows.");
            }

            materialized.Add(row);
        }

        foreach (var row in materialized)
        {
            ArgumentNullException.ThrowIfNull(row);
        }

        Rows = materialized.AsReadOnly();
        _owner = owner;

        try
        {
            EstimatedByteCount = materialized.Aggregate(
                0L,
                static (total, row) => checked(
                    total + row.Values.Sum(static value => value.EstimateSizeInBytes())));
        }
        catch (OverflowException exception)
        {
            throw new TabularLimitExceededException(
                "tabular.batch.bytes.overflow",
                "The estimated batch size exceeds the supported range.",
                exception);
        }

        if (EstimatedByteCount > AbsoluteMaximumBytes)
        {
            throw new TabularLimitExceededException(
                "tabular.batch.bytes.absolute_limit",
                $"A tabular batch cannot exceed {AbsoluteMaximumBytes} bytes.");
        }
    }

    public IReadOnlyList<TabularRow> Rows { get; }

    public long EstimatedByteCount { get; }

    public async ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is not null)
        {
            await owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record TabularExecutionLimits
{
    public const int AbsoluteMaximumColumns = TabularSchema.AbsoluteMaximumColumns;
    public const int AbsoluteMaximumRowsPerBatch = 100_000;
    public const int AbsoluteMaximumBatchesPerOutput = 1_000_000;
    public const long AbsoluteMaximumRowsPerOutput = 10_000_000;
    public const long AbsoluteMaximumBytesPerOutput = 4L * 1024 * 1024 * 1024;

    public TabularExecutionLimits(
        int maximumColumns = 256,
        int maximumRowsPerBatch = 5_000,
        long maximumBytesPerBatch = 8 * 1024 * 1024,
        long maximumRowsPerNodeOutput = 1_000_000,
        long maximumBytesPerNodeOutput = 512L * 1024 * 1024,
        int maximumBatchesPerNodeOutput = 10_000,
        int maximumStringCharacters = 1_000_000,
        int maximumBinaryBytesPerValue = 8 * 1024 * 1024,
        int maximumInputsPerNode = 16,
        long maximumRetainedRowsPerWorkflow = 3_000_000,
        long maximumRetainedBytesPerWorkflow = 1536L * 1024 * 1024)
    {
        MaximumColumns = RequireRange(
            maximumColumns,
            1,
            AbsoluteMaximumColumns,
            nameof(maximumColumns));
        MaximumRowsPerBatch = RequireRange(
            maximumRowsPerBatch,
            1,
            AbsoluteMaximumRowsPerBatch,
            nameof(maximumRowsPerBatch));
        MaximumBytesPerBatch = RequireRange(
            maximumBytesPerBatch,
            1,
            TabularBatch.AbsoluteMaximumBytes,
            nameof(maximumBytesPerBatch));
        MaximumRowsPerNodeOutput = RequireRange(
            maximumRowsPerNodeOutput,
            1,
            AbsoluteMaximumRowsPerOutput,
            nameof(maximumRowsPerNodeOutput));
        MaximumBytesPerNodeOutput = RequireRange(
            maximumBytesPerNodeOutput,
            1,
            AbsoluteMaximumBytesPerOutput,
            nameof(maximumBytesPerNodeOutput));
        MaximumBatchesPerNodeOutput = RequireRange(
            maximumBatchesPerNodeOutput,
            1,
            AbsoluteMaximumBatchesPerOutput,
            nameof(maximumBatchesPerNodeOutput));
        MaximumStringCharacters = RequireRange(
            maximumStringCharacters,
            1,
            TabularValue.AbsoluteMaximumStringCharacters,
            nameof(maximumStringCharacters));
        MaximumBinaryBytesPerValue = RequireRange(
            maximumBinaryBytesPerValue,
            1,
            TabularValue.AbsoluteMaximumBinaryBytes,
            nameof(maximumBinaryBytesPerValue));
        MaximumInputsPerNode = RequireRange(maximumInputsPerNode, 1, 128, nameof(maximumInputsPerNode));
        MaximumRetainedRowsPerWorkflow = RequireRange(
            maximumRetainedRowsPerWorkflow,
            1,
            AbsoluteMaximumRowsPerOutput,
            nameof(maximumRetainedRowsPerWorkflow));
        MaximumRetainedBytesPerWorkflow = RequireRange(
            maximumRetainedBytesPerWorkflow,
            1,
            AbsoluteMaximumBytesPerOutput,
            nameof(maximumRetainedBytesPerWorkflow));

        if (MaximumRowsPerNodeOutput < MaximumRowsPerBatch)
        {
            throw new ArgumentException(
                "The per-output row limit cannot be smaller than the per-batch row limit.",
                nameof(maximumRowsPerNodeOutput));
        }

        if (MaximumBytesPerNodeOutput < MaximumBytesPerBatch)
        {
            throw new ArgumentException(
                "The per-output byte limit cannot be smaller than the per-batch byte limit.",
                nameof(maximumBytesPerNodeOutput));
        }


        if (MaximumRetainedRowsPerWorkflow < MaximumRowsPerNodeOutput)
        {
            throw new ArgumentException(
                "The retained workflow row limit cannot be smaller than the per-output row limit.",
                nameof(maximumRetainedRowsPerWorkflow));
        }

        if (MaximumRetainedBytesPerWorkflow < MaximumBytesPerNodeOutput)
        {
            throw new ArgumentException(
                "The retained workflow byte limit cannot be smaller than the per-output byte limit.",
                nameof(maximumRetainedBytesPerWorkflow));
        }
    }

    public int MaximumColumns { get; }

    public int MaximumRowsPerBatch { get; }

    public long MaximumBytesPerBatch { get; }

    public long MaximumRowsPerNodeOutput { get; }

    public long MaximumBytesPerNodeOutput { get; }

    public int MaximumBatchesPerNodeOutput { get; }

    public int MaximumStringCharacters { get; }

    public int MaximumBinaryBytesPerValue { get; }

    public int MaximumInputsPerNode { get; }

    public long MaximumRetainedRowsPerWorkflow { get; }

    public long MaximumRetainedBytesPerWorkflow { get; }

    private static int RequireRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be {minimum} to {maximum}.");
        }

        return value;
    }

    private static long RequireRange(
        long value,
        long minimum,
        long maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be {minimum} to {maximum}.");
        }

        return value;
    }
}

public interface ITabularDataSet : IAsyncDisposable
{
    TabularSchema Schema { get; }

    long RowCount { get; }

    long ByteCount { get; }

    IAsyncEnumerable<TabularBatch> ReadBatchesAsync(CancellationToken cancellationToken = default);
}

public interface ITabularDataSetStore
{
    ValueTask<ITabularDataSet> MaterializeAsync(
        TabularSchema schema,
        IAsyncEnumerable<TabularBatch> batches,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken);
}

public class TabularContractException : Exception
{
    public TabularContractException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public TabularContractException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public sealed class TabularLimitExceededException : TabularContractException
{
    public TabularLimitExceededException(string code, string message)
        : base(code, message)
    {
    }

    public TabularLimitExceededException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}

/// <summary>
/// Conservative default materializer for an initial single-worker deployment. Every connector
/// output is copied, validated, and bounded before it can be replayed by downstream branches.
/// A disk-backed implementation can replace this port without changing node executors.
/// </summary>
public sealed class InMemoryTabularDataSetStore : ITabularDataSetStore
{
    public async ValueTask<ITabularDataSet> MaterializeAsync(
        TabularSchema schema,
        IAsyncEnumerable<TabularBatch> batches,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(limits);

        if (schema.Columns.Count > limits.MaximumColumns)
        {
            throw new TabularLimitExceededException(
                "tabular.schema.columns.limit",
                $"The output schema exceeds the configured {limits.MaximumColumns}-column limit.");
        }

        var storedBatches = new List<IReadOnlyList<TabularRow>>();
        long totalRows = 0;
        long totalBytes = 0;
        var batchCount = 0;

        await foreach (var batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batch is null)
            {
                throw new TabularContractException(
                    "tabular.batch.null",
                    "A node returned a null tabular batch.");
            }

            await using (batch.ConfigureAwait(false))
            {
                batchCount = checked(batchCount + 1);
                if (batchCount > limits.MaximumBatchesPerNodeOutput)
                {
                    throw new TabularLimitExceededException(
                        "tabular.output.batches.limit",
                        $"The output exceeds the configured {limits.MaximumBatchesPerNodeOutput}-batch limit.");
                }

                if (batch.Rows.Count > limits.MaximumRowsPerBatch)
                {
                    throw new TabularLimitExceededException(
                        "tabular.batch.rows.limit",
                        $"A batch exceeds the configured {limits.MaximumRowsPerBatch}-row limit.");
                }

                if (batch.EstimatedByteCount > limits.MaximumBytesPerBatch)
                {
                    throw new TabularLimitExceededException(
                        "tabular.batch.bytes.limit",
                        $"A batch exceeds the configured {limits.MaximumBytesPerBatch}-byte limit.");
                }

                var storedRows = new TabularRow[batch.Rows.Count];
                for (var rowIndex = 0; rowIndex < batch.Rows.Count; rowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = batch.Rows[rowIndex];
                    ValidateRow(schema, row, limits);
                    storedRows[rowIndex] = row;
                }

                totalRows = checked(totalRows + batch.Rows.Count);
                totalBytes = checked(totalBytes + batch.EstimatedByteCount);
                if (totalRows > limits.MaximumRowsPerNodeOutput)
                {
                    throw new TabularLimitExceededException(
                        "tabular.output.rows.limit",
                        $"The output exceeds the configured {limits.MaximumRowsPerNodeOutput}-row limit.");
                }

                if (totalBytes > limits.MaximumBytesPerNodeOutput)
                {
                    throw new TabularLimitExceededException(
                        "tabular.output.bytes.limit",
                        $"The output exceeds the configured {limits.MaximumBytesPerNodeOutput}-byte limit.");
                }

                storedBatches.Add(storedRows);
            }
        }

        return new InMemoryTabularDataSet(schema, storedBatches, totalRows, totalBytes);
    }

    private static void ValidateRow(
        TabularSchema schema,
        TabularRow row,
        TabularExecutionLimits limits)
    {
        if (row.Values.Count != schema.Columns.Count)
        {
            throw new TabularContractException(
                "tabular.row.columns.invalid",
                $"A row contains {row.Values.Count} values for a {schema.Columns.Count}-column schema.");
        }

        for (var index = 0; index < row.Values.Count; index++)
        {
            var value = row.Values[index];
            var column = schema.Columns[index];
            if (value.Kind == TabularValueKind.Null)
            {
                if (!column.IsNullable)
                {
                    throw new TabularContractException(
                        "tabular.value.null.invalid",
                        $"Column '{column.Name}' does not allow null values.");
                }

                continue;
            }

            if (!Matches(value.Kind, column.DataType))
            {
                throw new TabularContractException(
                    "tabular.value.type.invalid",
                    $"Column '{column.Name}' received an incompatible value type.");
            }

            if (value.Kind == TabularValueKind.String
                && ((string)value.Value!).Length > limits.MaximumStringCharacters)
            {
                throw new TabularLimitExceededException(
                    "tabular.value.string.limit",
                    $"A string value exceeds the configured {limits.MaximumStringCharacters}-character limit.");
            }

            if (value.Kind == TabularValueKind.Binary
                && ((ReadOnlyMemory<byte>)value.Value!).Length > limits.MaximumBinaryBytesPerValue)
            {
                throw new TabularLimitExceededException(
                    "tabular.value.binary.limit",
                    $"A binary value exceeds the configured {limits.MaximumBinaryBytesPerValue}-byte limit.");
            }
        }
    }

    private static bool Matches(TabularValueKind valueKind, TabularDataType dataType) =>
        (valueKind, dataType) switch
        {
            (TabularValueKind.Boolean, TabularDataType.Boolean) => true,
            (TabularValueKind.Int64, TabularDataType.Int64) => true,
            (TabularValueKind.Decimal, TabularDataType.Decimal) => true,
            (TabularValueKind.Double, TabularDataType.Double) => true,
            (TabularValueKind.String, TabularDataType.String) => true,
            (TabularValueKind.DateTimeOffset, TabularDataType.DateTimeOffset) => true,
            (TabularValueKind.Guid, TabularDataType.Guid) => true,
            (TabularValueKind.Binary, TabularDataType.Binary) => true,
            _ => false
        };

    private sealed class InMemoryTabularDataSet : ITabularDataSet
    {
        private IReadOnlyList<IReadOnlyList<TabularRow>>? _batches;

        public InMemoryTabularDataSet(
            TabularSchema schema,
            IReadOnlyList<IReadOnlyList<TabularRow>> batches,
            long rowCount,
            long byteCount)
        {
            Schema = schema;
            _batches = batches;
            RowCount = rowCount;
            ByteCount = byteCount;
        }

        public TabularSchema Schema { get; }

        public long RowCount { get; }

        public long ByteCount { get; }

        public async IAsyncEnumerable<TabularBatch> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var batches = _batches
                ?? throw new ObjectDisposedException(nameof(InMemoryTabularDataSet));

            await Task.Yield();
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TabularBatch(batch);
            }
        }

        public ValueTask DisposeAsync()
        {
            _batches = null;
            return ValueTask.CompletedTask;
        }
    }
}
