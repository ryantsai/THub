using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class TransformNodeExecutorTests
{
    [Fact]
    public async Task SelectColumnsProjectsSchemaAndValuesInConfiguredOrder()
    {
        var input = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Name", TabularDataType.String)
            ]),
            [new([TabularValue.From(7L), TabularValue.From("Seven")])]);
        var executor = new SelectColumnsNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new("select", WorkflowNodeKind.SelectColumns, "Select", 0, 0, """{"columns":["Name","Id"]}"""),
            [new("source", input)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(["Name", "Id"], result.Output!.Schema.Columns.Select(column => column.Name));
        var row = Assert.Single(rows);
        Assert.Equal("Seven", row.Values[0].Value);
        Assert.Equal(7L, row.Values[1].Value);
    }

    [Fact]
    public async Task FilterRowsAppliesTypedConditionsWithAndSemantics()
    {
        var schema = new TabularSchema(
        [
            new("Id", TabularDataType.Int64, false),
            new("Name", TabularDataType.String, false)
        ]);
        var input = DataSet(
            schema,
            [
                new([TabularValue.From(1L), TabularValue.From("Alpha")]),
                new([TabularValue.From(2L), TabularValue.From("Beta")]),
                new([TabularValue.From(3L), TabularValue.From("Alpine")])
            ]);
        var executor = new FilterRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "filter",
                WorkflowNodeKind.FilterRows,
                "Filter",
                0,
                0,
                """{"conditions":[{"column":"Id","operator":"greaterThan","value":1},{"column":"Name","operator":"startsWith","value":"Al"}]}"""),
            [new("source", input)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        var row = Assert.Single(rows);
        Assert.Equal(3L, row.Values[0].Value);
    }

    [Fact]
    public async Task LeftJoinUsesConfiguredSourceIdentitiesAndProducesNullsForMissingMatch()
    {
        var left = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Name", TabularDataType.String, false)
            ]),
            [
                new([TabularValue.From(1L), TabularValue.From("One")]),
                new([TabularValue.From(2L), TabularValue.From("Two")])
            ]);
        var right = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Code", TabularDataType.String, false)
            ]),
            [new([TabularValue.From(1L), TabularValue.From("A")])]);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "join",
                WorkflowNodeKind.Join,
                "Join",
                0,
                0,
                """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"left","maximumBufferedRows":100}"""),
            [new("left", left), new("right", right)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(["Id", "Name", "right.Id", "Code"], result.Output!.Schema.Columns.Select(column => column.Name));
        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0].Values[3].Value);
        Assert.Equal(TabularValueKind.Null, rows[1].Values[2].Kind);
        Assert.Equal(TabularValueKind.Null, rows[1].Values[3].Kind);
    }

    [Fact]
    public async Task JoinResolvesConfiguredInputIdsCaseInsensitively()
    {
        var schema = new TabularSchema(
            [new("Id", TabularDataType.Int64, false)]);
        var left = DataSet(schema, [new([TabularValue.From(1L)])]);
        var right = DataSet(schema, [new([TabularValue.From(1L)])]);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "join",
                WorkflowNodeKind.Join,
                "Join",
                0,
                0,
                """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"inner","maximumBufferedRows":100}"""),
            [new("LEFT", left), new("Right", right)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Single(rows);
    }

    [Fact]
    public async Task JoinSchemaTruncatesAndSuffixesCollidingNames()
    {
        var sourceName = new string('x', TabularColumn.MaximumNameLength);
        var truncatedPrefix = $"right.{sourceName}"[..TabularColumn.MaximumNameLength];
        var expected = $"{truncatedPrefix[..^2]}_2";
        var left = DataSet(
            new TabularSchema(
            [
                new(sourceName, TabularDataType.Int64, false),
                new(truncatedPrefix, TabularDataType.String, false)
            ]),
            []);
        var right = DataSet(
            new TabularSchema([new(sourceName, TabularDataType.Int64, false)]),
            []);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "join",
                WorkflowNodeKind.Join,
                "Join",
                0,
                0,
                $$"""{"leftNodeId":"left","rightNodeId":"right","leftKeys":["{{sourceName}}"],"rightKeys":["{{sourceName}}"],"type":"full","maximumBufferedRows":100}"""),
            [new("left", left), new("right", right)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(expected, result.Output!.Schema.Columns[^1].Name);
        Assert.All(result.Output.Schema.Columns, column => Assert.True(column.IsNullable));
    }

    [Fact]
    public async Task UnionByNameReordersColumnsAndKeepsAllRows()
    {
        var first = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Name", TabularDataType.String, false)
            ]),
            [new([TabularValue.From(1L), TabularValue.From("One")])]);
        var second = DataSet(
            new TabularSchema(
            [
                new("name", TabularDataType.String),
                new("ID", TabularDataType.Int64, false)
            ]),
            [new([TabularValue.From("Two"), TabularValue.From(2L)])]);
        var executor = new UnionRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "union",
                WorkflowNodeKind.UnionRows,
                "Union",
                0,
                0,
                """{"inputNodeIds":["first","second"],"matchBy":"name","mode":"all"}"""),
            [new("second", second), new("first", first)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(["Id", "Name"], result.Output!.Schema.Columns.Select(column => column.Name));
        Assert.False(result.Output.Schema.Columns[0].IsNullable);
        Assert.True(result.Output.Schema.Columns[1].IsNullable);
        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal(1L, row.Values[0].Value);
                Assert.Equal("One", row.Values[1].Value);
            },
            row =>
            {
                Assert.Equal(2L, row.Values[0].Value);
                Assert.Equal("Two", row.Values[1].Value);
            });
    }

    [Fact]
    public async Task UnionDistinctRemovesDuplicateRowsAcrossInputs()
    {
        var schema = new TabularSchema(
        [
            new("Id", TabularDataType.Int64, false),
            new("Value", TabularDataType.String)
        ]);
        var first = DataSet(
            schema,
            [
                new([TabularValue.From(1L), TabularValue.Null]),
                new([TabularValue.From(2L), TabularValue.From("a:b|c")])
            ]);
        var second = DataSet(
            schema,
            [
                new([TabularValue.From(1L), TabularValue.Null]),
                new([TabularValue.From(3L), TabularValue.From("a:b|c")])
            ]);
        var executor = new UnionRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "union",
                WorkflowNodeKind.UnionRows,
                "Union",
                0,
                0,
                """{"inputNodeIds":["first","second"],"matchBy":"position","mode":"distinct"}"""),
            [new("first", first), new("second", second)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal([1L, 2L, 3L], rows.Select(row => (long)row.Values[0].Value!));
    }

    [Fact]
    public async Task UnionDistinctUsesStructuralTypedEquality()
    {
        var schema = EquivalentValueSchema();
        var first = DataSet(schema, [EquivalentValueRow(useAlternateRepresentation: false)]);
        var second = DataSet(schema, [EquivalentValueRow(useAlternateRepresentation: true)]);
        var executor = new UnionRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "union",
                    WorkflowNodeKind.UnionRows,
                    "Union",
                    0,
                    0,
                    """{"inputNodeIds":["first","second"],"matchBy":"position","mode":"distinct"}"""),
                [new("first", first), new("second", second)]),
            CancellationToken.None);

        Assert.Single(await ReadRowsAsync(result.Output!));
    }

    [Fact]
    public void StructuralKeyHashingPreservesEqualityAndSeparatesLegacyInt64Collisions()
    {
        var hasher = new TransformRowKeyHasher(seed: 12345);
        var equivalentIndexes = Enumerable.Range(0, EquivalentValueSchema().Columns.Count).ToArray();

        Assert.Equal(
            hasher.GetHashCode(
                EquivalentValueRow(useAlternateRepresentation: false),
                equivalentIndexes),
            hasher.GetHashCode(
                EquivalentValueRow(useAlternateRepresentation: true),
                equivalentIndexes));

        long[] legacyCollisions =
        [
            0,
            0x0000000100000001,
            0x0000000200000002,
            0x0000000300000003
        ];
        Assert.Single(legacyCollisions.Select(value => value.GetHashCode()).Distinct());

        var hashes = legacyCollisions
            .Select(value => hasher.GetHashCode(
                new TabularRow([TabularValue.From(value)]),
                [0]))
            .ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void StructuralKeySetObservesCancellationDuringForcedCollisionScan()
    {
        var keys = new TransformStructuralKeySet(static (_, _) => 0);
        for (var value = 0L; value < 512; value++)
        {
            Assert.Equal(
                TransformKeyAddResult.Added,
                keys.TryAdd(
                    new TabularRow([TabularValue.From(value)]),
                    [0],
                    maximumKeys: 1_000,
                    CancellationToken.None));
        }

        using var cancellation = new CancellationTokenSource();
        var indexes = new CancellationTriggeringIndexes(0);
        indexes.Arm(cancellation, readsBeforeCancellation: 65);

        Assert.Throws<OperationCanceledException>(() =>
            keys.TryAdd(
                new TabularRow([TabularValue.From(-1L)]),
                indexes,
                maximumKeys: 1_000,
                cancellation.Token));
    }

    [Fact]
    public async Task UnionRejectsInputAndSchemaMismatchWithStableCodes()
    {
        var first = DataSet(
            new TabularSchema([new("Id", TabularDataType.Int64, false)]),
            []);
        var second = DataSet(
            new TabularSchema([new("Id", TabularDataType.String, false)]),
            []);
        var executor = new UnionRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var node = new WorkflowNode(
            "union",
            WorkflowNodeKind.UnionRows,
            "Union",
            0,
            0,
            """{"inputNodeIds":["first","second"],"matchBy":"name","mode":"all"}""");

        var inputException = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => executor.ExecuteAsync(
                Context(node, [new("first", first), new("other", second)]),
                CancellationToken.None).AsTask());
        Assert.Equal("execution.union.input", inputException.Error.Code);

        var schemaException = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => executor.ExecuteAsync(
                Context(node, [new("first", first), new("second", second)]),
                CancellationToken.None).AsTask());
        Assert.Equal("execution.union.schema", schemaException.Error.Code);
    }

    [Fact]
    public async Task DeriveColumnsEvaluatesTypedExpressionWithVariables()
    {
        var input = DataSet(
            new TabularSchema([new("Amount", TabularDataType.Int64, false)]),
            [new([TabularValue.From(7L)])]);
        var executor = new DeriveColumnsNodeExecutor(
            new WorkflowNodeSettingsValidator(),
            new JintWorkflowExpressionSessionFactory());
        var context = Context(
            new(
                "derive",
                WorkflowNodeKind.DeriveColumns,
                "Derive",
                0,
                0,
                """{"columns":[{"name":"Adjusted","type":"Int64","nullable":false,"expression":"row.Amount + vars.increment"}]}"""),
            [new("source", input)],
            new Dictionary<string, TabularValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["increment"] = TabularValue.From(5L)
            });

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var row = Assert.Single(await ReadRowsAsync(result.Output!));

        Assert.Equal(["Amount", "Adjusted"], result.Output!.Schema.Columns.Select(column => column.Name));
        Assert.Equal(7L, row.Values[0].Value);
        Assert.Equal(12L, row.Values[1].Value);
    }

    [Fact]
    public async Task DeriveColumnsRejectsInputCollisionAndNonnullableNull()
    {
        var input = DataSet(
            new TabularSchema([new("Amount", TabularDataType.Int64, false)]),
            [new([TabularValue.From(7L)])]);
        var executor = new DeriveColumnsNodeExecutor(
            new WorkflowNodeSettingsValidator(),
            new JintWorkflowExpressionSessionFactory());

        var duplicate = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => executor.ExecuteAsync(
                Context(
                    new(
                        "derive",
                        WorkflowNodeKind.DeriveColumns,
                        "Derive",
                        0,
                        0,
                        """{"columns":[{"name":"amount","type":"Int64","nullable":false,"expression":"1"}]}"""),
                    [new("source", input)]),
                CancellationToken.None).AsTask());
        Assert.Equal("execution.derive.column.duplicate", duplicate.Error.Code);

        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "derive",
                    WorkflowNodeKind.DeriveColumns,
                    "Derive",
                    0,
                    0,
                    """{"columns":[{"name":"Missing","type":"String","nullable":false,"expression":"null"}]}"""),
                [new("source", input)]),
            CancellationToken.None);
        var nullException = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => ReadRowsAsync(result.Output!));
        Assert.Equal("execution.derive.value.null", nullException.Error.Code);
    }

    [Fact]
    public async Task AggregateGroupsAndComputesCountSumAverageMinimumAndMaximum()
    {
        var input = DataSet(
            new TabularSchema(
            [
                new("Category", TabularDataType.String, false),
                new("Amount", TabularDataType.Int64)
            ]),
            [
                new([TabularValue.From("A"), TabularValue.From(2L)]),
                new([TabularValue.From("A"), TabularValue.From(4L)]),
                new([TabularValue.From("A"), TabularValue.Null]),
                new([TabularValue.From("B"), TabularValue.From(9L)])
            ]);
        var executor = new AggregateRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "aggregate",
                WorkflowNodeKind.AggregateRows,
                "Aggregate",
                0,
                0,
                """
                {"groupBy":["Category"],"aggregates":[
                  {"name":"Rows","operation":"count"},
                  {"name":"Present","operation":"countNonNull","column":"Amount"},
                  {"name":"Total","operation":"sum","column":"Amount"},
                  {"name":"Mean","operation":"average","column":"Amount"},
                  {"name":"Lowest","operation":"minimum","column":"Amount"},
                  {"name":"Highest","operation":"maximum","column":"Amount"}
                ],"maximumGroups":10}
                """),
            [new("source", input)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(
            ["Category", "Rows", "Present", "Total", "Mean", "Lowest", "Highest"],
            result.Output!.Schema.Columns.Select(column => column.Name));
        Assert.Equal(
            [
                TabularDataType.String,
                TabularDataType.Int64,
                TabularDataType.Int64,
                TabularDataType.Int64,
                TabularDataType.Decimal,
                TabularDataType.Int64,
                TabularDataType.Int64
            ],
            result.Output.Schema.Columns.Select(column => column.DataType));
        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("A", row.Values[0].Value);
                Assert.Equal(3L, row.Values[1].Value);
                Assert.Equal(2L, row.Values[2].Value);
                Assert.Equal(6L, row.Values[3].Value);
                Assert.Equal(3m, row.Values[4].Value);
                Assert.Equal(2L, row.Values[5].Value);
                Assert.Equal(4L, row.Values[6].Value);
            },
            row => Assert.Equal("B", row.Values[0].Value));
    }

    [Fact]
    public async Task AggregateEnforcesGroupLimitAndNumericOperationTypes()
    {
        var input = DataSet(
            new TabularSchema([new("Name", TabularDataType.String, false)]),
            [
                new([TabularValue.From("A")]),
                new([TabularValue.From("B")])
            ]);
        var executor = new AggregateRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var limited = await executor.ExecuteAsync(
            Context(
                new(
                    "aggregate",
                    WorkflowNodeKind.AggregateRows,
                    "Aggregate",
                    0,
                    0,
                    """{"groupBy":["Name"],"aggregates":[{"name":"Rows","operation":"count"}],"maximumGroups":1}"""),
                [new("source", input)]),
            CancellationToken.None);
        var limitException = await Assert.ThrowsAsync<TabularLimitExceededException>(
            () => ReadRowsAsync(limited.Output!));
        Assert.Equal("execution.aggregate.group.limit", limitException.Code);

        var typeException = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => executor.ExecuteAsync(
                Context(
                    new(
                        "aggregate",
                        WorkflowNodeKind.AggregateRows,
                        "Aggregate",
                        0,
                        0,
                        """{"groupBy":[],"aggregates":[{"name":"Total","operation":"sum","column":"Name"}],"maximumGroups":1}"""),
                    [new("source", input)]),
                CancellationToken.None).AsTask());
        Assert.Equal("execution.aggregate.operation.type", typeException.Error.Code);
    }

    [Fact]
    public async Task AggregateEmptyInputUsesGroupedAndGlobalSemantics()
    {
        var input = DataSet(
            new TabularSchema(
            [
                new("Category", TabularDataType.String, false),
                new("Amount", TabularDataType.Decimal)
            ]),
            []);
        var executor = new AggregateRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var grouped = await executor.ExecuteAsync(
            Context(
                new(
                    "grouped",
                    WorkflowNodeKind.AggregateRows,
                    "Grouped",
                    0,
                    0,
                    """{"groupBy":["Category"],"aggregates":[{"name":"Rows","operation":"count"}],"maximumGroups":10}"""),
                [new("source", input)]),
            CancellationToken.None);
        Assert.Empty(await ReadRowsAsync(grouped.Output!));

        var global = await executor.ExecuteAsync(
            Context(
                new(
                    "global",
                    WorkflowNodeKind.AggregateRows,
                    "Global",
                    0,
                    0,
                    """{"groupBy":[],"aggregates":[{"name":"Rows","operation":"count"},{"name":"Present","operation":"countNonNull","column":"Amount"},{"name":"Total","operation":"sum","column":"Amount"},{"name":"Mean","operation":"average","column":"Amount"}],"maximumGroups":10}"""),
                [new("source", input)]),
            CancellationToken.None);
        var row = Assert.Single(await ReadRowsAsync(global.Output!));
        Assert.Equal(0L, row.Values[0].Value);
        Assert.Equal(0L, row.Values[1].Value);
        Assert.Equal(TabularValueKind.Null, row.Values[2].Kind);
        Assert.Equal(TabularValueKind.Null, row.Values[3].Kind);
    }

    [Fact]
    public async Task AggregateGroupingUsesStructuralTypedEquality()
    {
        var input = DataSet(
            EquivalentValueSchema(),
            [
                EquivalentValueRow(useAlternateRepresentation: false),
                EquivalentValueRow(useAlternateRepresentation: true)
            ]);
        var executor = new AggregateRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "aggregate",
                    WorkflowNodeKind.AggregateRows,
                    "Aggregate",
                    0,
                    0,
                    """{"groupBy":["DecimalValue","Instant","SignedZero","NullableValue","BinaryValue"],"aggregates":[{"name":"Rows","operation":"count"}],"maximumGroups":10}"""),
                [new("source", input)]),
            CancellationToken.None);
        var row = Assert.Single(await ReadRowsAsync(result.Output!));

        Assert.Equal(2L, row.Values[^1].Value);
    }

    [Fact]
    public async Task AggregateNormalizesInt64AndDecimalSumOverflow()
    {
        var executor = new AggregateRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var int64Result = await executor.ExecuteAsync(
            Context(
                AggregateNode("sum", "Value"),
                [
                    new(
                        "source",
                        DataSet(
                            new TabularSchema([new("Value", TabularDataType.Int64, false)]),
                            [
                                new([TabularValue.From(long.MaxValue)]),
                                new([TabularValue.From(1L)])
                            ]))
                ]),
            CancellationToken.None);
        var int64Exception = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => ReadRowsAsync(int64Result.Output!));
        Assert.Equal("execution.aggregate.numeric.overflow", int64Exception.Error.Code);

        var decimalResult = await executor.ExecuteAsync(
            Context(
                AggregateNode("sum", "Value"),
                [
                    new(
                        "source",
                        DataSet(
                            new TabularSchema([new("Value", TabularDataType.Decimal, false)]),
                            [
                                new([TabularValue.From(decimal.MaxValue)]),
                                new([TabularValue.From(1m)])
                            ]))
                ]),
            CancellationToken.None);
        var decimalException = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => ReadRowsAsync(decimalResult.Output!));
        Assert.Equal("execution.aggregate.numeric.overflow", decimalException.Error.Code);
    }

    [Fact]
    public async Task AggregateDecimalAverageAvoidsRepresentableIntermediateOverflow()
    {
        var input = DataSet(
            new TabularSchema([new("Value", TabularDataType.Decimal, false)]),
            [
                new([TabularValue.From(decimal.MaxValue)]),
                new([TabularValue.From(decimal.MaxValue)])
            ]);
        var executor = new AggregateRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(AggregateNode("average", "Value"), [new("source", input)]),
            CancellationToken.None);
        var row = Assert.Single(await ReadRowsAsync(result.Output!));

        Assert.Equal(decimal.MaxValue, row.Values[0].Value);
    }

    [Theory]
    [InlineData("sum", false)]
    [InlineData("average", true)]
    public async Task AggregateRejectsNonFiniteDoubleResults(string operation, bool useNaN)
    {
        var values = useNaN
            ? new[] { double.NaN }
            : [double.MaxValue, double.MaxValue];
        var input = DataSet(
            new TabularSchema([new("Value", TabularDataType.Double, false)]),
            values.Select(value => new TabularRow([TabularValue.From(value)])).ToArray());
        var executor = new AggregateRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(AggregateNode(operation, "Value"), [new("source", input)]),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WorkflowNodeExecutionException>(
            () => ReadRowsAsync(result.Output!));
        Assert.Equal("execution.aggregate.numeric.overflow", exception.Error.Code);
    }

    [Fact]
    public async Task DistinctRowsUsesConfiguredKeyColumns()
    {
        var input = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Version", TabularDataType.Int64, false)
            ]),
            [
                new([TabularValue.From(1L), TabularValue.From(1L)]),
                new([TabularValue.From(1L), TabularValue.From(2L)]),
                new([TabularValue.From(2L), TabularValue.From(1L)])
            ]);
        var executor = new DistinctRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "distinct",
                    WorkflowNodeKind.DistinctRows,
                    "Distinct",
                    0,
                    0,
                    """{"columns":["Id"],"maximumKeys":10}"""),
                [new("source", input)]),
            CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal([1L, 2L], rows.Select(row => (long)row.Values[0].Value!));
        Assert.Equal(1L, rows[0].Values[1].Value);
    }

    [Fact]
    public async Task DistinctRowsEnforcesMaximumKeys()
    {
        var input = DataSet(
            new TabularSchema([new("Id", TabularDataType.Int64, false)]),
            [
                new([TabularValue.From(1L)]),
                new([TabularValue.From(2L)])
            ]);
        var executor = new DistinctRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "distinct",
                    WorkflowNodeKind.DistinctRows,
                    "Distinct",
                    0,
                    0,
                    """{"maximumKeys":1}"""),
                [new("source", input)]),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TabularLimitExceededException>(
            () => ReadRowsAsync(result.Output!));
        Assert.Equal("execution.distinct.key.limit", exception.Code);
    }

    [Fact]
    public async Task DistinctRowsUsesStructuralTypedEquality()
    {
        var input = DataSet(
            EquivalentValueSchema(),
            [
                EquivalentValueRow(useAlternateRepresentation: false),
                EquivalentValueRow(useAlternateRepresentation: true)
            ]);
        var executor = new DistinctRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "distinct",
                    WorkflowNodeKind.DistinctRows,
                    "Distinct",
                    0,
                    0,
                    """{"maximumKeys":10}"""),
                [new("source", input)]),
            CancellationToken.None);

        Assert.Single(await ReadRowsAsync(result.Output!));
    }

    [Fact]
    public async Task SortRowsAppliesMultipleKeysAndNullPlacement()
    {
        var input = DataSet(
            new TabularSchema(
            [
                new("Group", TabularDataType.String, false),
                new("Rank", TabularDataType.Int64),
                new("Sequence", TabularDataType.Int64, false)
            ]),
            [
                new([TabularValue.From("B"), TabularValue.From(2L), TabularValue.From(1L)]),
                new([TabularValue.From("A"), TabularValue.Null, TabularValue.From(2L)]),
                new([TabularValue.From("A"), TabularValue.From(3L), TabularValue.From(3L)]),
                new([TabularValue.From("A"), TabularValue.From(3L), TabularValue.From(4L)])
            ]);
        var executor = new SortRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "sort",
                    WorkflowNodeKind.SortRows,
                    "Sort",
                    0,
                    0,
                    """{"keys":[{"column":"Group","direction":"ascending","nulls":"last"},{"column":"Rank","direction":"descending","nulls":"first"}],"maximumBufferedRows":10}"""),
                [new("source", input)]),
            CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal([2L, 3L, 4L, 1L], rows.Select(row => (long)row.Values[2].Value!));
    }

    [Fact]
    public async Task SortRowsEnforcesBufferLimit()
    {
        var input = DataSet(
            new TabularSchema([new("Id", TabularDataType.Int64, false)]),
            [
                new([TabularValue.From(1L)]),
                new([TabularValue.From(2L)])
            ]);
        var executor = new SortRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "sort",
                    WorkflowNodeKind.SortRows,
                    "Sort",
                    0,
                    0,
                    """{"keys":[{"column":"Id","direction":"ascending","nulls":"last"}],"maximumBufferedRows":1}"""),
                [new("source", input)]),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TabularLimitExceededException>(
            () => ReadRowsAsync(result.Output!));
        Assert.Equal("execution.sort.buffer.limit", exception.Code);
    }

    [Fact]
    public async Task SortRowsObservesCancellationAfterCpuSortComparisonsBegin()
    {
        using var cancellation = new CancellationTokenSource();
        var rows = Enumerable.Range(0, 20_000)
            .Select(index => new TabularRow([TabularValue.From((long)(20_000 - index))]))
            .ToArray();
        var input = DataSet(
            new TabularSchema([new("Value", TabularDataType.Int64, false)]),
            rows);
        var comparisons = 0;
        var executor = new SortRowsNodeExecutor(
            new WorkflowNodeSettingsValidator(),
            comparisonCount =>
            {
                comparisons = comparisonCount;
                if (comparisonCount == 1)
                {
                    cancellation.Cancel();
                }
            });
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "sort",
                    WorkflowNodeKind.SortRows,
                    "Sort",
                    0,
                    0,
                    """{"keys":[{"column":"Value","direction":"ascending","nulls":"last"}],"maximumBufferedRows":20000}"""),
                [new("source", input)]),
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadRowsAsync(result.Output!, cancellation.Token));
        Assert.True(comparisons >= 1);
    }

    [Fact]
    public async Task FullJoinEmitsUnmatchedRowsFromBothInputs()
    {
        var left = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("LeftValue", TabularDataType.String, false)
            ]),
            [
                new([TabularValue.From(1L), TabularValue.From("one")]),
                new([TabularValue.From(2L), TabularValue.From("two")])
            ]);
        var right = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("RightValue", TabularDataType.String, false)
            ]),
            [
                new([TabularValue.From(1L), TabularValue.From("matched")]),
                new([TabularValue.From(1L), TabularValue.From("matched-again")]),
                new([TabularValue.From(3L), TabularValue.From("three")])
            ]);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "join",
                    WorkflowNodeKind.Join,
                    "Join",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"full","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(4, rows.Count);
        Assert.All(result.Output!.Schema.Columns, column => Assert.True(column.IsNullable));
        Assert.Equal(TabularValueKind.Null, rows[2].Values[2].Kind);
        Assert.Equal(TabularValueKind.Null, rows[3].Values[0].Kind);
        Assert.Equal(3L, rows[3].Values[2].Value);
    }

    [Fact]
    public async Task FullJoinUsesStructuralTypedKeyEqualityWithoutReemittingMatch()
    {
        var left = DataSet(
            new TabularSchema(
            [
                new("DecimalKey", TabularDataType.Decimal, false),
                new("InstantKey", TabularDataType.DateTimeOffset, false),
                new("ZeroKey", TabularDataType.Double, false),
                new("LeftValue", TabularDataType.String, false)
            ]),
            [
                new(
                [
                    TabularValue.From(1.0m),
                    TabularValue.From(new DateTimeOffset(
                        2026,
                        7,
                        26,
                        8,
                        0,
                        0,
                        TimeSpan.FromHours(8))),
                    TabularValue.From(+0d),
                    TabularValue.From("left")
                ])
            ]);
        var right = DataSet(
            new TabularSchema(
            [
                new("DecimalKey", TabularDataType.Decimal, false),
                new("InstantKey", TabularDataType.DateTimeOffset, false),
                new("ZeroKey", TabularDataType.Double, false),
                new("RightValue", TabularDataType.String, false)
            ]),
            [
                new(
                [
                    TabularValue.From(1.00m),
                    TabularValue.From(new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        0,
                        TimeSpan.Zero)),
                    TabularValue.From(-0d),
                    TabularValue.From("matched")
                ]),
                new(
                [
                    TabularValue.From(2m),
                    TabularValue.From(new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        0,
                        TimeSpan.Zero)),
                    TabularValue.From(0d),
                    TabularValue.From("unmatched")
                ])
            ]);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "join",
                    WorkflowNodeKind.Join,
                    "Join",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["DecimalKey","InstantKey","ZeroKey"],"rightKeys":["DecimalKey","InstantKey","ZeroKey"],"type":"full","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(2, rows.Count);
        Assert.Equal("matched", rows[0].Values[7].Value);
        Assert.Equal(TabularValueKind.Null, rows[1].Values[0].Kind);
        Assert.Equal("unmatched", rows[1].Values[7].Value);
    }

    [Fact]
    public async Task RightJoinEmitsMatchedDuplicatesThenUnmatchedRightRowsInSourceOrder()
    {
        var left = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("LeftValue", TabularDataType.String, false)
            ]),
            [
                new([TabularValue.From(1L), TabularValue.From("first-left")]),
                new([TabularValue.From(1L), TabularValue.From("second-left")]),
                new([TabularValue.From(4L), TabularValue.From("left-only")])
            ]);
        var right = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("RightValue", TabularDataType.String, false)
            ]),
            [
                new([TabularValue.From(1L), TabularValue.From("first-right")]),
                new([TabularValue.From(1L), TabularValue.From("second-right")]),
                new([TabularValue.From(3L), TabularValue.From("right-only")])
            ]);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "join",
                    WorkflowNodeKind.Join,
                    "Join",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"right","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            ["Id", "LeftValue", "right.Id", "RightValue"],
            result.Output!.Schema.Columns.Select(column => column.Name));
        Assert.Equal(
            [
                ("first-left", "first-right"),
                ("first-left", "second-right"),
                ("second-left", "first-right"),
                ("second-left", "second-right")
            ],
            rows.Take(4).Select(row => (
                (string)row.Values[1].Value!,
                (string)row.Values[3].Value!)));
        Assert.Equal(TabularValueKind.Null, rows[4].Values[0].Kind);
        Assert.Equal("right-only", rows[4].Values[3].Value);
    }

    [Fact]
    public async Task RightJoinDoesNotMatchNullKeysAndUsesStructuralTypedKeyEquality()
    {
        var schema = new TabularSchema(
        [
            new("DecimalKey", TabularDataType.Decimal),
            new("InstantKey", TabularDataType.DateTimeOffset),
            new("ZeroKey", TabularDataType.Double),
            new("Value", TabularDataType.String, false)
        ]);
        var left = DataSet(
            schema,
            [
                new(
                [
                    TabularValue.From(1.0m),
                    TabularValue.From(new DateTimeOffset(
                        2026,
                        7,
                        26,
                        8,
                        0,
                        0,
                        TimeSpan.FromHours(8))),
                    TabularValue.From(+0d),
                    TabularValue.From("left-match")
                ]),
                new(
                [
                    TabularValue.Null,
                    TabularValue.From(DateTimeOffset.UnixEpoch),
                    TabularValue.From(0d),
                    TabularValue.From("left-null")
                ])
            ]);
        var right = DataSet(
            schema,
            [
                new(
                [
                    TabularValue.From(1.00m),
                    TabularValue.From(new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        0,
                        TimeSpan.Zero)),
                    TabularValue.From(-0d),
                    TabularValue.From("right-match")
                ]),
                new(
                [
                    TabularValue.Null,
                    TabularValue.From(DateTimeOffset.UnixEpoch),
                    TabularValue.From(0d),
                    TabularValue.From("right-null")
                ])
            ]);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "join",
                    WorkflowNodeKind.Join,
                    "Join",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["DecimalKey","InstantKey","ZeroKey"],"rightKeys":["DecimalKey","InstantKey","ZeroKey"],"type":"right","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(2, rows.Count);
        Assert.Equal("left-match", rows[0].Values[3].Value);
        Assert.Equal("right-match", rows[0].Values[7].Value);
        Assert.Equal(TabularValueKind.Null, rows[1].Values[0].Kind);
        Assert.Equal("right-null", rows[1].Values[7].Value);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public async Task RightJoinHandlesEmptySides(bool emptyLeft, int expectedRows)
    {
        var schema = new TabularSchema([new("Id", TabularDataType.Int64, false)]);
        var left = DataSet(
            schema,
            emptyLeft
                ? []
                : [new([TabularValue.From(1L)])]);
        var right = DataSet(
            schema,
            emptyLeft
                ?
                [
                    new([TabularValue.From(1L)]),
                    new([TabularValue.From(2L)])
                ]
                : []);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var result = await executor.ExecuteAsync(
            Context(
                new(
                    "join",
                    WorkflowNodeKind.Join,
                    "Join",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"right","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(expectedRows, rows.Count);
        Assert.All(rows, row => Assert.Equal(TabularValueKind.Null, row.Values[0].Kind));
    }

    [Fact]
    public async Task JoinSchemaNullabilityMatchesJoinType()
    {
        var left = DataSet(
            new TabularSchema([new("LeftId", TabularDataType.Int64, false)]),
            []);
        var right = DataSet(
            new TabularSchema([new("RightId", TabularDataType.Int64, false)]),
            []);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());

        var inner = await executor.ExecuteAsync(
            Context(
                new(
                    "inner",
                    WorkflowNodeKind.Join,
                    "Inner",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["LeftId"],"rightKeys":["RightId"],"type":"inner","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        Assert.All(inner.Output!.Schema.Columns, column => Assert.False(column.IsNullable));

        var outer = await executor.ExecuteAsync(
            Context(
                new(
                    "left-join",
                    WorkflowNodeKind.Join,
                    "Left",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["LeftId"],"rightKeys":["RightId"],"type":"left","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        Assert.False(outer.Output!.Schema.Columns[0].IsNullable);
        Assert.True(outer.Output.Schema.Columns[1].IsNullable);

        var rightOuter = await executor.ExecuteAsync(
            Context(
                new(
                    "right-join",
                    WorkflowNodeKind.Join,
                    "Right",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["LeftId"],"rightKeys":["RightId"],"type":"right","maximumBufferedRows":100}"""),
                [new("left", left), new("right", right)]),
            CancellationToken.None);
        Assert.True(rightOuter.Output!.Schema.Columns[0].IsNullable);
        Assert.False(rightOuter.Output.Schema.Columns[1].IsNullable);
    }

    private static WorkflowNodeExecutionContext Context(
        WorkflowNode node,
        IReadOnlyList<WorkflowNodeInput> inputs,
        IReadOnlyDictionary<string, TabularValue>? variables = null,
        IWorkflowNodeProgressReporter? progress = null) => new(
            Guid.NewGuid(),
            node,
            1,
            inputs,
            new TabularExecutionLimits(),
            progress ?? new RecordingProgress(),
            variables);

    private static TabularSchema EquivalentValueSchema() => new(
    [
        new("DecimalValue", TabularDataType.Decimal, false),
        new("Instant", TabularDataType.DateTimeOffset, false),
        new("SignedZero", TabularDataType.Double, false),
        new("NullableValue", TabularDataType.String),
        new("BinaryValue", TabularDataType.Binary, false)
    ]);

    private static TabularRow EquivalentValueRow(bool useAlternateRepresentation) => new(
    [
        TabularValue.From(useAlternateRepresentation ? 1.00m : 1.0m),
        TabularValue.From(useAlternateRepresentation
            ? new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.FromHours(8))),
        TabularValue.From(useAlternateRepresentation ? -0d : +0d),
        TabularValue.Null,
        TabularValue.From(new byte[] { 0, 1, 2, 255 })
    ]);

    private static WorkflowNode AggregateNode(string operation, string column) => new(
        "aggregate",
        WorkflowNodeKind.AggregateRows,
        "Aggregate",
        0,
        0,
        $$"""{"groupBy":[],"aggregates":[{"name":"Result","operation":"{{operation}}","column":"{{column}}"}],"maximumGroups":1}""");

    private static ITabularDataSet DataSet(TabularSchema schema, IReadOnlyList<TabularRow> rows) =>
        new TestDataSet(schema, rows);

    private static async Task<IReadOnlyList<TabularRow>> ReadRowsAsync(
        WorkflowNodeOutput output,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<TabularRow>();
        await foreach (var batch in output.Batches.WithCancellation(cancellationToken))
        {
            await using (batch)
            {
                rows.AddRange(batch.Rows);
            }
        }

        return rows;
    }

    private sealed class RecordingProgress : IWorkflowNodeProgressReporter
    {
        public ValueTask ReportAsync(
            WorkflowNodeProgress delta,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delta.Validate();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationTriggeringIndexes(params int[] indexes)
        : IReadOnlyList<int>
    {
        private CancellationTokenSource? cancellation;
        private int remainingReads;

        public int Count => indexes.Length;

        public int this[int index]
        {
            get
            {
                if (cancellation is not null && --remainingReads == 0)
                {
                    cancellation.Cancel();
                }

                return indexes[index];
            }
        }

        public void Arm(
            CancellationTokenSource cancellationSource,
            int readsBeforeCancellation)
        {
            cancellation = cancellationSource;
            remainingReads = readsBeforeCancellation;
        }

        public IEnumerator<int> GetEnumerator() =>
            ((IEnumerable<int>)indexes).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class TestDataSet(TabularSchema schema, IReadOnlyList<TabularRow> rows)
        : ITabularDataSet
    {
        public TabularSchema Schema { get; } = schema;

        public long RowCount => rows.Count;

        public long ByteCount => 0;

        public async IAsyncEnumerable<TabularBatch> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TabularBatch(rows);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
