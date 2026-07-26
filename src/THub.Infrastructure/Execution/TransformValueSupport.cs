using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using THub.Application.Execution;

namespace THub.Infrastructure.Execution;

internal static class TransformValueSupport
{
    public static int Compare(TabularValue left, TabularValue right)
    {
        if (left.Kind != right.Kind)
        {
            throw ExecutionFailure.Configuration(
                "execution.transform.type",
                "Transform values have incompatible data types.");
        }

        return left.Kind switch
        {
            TabularValueKind.Boolean => ((bool)left.Value!).CompareTo((bool)right.Value!),
            TabularValueKind.Int64 => ((long)left.Value!).CompareTo((long)right.Value!),
            TabularValueKind.Decimal => ((decimal)left.Value!).CompareTo((decimal)right.Value!),
            TabularValueKind.Double => ((double)left.Value!).CompareTo((double)right.Value!),
            TabularValueKind.String => string.Compare(
                (string)left.Value!,
                (string)right.Value!,
                StringComparison.Ordinal),
            TabularValueKind.DateTimeOffset => ((DateTimeOffset)left.Value!).CompareTo(
                (DateTimeOffset)right.Value!),
            TabularValueKind.Guid => ((Guid)left.Value!).CompareTo((Guid)right.Value!),
            TabularValueKind.Binary => ((ReadOnlyMemory<byte>)left.Value!).Span.SequenceCompareTo(
                ((ReadOnlyMemory<byte>)right.Value!).Span),
            _ => throw new ArgumentOutOfRangeException(nameof(left))
        };
    }

    public static bool HasNull(TabularRow row, IReadOnlyList<int> indexes)
    {
        for (var index = 0; index < indexes.Count; index++)
        {
            if (row.Values[indexes[index]].Kind == TabularValueKind.Null)
            {
                return true;
            }
        }

        return false;
    }

