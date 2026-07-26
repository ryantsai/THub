using System.Text.Json;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Workflows;
using THub.Web.Components.WorkflowDesigner;

namespace THub.Web.Tests;

public sealed class TransformEditorModelsTests
{
    public static TheoryData<WorkflowNodeKind, string> CanonicalSettings =>
        new()
        {
            {
                WorkflowNodeKind.SelectColumns,
                """{"columns":["CustomerId","Name"]}"""
            },
            {
                WorkflowNodeKind.FilterRows,
                """{"conditions":[{"column":"Status","operator":"equals","value":"Ready"},{"column":"DeletedAt","operator":"isNull"}]}"""
            },
            {
                WorkflowNodeKind.Join,
                """{"leftNodeId":"orders","rightNodeId":"customers","leftKeys":["CustomerId"],"rightKeys":["Id"],"type":"right","maximumBufferedRows":25000}"""
            },
            {
                WorkflowNodeKind.UnionRows,
                """{"inputNodeIds":["north","south"],"matchBy":"position","mode":"distinct"}"""
            },
            {
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Quantity * row.Price"}]}"""
            },
            {
                WorkflowNodeKind.AggregateRows,
                """{"groupBy":["Region"],"aggregates":[{"name":"Rows","operation":"count"},{"name":"Total","operation":"sum","column":"Amount"}],"maximumGroups":5000}"""
            },
            {
                WorkflowNodeKind.DistinctRows,
                """{"columns":["CustomerId"],"maximumKeys":75000}"""
            },
            {
                WorkflowNodeKind.SortRows,
                """{"keys":[{"column":"CreatedAt","direction":"descending","nulls":"last"}],"maximumBufferedRows":90000}"""
            },
        };

    [Theory]
    [MemberData(nameof(CanonicalSettings))]
    public void Codec_RoundTripsCanonicalSettings(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        var parsed = TransformEditorSettingsCodec.Parse(kind, settingsJson);

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.True(parsed.IsPublishable, parsed.ValidationError);
        Assert.NotNull(parsed.Settings);
        var serialized = SerializeValid(parsed.Settings);
        Assert.Equal(settingsJson, serialized);
        var reloaded = TransformEditorSettingsCodec.Parse(kind, serialized);
        Assert.True(reloaded.IsSuccess, reloaded.Error);
        Assert.True(reloaded.IsPublishable, reloaded.ValidationError);
        var parsedSettings = new WorkflowNodeSettingsValidator().Parse(
            new(
                "transform",
                kind,
                "Transform",
                0,
                0,
                serialized));
        var parsedKind = parsedSettings switch
        {
            SelectColumnsNodeSettings => WorkflowNodeKind.SelectColumns,
            FilterRowsNodeSettings => WorkflowNodeKind.FilterRows,
            JoinNodeSettings => WorkflowNodeKind.Join,
            UnionRowsNodeSettings => WorkflowNodeKind.UnionRows,
            DeriveColumnsNodeSettings => WorkflowNodeKind.DeriveColumns,
            AggregateRowsNodeSettings => WorkflowNodeKind.AggregateRows,
            DistinctRowsNodeSettings => WorkflowNodeKind.DistinctRows,
            SortRowsNodeSettings => WorkflowNodeKind.SortRows,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(
            kind,
            parsedKind);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"columns":[],"unexpected":true}""")]
    [InlineData("""{"columns":"not-an-array"}""")]
    public void Codec_RejectsMalformedOrNonObjectJsonWithoutReplacement(string settingsJson)
    {
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.SelectColumns,
            settingsJson);

        Assert.False(parsed.IsSuccess);
        Assert.Null(parsed.Settings);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
    }

    [Fact]
    public void Codec_OmitsOperatorSpecificProperties()
    {
        var filter = Assert.IsType<FilterRowsEditorSettings>(
            TransformEditorSettingsCodec.Parse(
                WorkflowNodeKind.FilterRows,
                """{"conditions":[{"column":"DeletedAt","operator":"isNotNull","value":"ignored"}]}""")
                .Settings);
        var aggregate = new AggregateRowsEditorSettings
        {
            MaximumGroups = 100,
        };
        aggregate.Aggregates.Add(new()
        {
            Name = "Rows",
            Operation = "count",
            Column = null,
        });
        var distinct = new DistinctRowsEditorSettings
        {
            UseAllColumns = true,
            MaximumKeys = 100,
        };

        Assert.Equal(
            """{"conditions":[{"column":"DeletedAt","operator":"isNotNull"}]}""",
            SerializeValid(filter));
        Assert.Equal(
            """{"groupBy":[],"aggregates":[{"name":"Rows","operation":"count"}],"maximumGroups":100}""",
            SerializeValid(aggregate));
        Assert.Equal(
            """{"maximumKeys":100}""",
            SerializeValid(distinct));
    }

    [Fact]
    public void Codec_AcceptsValidatorCompatibleDataTypeCasingAndCanonicalizesEnumName()
    {
        foreach (var dataType in new[] { "decimal", "DECIMAL", "Decimal" })
        {
            var parsed = TransformEditorSettingsCodec.Parse(
                WorkflowNodeKind.DeriveColumns,
                $$"""{"columns":[{"name":"Total","type":"{{dataType}}","nullable":false,"expression":"row.Amount"}]}""");

            Assert.True(parsed.IsSuccess, parsed.Error);
            Assert.Equal(
                """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Amount"}]}""",
                SerializeValid(parsed.Settings!));
        }
    }

    [Fact]
    public void Codec_TreatsEmptyDistinctColumnsAsAllColumnsAndOmitsProperty()
    {
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.DistinctRows,
            """{"columns":[],"maximumKeys":100}""");
        var settings = Assert.IsType<DistinctRowsEditorSettings>(parsed.Settings);

        Assert.True(settings.UseAllColumns);
        Assert.Empty(settings.Columns);
        Assert.Equal(
            """{"maximumKeys":100}""",
            SerializeValid(settings));
    }

    [Fact]
    public void Codec_PreservesCountAggregateSourceColumnAsNonPublishableDraft()
    {
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.AggregateRows,
            """{"groupBy":[],"aggregates":[{"name":"Rows","operation":"count","column":"ignored"}],"maximumGroups":100}""");
        var constructed = new AggregateRowsEditorSettings
        {
            MaximumGroups = 100,
        };
        constructed.Aggregates.Add(new()
        {
            Name = "Rows",
            Operation = "count",
            Column = "ignored",
        });

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.False(parsed.IsPublishable);
        Assert.False(string.IsNullOrWhiteSpace(parsed.ValidationError));
        var parsedSettings = Assert.IsType<AggregateRowsEditorSettings>(parsed.Settings);
        Assert.Equal("ignored", parsedSettings.Aggregates.Single().Column);
        var serialized = TransformEditorSettingsCodec.Serialize(constructed);
        Assert.True(serialized.IsSuccess, serialized.Error);
        Assert.False(serialized.IsPublishable);
        Assert.False(string.IsNullOrWhiteSpace(serialized.ValidationError));
        Assert.Contains(
            """"column":"ignored"""",
            serialized.SettingsJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_UsesStableUiOnlyRowIdsForEveryRepeatedEditorRow()
    {
        var examples = new (WorkflowNodeKind Kind, string Json, Func<TransformEditorSettings, string> UiId)[]
        {
            (
                WorkflowNodeKind.FilterRows,
                """{"conditions":[{"column":"Status","operator":"equals","value":"Ready"}]}""",
                settings => Assert.IsType<FilterRowsEditorSettings>(settings).Conditions.Single().UiId),
            (
                WorkflowNodeKind.Join,
                """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"inner","maximumBufferedRows":100}""",
                settings => Assert.IsType<JoinEditorSettings>(settings).KeyPairs.Single().UiId),
            (
                WorkflowNodeKind.UnionRows,
                """{"inputNodeIds":["left","right"],"matchBy":"name","mode":"all"}""",
                settings => Assert.IsType<UnionRowsEditorSettings>(settings).Inputs[0].UiId),
            (
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Amount"}]}""",
                settings => Assert.IsType<DeriveColumnsEditorSettings>(settings).Columns.Single().UiId),
            (
                WorkflowNodeKind.AggregateRows,
                """{"groupBy":[],"aggregates":[{"name":"Total","operation":"sum","column":"Amount"}],"maximumGroups":100}""",
                settings => Assert.IsType<AggregateRowsEditorSettings>(settings).Aggregates.Single().UiId),
            (
                WorkflowNodeKind.SortRows,
                """{"keys":[{"column":"CreatedAt","direction":"ascending","nulls":"first"}],"maximumBufferedRows":100}""",
                settings => Assert.IsType<SortRowsEditorSettings>(settings).Keys.Single().UiId),
        };

        foreach (var example in examples)
        {
            var parsed = TransformEditorSettingsCodec.Parse(example.Kind, example.Json);
            var settings = Assert.IsAssignableFrom<TransformEditorSettings>(parsed.Settings);
            var uiId = example.UiId(settings);
            var serialized = SerializeValid(settings);

            Assert.Equal(uiId, example.UiId(settings));
            Assert.DoesNotContain("uiId", serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Join_SwapInputsSwapsIdsAndKeyColumns()
    {
        var settings = Assert.IsType<JoinEditorSettings>(
            TransformEditorSettingsCodec.Parse(
                WorkflowNodeKind.Join,
                """{"leftNodeId":"orders","rightNodeId":"customers","leftKeys":["CustomerId","RegionId"],"rightKeys":["Id","RegionId"],"type":"left","maximumBufferedRows":1000}""")
                .Settings);
        var rowIds = settings.KeyPairs.Select(pair => pair.UiId).ToArray();

        settings.SwapInputs();

        Assert.Equal("customers", settings.LeftNodeId);
        Assert.Equal("orders", settings.RightNodeId);
        Assert.Equal(["Id", "RegionId"], settings.KeyPairs.Select(pair => pair.LeftKey));
        Assert.Equal(["CustomerId", "RegionId"], settings.KeyPairs.Select(pair => pair.RightKey));
        Assert.Equal(rowIds, settings.KeyPairs.Select(pair => pair.UiId));
    }

    [Fact]
    public void JoinSynchronization_PreservesPersistedOrderAndSynchronizesChanges()
    {
        var settings = new JoinEditorSettings
        {
            LeftNodeId = "zeta",
            RightNodeId = "alpha",
        };

        var unchanged = TransformInputSynchronizer.SynchronizeJoin(
            settings,
            ["alpha", "zeta"]);
        var changed = TransformInputSynchronizer.SynchronizeJoin(
            settings,
            ["beta", "zeta"]);
        var removed = TransformInputSynchronizer.SynchronizeJoin(
            settings,
            ["zeta"]);
        var replaced = TransformInputSynchronizer.SynchronizeJoin(
            new JoinEditorSettings
            {
                LeftNodeId = "stale-left",
                RightNodeId = "stale-right",
            },
            ["zeta", "alpha"]);

        Assert.Equal(InputSynchronizationOutcome.Applied, unchanged.Outcome);
        Assert.Equal(("zeta", "alpha"), (unchanged.LeftNodeId, unchanged.RightNodeId));
        Assert.Equal(("zeta", "beta"), (changed.LeftNodeId, changed.RightNodeId));
        Assert.Equal(("zeta", string.Empty), (removed.LeftNodeId, removed.RightNodeId));
        Assert.Equal(("zeta", "alpha"), (replaced.LeftNodeId, replaced.RightNodeId));
    }

    [Fact]
    public void UnionSynchronization_PreservesRetainedOrderAndAppendsNewInputsInEdgeOrder()
    {
        var settings = new UnionRowsEditorSettings();
        settings.Inputs.Add(new("zeta"));
        settings.Inputs.Add(new("alpha"));
        settings.Inputs.Add(new("stale"));
        var retainedUiIds = settings.Inputs
            .Take(2)
            .ToDictionary(input => input.NodeId, input => input.UiId);

        var result = TransformInputSynchronizer.SynchronizeUnion(
            settings,
            ["beta", "alpha", "zeta", "aardvark"]);

        Assert.Equal(InputSynchronizationOutcome.Applied, result.Outcome);
        Assert.Equal(
            ["zeta", "alpha", "beta", "aardvark"],
            result.InputNodeIds);
        Assert.Equal(retainedUiIds["zeta"], result.Inputs[0].UiId);
        Assert.Equal(retainedUiIds["alpha"], result.Inputs[1].UiId);
        Assert.DoesNotContain(
            result.Inputs.Skip(2),
            input => retainedUiIds.Values.Contains(input.UiId));
    }

    [Fact]
    public void Synchronization_ReportsCapacityWithoutMutatingPersistedOrder()
    {
        var join = new JoinEditorSettings
        {
            LeftNodeId = "left",
            RightNodeId = "right",
        };
        var union = new UnionRowsEditorSettings();
        union.Inputs.Add(new("first"));
        union.Inputs.Add(new("second"));

        var joinResult = TransformInputSynchronizer.SynchronizeJoin(
            join,
            ["left", "right", "third"]);
        var unionResult = TransformInputSynchronizer.SynchronizeUnion(
            union,
            Enumerable.Range(1, 17).Select(index => $"input-{index}").ToArray());

        Assert.Equal(InputSynchronizationOutcome.CapacityExceeded, joinResult.Outcome);
        Assert.Equal(("left", "right"), (joinResult.LeftNodeId, joinResult.RightNodeId));
        Assert.Equal(3, joinResult.ProposedInputCount);
        Assert.Equal(InputSynchronizationOutcome.CapacityExceeded, unionResult.Outcome);
        Assert.Equal(["first", "second"], unionResult.InputNodeIds);
        Assert.Equal(17, unionResult.ProposedInputCount);
        Assert.Equal(["first", "second"], union.Inputs.Select(input => input.NodeId));
    }

    public static TheoryData<WorkflowNodeKind, string> StrictInvalidSettings =>
        new()
        {
            { WorkflowNodeKind.SelectColumns, """{"columns":[]}""" },
            {
                WorkflowNodeKind.FilterRows,
                """{"conditions":[{"column":"Status","operator":"equals"}]}"""
            },
            {
                WorkflowNodeKind.Join,
                """{"leftNodeId":"invalid/id","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"inner","maximumBufferedRows":100}"""
            },
            {
                WorkflowNodeKind.UnionRows,
                """{"inputNodeIds":["left","LEFT"],"matchBy":"name","mode":"all"}"""
            },
            {
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Amount"},{"name":"TOTAL","type":"Decimal","nullable":true,"expression":"row.Tax"}]}"""
            },
            {
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"Total","type":"Unsupported","nullable":false,"expression":"row.Amount"}]}"""
            },
            {
                WorkflowNodeKind.AggregateRows,
                """{"groupBy":[],"aggregates":[{"name":"Rows","operation":"count","column":"Amount"}],"maximumGroups":100}"""
            },
            {
                WorkflowNodeKind.DistinctRows,
                """{"maximumKeys":0}"""
            },
            {
                WorkflowNodeKind.SortRows,
                """{"keys":[{"column":"Id","direction":"ascending","nulls":"last"},{"column":"ID","direction":"descending","nulls":"first"}],"maximumBufferedRows":100}"""
            },
        };

    [Theory]
    [MemberData(nameof(StrictInvalidSettings))]
    public void Codec_ReturnsEditableDraftWithAuthoritativeNonPublishableStatus(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        var parsed = TransformEditorSettingsCodec.Parse(kind, settingsJson);

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.NotNull(parsed.Settings);
        Assert.False(parsed.IsPublishable);
        Assert.False(string.IsNullOrWhiteSpace(parsed.ValidationError));
        var candidate = TransformEditorSettingsCodec.Serialize(parsed.Settings);
        Assert.True(candidate.IsSuccess, candidate.Error);
        Assert.False(candidate.IsPublishable);
        Assert.NotNull(candidate.SettingsJson);
        Assert.False(string.IsNullOrWhiteSpace(candidate.ValidationError));
        var reloaded = TransformEditorSettingsCodec.Parse(
            kind,
            candidate.SettingsJson);
        Assert.True(reloaded.IsSuccess, reloaded.Error);
        Assert.False(reloaded.IsPublishable);
    }

    [Fact]
    public void Codec_ExposesBoundedDraftCandidateButNeverLabelsIncompleteStatePublishable()
    {
        var invalidSettings = new TransformEditorSettings[]
        {
            TransformEditorSettingsCodec.Create(WorkflowNodeKind.SelectColumns),
            TransformEditorSettingsCodec.Create(WorkflowNodeKind.FilterRows),
            TransformEditorSettingsCodec.Create(WorkflowNodeKind.Join),
            TransformEditorSettingsCodec.Create(WorkflowNodeKind.UnionRows),
            TransformEditorSettingsCodec.Create(WorkflowNodeKind.DeriveColumns),
            TransformEditorSettingsCodec.Create(WorkflowNodeKind.AggregateRows),
            new DistinctRowsEditorSettings { MaximumKeys = 0 },
            TransformEditorSettingsCodec.Create(WorkflowNodeKind.SortRows),
        };

        foreach (var settings in invalidSettings)
        {
            var serialized = TransformEditorSettingsCodec.Serialize(settings);

            Assert.True(serialized.IsSuccess, serialized.Error);
            Assert.NotNull(serialized.SettingsJson);
            Assert.False(serialized.IsPublishable);
            Assert.False(string.IsNullOrWhiteSpace(serialized.ValidationError));
        }
    }

    public static TheoryData<WorkflowNodeKind, string> MissingRequiredProperties =>
        new()
        {
            { WorkflowNodeKind.SelectColumns, "{}" },
            {
                WorkflowNodeKind.FilterRows,
                """{"conditions":[{"column":"Status","value":"Ready"}]}"""
            },
            {
                WorkflowNodeKind.Join,
                """{"leftNodeId":"left","rightNodeId":"right","type":"inner","maximumBufferedRows":100}"""
            },
            {
                WorkflowNodeKind.UnionRows,
                """{"inputNodeIds":["left","right"],"matchBy":"name"}"""
            },
            {
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"Total","nullable":false,"expression":"row.Amount"}]}"""
            },
            {
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"Total","type":"Decimal","expression":"row.Amount"}]}"""
            },
            {
                WorkflowNodeKind.AggregateRows,
                """{"aggregates":[{"name":"Rows","operation":"count"}],"maximumGroups":100}"""
            },
            {
                WorkflowNodeKind.AggregateRows,
                """{"groupBy":[],"aggregates":[{"name":"Rows"}],"maximumGroups":100}"""
            },
            {
                WorkflowNodeKind.DistinctRows,
                "{}"
            },
            {
                WorkflowNodeKind.SortRows,
                """{"keys":[{"column":"Id","nulls":"last"}],"maximumBufferedRows":100}"""
            },
            {
                WorkflowNodeKind.SortRows,
                """{"keys":[{"column":"Id","direction":"ascending","nulls":"last"}]}"""
            },
        };

    [Theory]
    [MemberData(nameof(MissingRequiredProperties))]
    public void Codec_PreservesMissingRequiredPropertiesAsNonPublishableDrafts(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        var parsed = TransformEditorSettingsCodec.Parse(kind, settingsJson);

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.False(parsed.IsPublishable);
        Assert.False(string.IsNullOrWhiteSpace(parsed.ValidationError));
        var candidate = TransformEditorSettingsCodec.Serialize(parsed.Settings!);
        Assert.True(candidate.IsSuccess, candidate.Error);
        Assert.False(candidate.IsPublishable);
        Assert.False(string.IsNullOrWhiteSpace(candidate.ValidationError));
        var reloaded = TransformEditorSettingsCodec.Parse(
            kind,
            candidate.SettingsJson!);
        Assert.True(reloaded.IsSuccess, reloaded.Error);
        Assert.False(reloaded.IsPublishable);
    }

    [Fact]
    public void Codec_ExplicitUiEditsMarkMissingPropertiesPresent()
    {
        AssertBecomesPublishable(
            WorkflowNodeKind.SelectColumns,
            "{}",
            settings => Assert.IsType<SelectColumnsEditorSettings>(settings)
                .Columns.Add("Id"));
        AssertBecomesPublishable(
            WorkflowNodeKind.FilterRows,
            """{"conditions":[{"column":"Status","value":"Ready"}]}""",
            settings => Assert.IsType<FilterRowsEditorSettings>(settings)
                .Conditions.Single().Operator = "equals");
        AssertBecomesPublishable(
            WorkflowNodeKind.Join,
            """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"inner"}""",
            settings => Assert.IsType<JoinEditorSettings>(settings)
                .MaximumBufferedRows = 100);
        AssertBecomesPublishable(
            WorkflowNodeKind.UnionRows,
            """{"inputNodeIds":["left","right"],"matchBy":"name"}""",
            settings => Assert.IsType<UnionRowsEditorSettings>(settings).Mode = "all");
        AssertBecomesPublishable(
            WorkflowNodeKind.DeriveColumns,
            """{"columns":[{"name":"Total","type":"Decimal","expression":"row.Amount"}]}""",
            settings => Assert.IsType<DeriveColumnsEditorSettings>(settings)
                .Columns.Single().IsNullable = false);
        AssertBecomesPublishable(
            WorkflowNodeKind.AggregateRows,
            """{"aggregates":[{"name":"Rows","operation":"count"}],"maximumGroups":100}""",
            settings => Assert.IsType<AggregateRowsEditorSettings>(settings)
                .GroupBy.Clear());
        AssertBecomesPublishable(
            WorkflowNodeKind.DistinctRows,
            "{}",
            settings => Assert.IsType<DistinctRowsEditorSettings>(settings)
                .MaximumKeys = 100);
        AssertBecomesPublishable(
            WorkflowNodeKind.SortRows,
            """{"keys":[{"column":"Id","nulls":"last"}],"maximumBufferedRows":100}""",
            settings => Assert.IsType<SortRowsEditorSettings>(settings)
                .Keys.Single().Direction = "ascending");
    }

    [Theory]
    [InlineData(WorkflowNodeKind.SelectColumns, """{"columns":[]}""")]
    [InlineData(WorkflowNodeKind.FilterRows, """{"conditions":[]}""")]
    [InlineData(
        WorkflowNodeKind.Join,
        """{"leftNodeId":"","rightNodeId":"","leftKeys":[],"rightKeys":[],"type":"inner","maximumBufferedRows":100000}""")]
    public void Codec_SafelyRoundTripsCurrentIncompleteDraftDefaults(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        var parsed = TransformEditorSettingsCodec.Parse(kind, settingsJson);

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.False(parsed.IsPublishable);
        Assert.NotNull(parsed.Settings);
        var candidate = TransformEditorSettingsCodec.Serialize(parsed.Settings);
        Assert.True(candidate.IsSuccess, candidate.Error);
        Assert.False(candidate.IsPublishable);
        Assert.Equal(settingsJson, candidate.SettingsJson);
    }

    [Fact]
    public void Codec_RejectsOversizedInputBeforeParsing()
    {
        var oversized = $$"""{"columns":["{{new string(
            'x',
            WorkflowGraphValidator.MaximumNodeSettingsCharacters)}}"]}""";

        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.SelectColumns,
            oversized);

        Assert.False(parsed.IsSuccess);
        Assert.Null(parsed.Settings);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
    }

    [Fact]
    public void Codec_TreatsOmittedJoinTypeAsPublishableImplicitInner()
    {
        const string settingsJson =
            """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"maximumBufferedRows":100}""";

        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.Join,
            settingsJson);

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.True(parsed.IsPublishable, parsed.ValidationError);
        var settings = Assert.IsType<JoinEditorSettings>(parsed.Settings);
        Assert.Equal("inner", settings.JoinType);
        var candidate = TransformEditorSettingsCodec.Serialize(settings);
        Assert.True(candidate.IsSuccess, candidate.Error);
        Assert.True(candidate.IsPublishable, candidate.ValidationError);
        Assert.Equal(settingsJson, candidate.SettingsJson);
    }

    [Fact]
    public void Codec_RejectsStructurallyUnsafeOverCountInput()
    {
        var columns = string.Join(
            ",",
            Enumerable.Range(0, 513).Select(index => $@"""Column{index}"""));
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.SelectColumns,
            $$"""{"columns":[{{columns}}]}""");

        Assert.False(parsed.IsSuccess);
        Assert.Null(parsed.Settings);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
    }

    [Fact]
    public void Codec_DoesNotMaterializeOversizedScalarCandidate()
    {
        var settings = new DeriveColumnsEditorSettings();
        settings.Columns.Add(new()
        {
            Name = "Huge",
            DataType = nameof(TabularDataType.String),
            IsNullable = true,
            Expression = new string(
                'x',
                WorkflowGraphValidator.MaximumNodeSettingsCharacters),
        });

        var serialized = TransformEditorSettingsCodec.Serialize(settings);

        Assert.False(serialized.IsSuccess);
        Assert.Null(serialized.SettingsJson);
        Assert.False(string.IsNullOrWhiteSpace(serialized.Error));
    }

    [Fact]
    public void Codec_HonorsCharacterLimitForCanonicalCjkDrafts()
    {
        const string emptyCandidate =
            """{"conditions":[{"column":"Name","operator":"equals","value":""}]}""";
        var valueLength = WorkflowGraphValidator.MaximumNodeSettingsCharacters
            - emptyCandidate.Length;
        var settings = new FilterRowsEditorSettings();
        settings.Conditions.Add(new()
        {
            Column = "Name",
            Operator = "equals",
            Value = JsonSerializer.SerializeToElement(new string('資', valueLength)),
        });

        var atLimit = TransformEditorSettingsCodec.Serialize(settings);

        Assert.True(atLimit.IsSuccess, atLimit.Error);
        Assert.True(atLimit.IsPublishable, atLimit.ValidationError);
        Assert.Equal(
            WorkflowGraphValidator.MaximumNodeSettingsCharacters,
            atLimit.SettingsJson!.Length);
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.FilterRows,
            atLimit.SettingsJson);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(
            atLimit.SettingsJson,
            SerializeValid(parsed.Settings!));

        settings.Conditions[0].Value = JsonSerializer.SerializeToElement(
            new string('資', valueLength + 1));
        var overLimit = TransformEditorSettingsCodec.Serialize(settings);
        Assert.False(overLimit.IsSuccess);
        Assert.Null(overLimit.SettingsJson);
        Assert.False(string.IsNullOrWhiteSpace(overLimit.Error));
    }

    [Fact]
    public void Codec_HonorsCharacterLimitForHtmlSensitiveJsonScalars()
    {
        const string emptyCandidate =
            """{"conditions":[{"column":"Name","operator":"equals","value":""}]}""";
        var valueLength = WorkflowGraphValidator.MaximumNodeSettingsCharacters
            - emptyCandidate.Length;
        var characters = new[] { '<', '>', '&' };
        var value = new string(
            Enumerable.Range(0, valueLength)
                .Select(index => characters[index % characters.Length])
                .ToArray());
        var settings = new FilterRowsEditorSettings();
        settings.Conditions.Add(new()
        {
            Column = "Name",
            Operator = "equals",
            Value = JsonSerializer.SerializeToElement(value),
        });

        var atLimit = TransformEditorSettingsCodec.Serialize(settings);

        Assert.True(atLimit.IsSuccess, atLimit.Error);
        Assert.True(atLimit.IsPublishable, atLimit.ValidationError);
        Assert.Equal(
            WorkflowGraphValidator.MaximumNodeSettingsCharacters,
            atLimit.SettingsJson!.Length);
        Assert.Contains("<>&", atLimit.SettingsJson, StringComparison.Ordinal);
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.FilterRows,
            atLimit.SettingsJson);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(
            value,
            Assert.IsType<FilterRowsEditorSettings>(parsed.Settings)
                .Conditions.Single().Value!.Value.GetString());

        settings.Conditions[0].Value = JsonSerializer.SerializeToElement(value + "<");
        var overLimit = TransformEditorSettingsCodec.Serialize(settings);
        Assert.False(overLimit.IsSuccess);
        Assert.Null(overLimit.SettingsJson);
    }

    [Fact]
    public void Codec_RetainsRequiredJsonEscapingWithRelaxedUnicodeEncoder()
    {
        const string value = "<>&\"\\\n\u0001";
        var settings = new FilterRowsEditorSettings();
        settings.Conditions.Add(new()
        {
            Column = "Value",
            Operator = "equals",
            Value = JsonSerializer.SerializeToElement(value),
        });

        var candidate = TransformEditorSettingsCodec.Serialize(settings);

        Assert.True(candidate.IsSuccess, candidate.Error);
        Assert.Contains("<>&", candidate.SettingsJson, StringComparison.Ordinal);
        Assert.Contains("\\\"", candidate.SettingsJson, StringComparison.Ordinal);
        Assert.Contains("\\\\", candidate.SettingsJson, StringComparison.Ordinal);
        Assert.Contains("\\n", candidate.SettingsJson, StringComparison.Ordinal);
        Assert.Contains("\\u0001", candidate.SettingsJson, StringComparison.OrdinalIgnoreCase);
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.FilterRows,
            candidate.SettingsJson!);
        Assert.Equal(
            value,
            Assert.IsType<FilterRowsEditorSettings>(parsed.Settings)
                .Conditions.Single().Value!.Value.GetString());
    }

    [Fact]
    public void Codec_PreflightsMutableCollectionBoundsBeforeWriting()
    {
        var settings = new SelectColumnsEditorSettings();
        settings.Columns.AddRange(
            Enumerable.Range(0, 513).Select(index => $"Column{index}"));

        var serialized = TransformEditorSettingsCodec.Serialize(settings);

        Assert.False(serialized.IsSuccess);
        Assert.Null(serialized.SettingsJson);
        Assert.False(string.IsNullOrWhiteSpace(serialized.Error));
    }

    [Fact]
    public void Codec_RejectsNonScalarMutableFilterValueWithoutCandidate()
    {
        var settings = new FilterRowsEditorSettings();
        settings.Conditions.Add(new()
        {
            Column = "Status",
            Operator = "equals",
            Value = JsonSerializer.SerializeToElement(new[] { "Ready" }),
        });

        var serialized = TransformEditorSettingsCodec.Serialize(settings);

        Assert.False(serialized.IsSuccess);
        Assert.Null(serialized.SettingsJson);
        Assert.False(string.IsNullOrWhiteSpace(serialized.Error));
    }

    [Fact]
    public void UnionSynchronization_ReturnsImmutableMutationIsolatedPlan()
    {
        var settings = new UnionRowsEditorSettings();
        settings.Inputs.Add(new("first"));
        settings.Inputs.Add(new("second"));
        var originalUiId = settings.Inputs[0].UiId;

        var result = TransformInputSynchronizer.SynchronizeUnion(
            settings,
            ["first", "second", "third"]);
        settings.Inputs[0].NodeId = "mutated";
        settings.Inputs.Clear();

        Assert.Equal(["first", "second", "third"], result.InputNodeIds);
        Assert.Equal(originalUiId, result.Inputs[0].UiId);
        Assert.IsAssignableFrom<IReadOnlyList<UnionInputSynchronizationItem>>(
            result.Inputs);
        Assert.IsNotType<List<UnionInputSynchronizationItem>>(result.Inputs);
    }

    [Fact]
    public void UnionCapacityResult_IsAnImmutableMutationIsolatedSnapshot()
    {
        var settings = new UnionRowsEditorSettings();
        settings.Inputs.Add(new("first"));
        settings.Inputs.Add(new("second"));
        var originalUiId = settings.Inputs[0].UiId;

        var result = TransformInputSynchronizer.SynchronizeUnion(
            settings,
            Enumerable.Range(1, 17).Select(index => $"input-{index}").ToArray());
        settings.Inputs[0].NodeId = "mutated";
        settings.Inputs.Clear();

        Assert.Equal(InputSynchronizationOutcome.CapacityExceeded, result.Outcome);
        Assert.Equal(["first", "second"], result.InputNodeIds);
        Assert.Equal(originalUiId, result.Inputs[0].UiId);
        Assert.IsNotType<List<UnionInputSynchronizationItem>>(result.Inputs);
    }

    private static string SerializeValid(TransformEditorSettings settings)
    {
        var result = TransformEditorSettingsCodec.Serialize(settings);
        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.IsPublishable, result.ValidationError);
        return Assert.IsType<string>(result.SettingsJson);
    }

    private static void AssertBecomesPublishable(
        WorkflowNodeKind kind,
        string settingsJson,
        Action<TransformEditorSettings> edit)
    {
        var parsed = TransformEditorSettingsCodec.Parse(kind, settingsJson);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.False(parsed.IsPublishable);

        edit(parsed.Settings!);

        var candidate = TransformEditorSettingsCodec.Serialize(parsed.Settings!);
        Assert.True(candidate.IsSuccess, candidate.Error);
        Assert.True(candidate.IsPublishable, candidate.ValidationError);
        var reloaded = TransformEditorSettingsCodec.Parse(
            kind,
            candidate.SettingsJson!);
        Assert.True(reloaded.IsSuccess, reloaded.Error);
        Assert.True(reloaded.IsPublishable, reloaded.ValidationError);
    }
}