    public static async IAsyncEnumerable<TabularBatch> BatchRowsAsync(
        IReadOnlyList<TabularRow> rows,
        int maximumRowsPerBatch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        for (var offset = 0; offset < rows.Count; offset += maximumRowsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(maximumRowsPerBatch, rows.Count - offset);
            var batchRows = new TabularRow[count];
            for (var index = 0; index < count; index++)
            {
                if ((index & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                batchRows[index] = rows[offset + index];
            }

            yield return new TabularBatch(batchRows);
        }
    }
}

internal enum TransformKeyAddResult
{
    Existing,
    Added,
    LimitExceeded
}

internal sealed class TransformRowKey(IReadOnlyList<TabularValue> values)
{
    public IReadOnlyList<TabularValue> Values { get; } = values;
}

internal sealed class TransformRowKeyHasher
{
    private const int KeyBoundary = unchecked((int)0x9e3779b9);
    private readonly int seed;

    public TransformRowKeyHasher()
        : this(Random.Shared.Next())
    {
    }

    internal TransformRowKeyHasher(int seed)
    {
        this.seed = seed;
    }

    public int GetHashCode(TabularRow row, IReadOnlyList<int> indexes)
    {
        var hash = new HashCode();
        hash.Add(seed);
        hash.Add(indexes.Count);
        for (var index = 0; index < indexes.Count; index++)
        {
            AddValue(ref hash, row.Values[indexes[index]]);
            hash.Add(KeyBoundary);
        }

        return hash.ToHashCode();
    }

    private static void AddValue(ref HashCode hash, TabularValue value)
    {
        hash.Add((byte)value.Kind);
        switch (value.Kind)
        {
            case TabularValueKind.Null:
                hash.Add(0);
                break;
            case TabularValueKind.Boolean:
                hash.Add((bool)value.Value!);
                break;
            case TabularValueKind.Int64:
                AddInt64(ref hash, (long)value.Value!);
                break;
            case TabularValueKind.Decimal:
                AddDecimal(ref hash, (decimal)value.Value!);
                break;
            case TabularValueKind.Double:
                AddInt64(ref hash, CanonicalDoubleBits((double)value.Value!));
                break;
            case TabularValueKind.String:
                hash.Add((string)value.Value!, StringComparer.Ordinal);
                break;
            case TabularValueKind.DateTimeOffset:
                AddInt64(ref hash, ((DateTimeOffset)value.Value!).UtcDateTime.Ticks);
                break;
            case TabularValueKind.Guid:
                AddGuid(ref hash, (Guid)value.Value!);
                break;
            case TabularValueKind.Binary:
                AddBinary(ref hash, ((ReadOnlyMemory<byte>)value.Value!).Span);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static void AddInt64(ref HashCode hash, long value)
    {
        var bits = unchecked((ulong)value);
        hash.Add((uint)bits);
        hash.Add((uint)(bits >> 32));
    }

    private static long CanonicalDoubleBits(double value)
    {
        if (value == 0d)
        {
            return 0;
        }
        if (double.IsNaN(value))
        {
            return unchecked((long)0x7ff8000000000000);
        }

        return BitConverter.DoubleToInt64Bits(value);
    }

    private static void AddDecimal(ref HashCode hash, decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        var low = unchecked((uint)bits[0]);
        var middle = unchecked((uint)bits[1]);
        var high = unchecked((uint)bits[2]);
        var scale = (bits[3] >> 16) & 0x7f;
        var negative = (bits[3] & int.MinValue) != 0;

        if ((low | middle | high) == 0)
        {
            scale = 0;
            negative = false;
        }
        else
        {
            while (scale > 0)
            {
                var dividedLow = low;
                var dividedMiddle = middle;
                var dividedHigh = high;
                if (Divide96By10(
                        ref dividedLow,
                        ref dividedMiddle,
                        ref dividedHigh) != 0)
                {
                    break;
                }

                low = dividedLow;
                middle = dividedMiddle;
                high = dividedHigh;
                scale--;
            }
        }

        hash.Add(low);
        hash.Add(middle);
        hash.Add(high);
        hash.Add((byte)scale);
        hash.Add(negative);
    }

    private static uint Divide96By10(ref uint low, ref uint middle, ref uint high)
    {
        ulong part = high;
        high = (uint)(part / 10);
        var remainder = part % 10;

        part = (remainder << 32) | middle;
        middle = (uint)(part / 10);
        remainder = part % 10;

        part = (remainder << 32) | low;
        low = (uint)(part / 10);
        return (uint)(part % 10);
    }

    private static void AddGuid(ref HashCode hash, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes);
        for (var offset = 0; offset < bytes.Length; offset += sizeof(uint))
        {
            hash.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
        }
    }

    private static void AddBinary(ref HashCode hash, ReadOnlySpan<byte> value)
    {
        hash.Add(value.Length);
        var offset = 0;
        for (; offset <= value.Length - sizeof(ulong); offset += sizeof(ulong))
        {
            hash.Add(BinaryPrimitives.ReadUInt64LittleEndian(value[offset..]));
        }
        for (; offset < value.Length; offset++)
        {
            hash.Add(value[offset]);
        }
    }
}

internal static class TransformRowKeyComparer
{
    public static bool Equals(
        TransformRowKey key,
        TabularRow row,
        IReadOnlyList<int> indexes)
    {
        if (key.Values.Count != indexes.Count)
        {
            return false;
        }

        for (var index = 0; index < indexes.Count; index++)
        {
            if (!ValuesEqual(key.Values[index], row.Values[indexes[index]]))
            {
                return false;
            }
        }

        return true;
    }

    public static TransformRowKey CreateOwned(
        TabularRow row,
        IReadOnlyList<int> indexes)
    {
        var values = new TabularValue[indexes.Count];
        for (var index = 0; index < indexes.Count; index++)
        {
            values[index] = row.Values[indexes[index]];
        }

        return new(values);
    }

    private static bool ValuesEqual(TabularValue left, TabularValue right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        return left.Kind switch
        {
            TabularValueKind.Null => true,
            TabularValueKind.Boolean => (bool)left.Value! == (bool)right.Value!,
            TabularValueKind.Int64 => (long)left.Value! == (long)right.Value!,
            TabularValueKind.Decimal => (decimal)left.Value! == (decimal)right.Value!,
            TabularValueKind.Double => ((double)left.Value!).Equals((double)right.Value!),
            TabularValueKind.String => string.Equals(
                (string)left.Value!,
                (string)right.Value!,
                StringComparison.Ordinal),
            TabularValueKind.DateTimeOffset =>
                ((DateTimeOffset)left.Value!).UtcDateTime.Ticks
                == ((DateTimeOffset)right.Value!).UtcDateTime.Ticks,
            TabularValueKind.Guid => (Guid)left.Value! == (Guid)right.Value!,
            TabularValueKind.Binary => ((ReadOnlyMemory<byte>)left.Value!).Span.SequenceEqual(
                ((ReadOnlyMemory<byte>)right.Value!).Span),
            _ => throw new ArgumentOutOfRangeException(nameof(left))
        };
    }

}

internal sealed class TransformStructuralKeySet
{
    private readonly Dictionary<int, List<TransformRowKey>> buckets = [];
    private readonly TransformRowKeyHasher? hasher;
    private readonly Func<TabularRow, IReadOnlyList<int>, int>? hashCodeFactory;

    public TransformStructuralKeySet()
    {
        hasher = new TransformRowKeyHasher();
    }

    internal TransformStructuralKeySet(
        Func<TabularRow, IReadOnlyList<int>, int> hashCodeFactory)
    {
        ArgumentNullException.ThrowIfNull(hashCodeFactory);
        this.hashCodeFactory = hashCodeFactory;
    }

    public TransformKeyAddResult TryAdd(
        TabularRow row,
        IReadOnlyList<int> indexes,
        int maximumKeys,
        CancellationToken cancellationToken) =>
        TryAdd(row, indexes, maximumKeys, cancellationToken, out _);

    public TransformKeyAddResult TryAdd(
        TabularRow row,
        IReadOnlyList<int> indexes,
        int maximumKeys,
        CancellationToken cancellationToken,
        out TransformRowKey? ownedKey)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hashCode = hashCodeFactory is null
            ? hasher!.GetHashCode(row, indexes)
            : hashCodeFactory(row, indexes);
        var hasBucket = buckets.TryGetValue(hashCode, out var bucket);
        if (hasBucket)
        {
            for (var index = 0; index < bucket!.Count; index++)
            {
                if ((index & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (TransformRowKeyComparer.Equals(bucket[index], row, indexes))
                {
                    ownedKey = null;
                    return TransformKeyAddResult.Existing;
                }
            }
        }

        if (Count == maximumKeys)
        {
            ownedKey = null;
            return TransformKeyAddResult.LimitExceeded;
        }

        if (!hasBucket)
        {
            bucket = [];
            buckets.Add(hashCode, bucket);
        }
        ownedKey = TransformRowKeyComparer.CreateOwned(row, indexes);
        bucket!.Add(ownedKey);
        Count++;
        return TransformKeyAddResult.Added;
    }

    public int Count { get; private set; }
}

internal sealed class TransformStructuralKeyMap<TValue>
    where TValue : class
{
    private readonly Dictionary<int, List<Entry>> buckets = [];
    private readonly TransformRowKeyHasher hasher = new();

    public TransformKeyAddResult GetOrAdd<TState>(
        TabularRow row,
        IReadOnlyList<int> indexes,
        int maximumKeys,
        TState state,
        Func<TransformRowKey, TState, TValue> valueFactory,
        CancellationToken cancellationToken,
        out TValue value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hashCode = hasher.GetHashCode(row, indexes);
        var hasBucket = buckets.TryGetValue(hashCode, out var bucket);
        if (hasBucket)
        {
            for (var index = 0; index < bucket!.Count; index++)
            {
                if ((index & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var existing = bucket[index];
                if (TransformRowKeyComparer.Equals(existing.Key, row, indexes))
                {
                    value = existing.Value;
                    return TransformKeyAddResult.Existing;
                }
            }
        }

        if (Count == maximumKeys)
        {
            value = null!;
            return TransformKeyAddResult.LimitExceeded;
        }

        if (!hasBucket)
        {
            bucket = [];
            buckets.Add(hashCode, bucket);
        }
        var ownedKey = TransformRowKeyComparer.CreateOwned(row, indexes);
        value = valueFactory(ownedKey, state);
        bucket!.Add(new(ownedKey, value));
        Count++;
        return TransformKeyAddResult.Added;
    }

    public bool TryGetValue(
        TabularRow row,
        IReadOnlyList<int> indexes,
        CancellationToken cancellationToken,
        out TValue value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hashCode = hasher.GetHashCode(row, indexes);
        if (buckets.TryGetValue(hashCode, out var bucket))
        {
            for (var index = 0; index < bucket.Count; index++)
            {
                if ((index & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var existing = bucket[index];
                if (TransformRowKeyComparer.Equals(existing.Key, row, indexes))
                {
                    value = existing.Value;
                    return true;
                }
            }
        }

        value = null!;
        return false;
    }

    public int Count { get; private set; }

    private sealed record Entry(TransformRowKey Key, TValue Value);
}
