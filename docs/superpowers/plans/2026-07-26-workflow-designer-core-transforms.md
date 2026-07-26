# Workflow Designer Core Transforms Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make THub's canvas cover the common bounded tabular-processing path: graphical multi-table joins, unions, calculated columns, aggregates, distinct rows, sorting, selection, and filtering.

**Architecture:** Keep the existing immutable schema-version-2 DAG and add strictly typed node settings plus bounded in-memory executors behind the existing `BoundedWorkflowExecutionEngine`. Add an application-layer schema propagation service so the Blazor designer can offer column selectors after arbitrary transform chains without duplicating executor rules in the Razor component. Multi-table joins remain a chain of explicit two-input join nodes; union alone accepts two to sixteen inputs.

**Tech Stack:** .NET 10, ASP.NET Core 10 Blazor Interactive Server, Radzen Blazor 11, xUnit, Jint, immutable workflow DAG JSON.

---

## Scope and semantics

This increment implements these operational transform contracts:

- `Join`: two inputs, one to sixteen equality key pairs, `inner`, `left`, or `full`; SQL-style null keys never match; right-side buffering remains explicitly bounded.
- `UnionRows`: two to sixteen inputs; align by name or position; `all` keeps duplicates and `distinct` removes duplicates; all matched columns must have identical THub data types.
- `DeriveColumns`: add one to sixty-four uniquely named typed columns using the existing bounded expression-only JavaScript session. Derived columns are add-only in this increment; replacing existing columns is rejected.
- `AggregateRows`: zero to sixteen group columns and one to sixty-four aggregate outputs. Supported operations are `count`, `countNonNull`, `sum`, `average`, `minimum`, and `maximum`. The number of retained groups is explicitly bounded.
- `DistinctRows`: deduplicate by all columns or one to sixty-four selected columns, with an explicit retained-key bound.
- `SortRows`: one to sixteen ordered keys, each with ascending/descending and nulls-first/nulls-last settings, with an explicit buffered-row bound.
- Existing `SelectColumns` and `FilterRows` settings receive first-party graphical editors. Filter conditions retain AND semantics; OR and multi-output conditional split remain outside this increment.

The following remain explicitly unsupported after this increment: named output ports/conditional split, pivot/unpivot, window functions, fuzzy matching, CDC/SCD, row-level error redirection, streaming/spill-to-disk sort or aggregate, and arbitrary SQL transforms.

Research basis:

- Microsoft documents SSIS transforms for aggregate, derived column, sort, conditional split, union, lookup, and two-input merge joins: <https://learn.microsoft.com/en-us/sql/integration-services/data-flow/transformations/integration-services-transformations>
- Azure Data Factory documents join, union, aggregate, sort, derived-column, and conditional-split transforms as the visual data-flow baseline: <https://learn.microsoft.com/en-us/azure/data-factory/concepts-data-flow-overview>
- AWS Glue distinguishes union (combine rows) from join (combine columns) and validates union schema compatibility: <https://docs.aws.amazon.com/glue/latest/dg/transforms-configure-union.html>

No commits are included because repository instructions prohibit committing unless the user requests it.

### Task 1: Add strict settings contracts and graph cardinality

**Files:**

- Modify: `src/THub.Domain/Workflows/WorkflowNodeKind.cs`
- Modify: `src/THub.Application/Execution/WorkflowNodeSettings.cs`
- Modify: `src/THub.Application/Workflows/WorkflowGraphValidator.cs`
- Test: `tests/THub.Application.Tests/WorkflowNodeSettingsValidatorTests.cs`
- Test: `tests/THub.Application.Tests/WorkflowGraphValidatorTests.cs`

- [ ] **Step 1: Add failing settings-parser tests**

Add theory cases using these canonical documents:

```csharp
[InlineData(WorkflowNodeKind.UnionRows,
    """{"inputNodeIds":["north","south"],"matchBy":"name","mode":"all"}""")]
[InlineData(WorkflowNodeKind.DeriveColumns,
    """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Quantity * row.Price"}]}""")]
[InlineData(WorkflowNodeKind.AggregateRows,
    """{"groupBy":["Region"],"aggregates":[{"name":"OrderCount","operation":"count"},{"name":"Revenue","operation":"sum","column":"Amount"}],"maximumGroups":100000}""")]
[InlineData(WorkflowNodeKind.DistinctRows,
    """{"columns":["CustomerId"],"maximumKeys":100000}""")]
[InlineData(WorkflowNodeKind.SortRows,
    """{"keys":[{"column":"CreatedAt","direction":"descending","nulls":"last"}],"maximumBufferedRows":100000}""")]
```

Add negative tests for duplicate output names, unsupported operations, missing aggregate columns, union input IDs that do not match incoming edges, fewer than two union inputs, more than sixteen union inputs, duplicate sort keys, and buffer/group/key bounds outside `1..1_000_000`.

- [ ] **Step 2: Run the focused application tests and confirm RED**

Run:

```powershell
dotnet test tests/THub.Application.Tests/THub.Application.Tests.csproj --filter "FullyQualifiedName~WorkflowNodeSettingsValidatorTests|FullyQualifiedName~WorkflowGraphValidatorTests"
```

Expected: failures because the new enum members and settings records do not exist.

- [ ] **Step 3: Add the node kinds and immutable settings records**

Append these enum members after `Join`:

```csharp
UnionRows,
DeriveColumns,
AggregateRows,
DistinctRows,
SortRows,
```

Add records/enums whose serialized names match the JSON above:

```csharp
public enum UnionMatchMode { Name, Position }
public enum UnionRowMode { All, Distinct }
public sealed record UnionRowsNodeSettings(
    IReadOnlyList<string> InputNodeIds,
    UnionMatchMode MatchBy,
    UnionRowMode Mode) : WorkflowNodeSettings;

public sealed record DerivedColumnSettings(
    string Name,
    TabularDataType DataType,
    bool IsNullable,
    string Expression);
public sealed record DeriveColumnsNodeSettings(
    IReadOnlyList<DerivedColumnSettings> Columns) : WorkflowNodeSettings;

public enum AggregateOperation
{
    Count,
    CountNonNull,
    Sum,
    Average,
    Minimum,
    Maximum
}
public sealed record AggregateColumnSettings(
    string Name,
    AggregateOperation Operation,
    string? Column);
public sealed record AggregateRowsNodeSettings(
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<AggregateColumnSettings> Aggregates,
    int MaximumGroups) : WorkflowNodeSettings;

public sealed record DistinctRowsNodeSettings(
    IReadOnlyList<string>? Columns,
    int MaximumKeys) : WorkflowNodeSettings;

public enum SortDirection { Ascending, Descending }
public enum SortNullPlacement { First, Last }
public sealed record SortKeySettings(
    string Column,
    SortDirection Direction,
    SortNullPlacement Nulls);
public sealed record SortRowsNodeSettings(
    IReadOnlyList<SortKeySettings> Keys,
    int MaximumBufferedRows) : WorkflowNodeSettings;
```

Parsing must use existing strict-property, duplicate, identifier, expression-length, and numeric-bound helpers. Validate `count` without a column, require a column for every other aggregate operation, and validate every derived JavaScript expression through `IWorkflowExpressionSessionFactory` when available.

- [ ] **Step 4: Add per-kind cardinality**

Replace the join-only cardinality branch with:

```csharp
var (minimumInputs, maximumInputs) = node.Kind switch
{
    WorkflowNodeKind.Join => (2, 2),
    WorkflowNodeKind.UnionRows => (2, 16),
    _ => (1, 1)
};
```

Emit `node.input.cardinality` when the incoming edge count is outside that range. Validate `UnionRowsNodeSettings.InputNodeIds` as the exact case-insensitive set of incoming source IDs, just as join settings are validated today.

- [ ] **Step 5: Run the focused application tests and confirm GREEN**

Run the Step 2 command. Expected: all selected tests pass with zero warnings.

### Task 2: Add bounded transform executors

**Files:**

- Modify: `src/THub.Infrastructure/Execution/TransformNodeExecutors.cs`
- Modify: `src/THub.Infrastructure/DependencyInjection.cs`
- Modify: `src/THub.Application/Execution/BoundedWorkflowExecutionEngine.cs`
- Modify: `src/THub.Infrastructure/Execution/InfrastructureWorkflowNodeResourceValidator.cs`
- Test: `tests/THub.Infrastructure.Tests/TransformNodeExecutorTests.cs`
- Test: `tests/THub.Application.Tests/BoundedWorkflowExecutionEngineTests.cs`

- [ ] **Step 1: Add failing executor tests**

Add one behavioral test per operation:

```csharp
[Fact] public async Task UnionByNameReordersColumnsAndKeepsAllRows()
[Fact] public async Task UnionDistinctRemovesDuplicateRowsAcrossInputs()
[Fact] public async Task DeriveColumnsEvaluatesTypedExpressionWithVariables()
[Fact] public async Task AggregateGroupsAndComputesCountSumAverageMinimumAndMaximum()
[Fact] public async Task DistinctRowsUsesConfiguredKeyColumns()
[Fact] public async Task SortRowsAppliesMultipleKeysAndNullPlacement()
[Fact] public async Task FullJoinEmitsUnmatchedRowsFromBothInputs()
```

Each test must use real replayable `ITabularDataSet` inputs and assert output schema, row values, and the relevant limit error code. Add engine coverage proving every new kind is accepted only with a transform descriptor.

- [ ] **Step 2: Run transform tests and confirm RED**

Run:

```powershell
dotnet test tests/THub.Infrastructure.Tests/THub.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TransformNodeExecutorTests"
```

Expected: failures because the executor types and full join behavior do not exist.

- [ ] **Step 3: Implement union, derived-column, distinct, and sort executors**

Use `WorkflowNodeExecutorDescriptor.Transform` for every executor. Preserve engine ownership of inputs and report only aggregate counts/bytes through `IWorkflowNodeProgressReporter`.

Required failure codes:

```text
execution.union.input
execution.union.schema
execution.derive.column.duplicate
execution.distinct.key.limit
execution.sort.buffer.limit
```

Use the existing invariant tabular key encoding pattern from `JoinNodeExecutor` for union-distinct and distinct-row keys. Sort must retain `(row, originalOrdinal)` and use the ordinal as the final comparison to guarantee stable results.

- [ ] **Step 4: Implement aggregate and full outer join**

Aggregate must retain at most `MaximumGroups`, emit group columns before aggregate columns, return `Int64` for counts, preserve numeric input type for sum, return `Decimal` or `Double` for average, and preserve input type for minimum/maximum. Reject sum/average on nonnumeric columns as configuration errors.

Full join must track matched right-side rows and emit null-filled left columns for each unmatched right row after the left stream completes. The output schema must mark both sides nullable for a full join and only the right side nullable for a left join.

Required failure codes:

```text
execution.aggregate.group.limit
execution.aggregate.operation.type
execution.join.buffer.limit
```

- [ ] **Step 5: Register executors and execution roles**

Register the five new executors next to the existing transform registrations. Extend `MatchesExpectedRole` and the infrastructure preflight/resource validator so all new kinds are transforms and do not resolve external resources.

- [ ] **Step 6: Run focused infrastructure and engine tests and confirm GREEN**

Run:

```powershell
dotnet test tests/THub.Infrastructure.Tests/THub.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TransformNodeExecutorTests"
dotnet test tests/THub.Application.Tests/THub.Application.Tests.csproj --filter "FullyQualifiedName~BoundedWorkflowExecutionEngineTests"
```

Expected: all selected tests pass with zero warnings.

### Task 3: Add application-layer schema propagation

**Files:**

- Create: `src/THub.Application/Workflows/WorkflowTabularSchemaService.cs`
- Test: `tests/THub.Application.Tests/WorkflowTabularSchemaServiceTests.cs`
- Modify: `src/THub.Infrastructure/DependencyInjection.cs`

- [ ] **Step 1: Add failing schema-propagation tests**

Cover source selection, select/filter passthrough, chained join name collision (`right.Id`), full-join nullability, union-by-name reordering, derived columns, aggregate output types, distinct/sort passthrough, cycle/missing-schema rejection, and a three-table chain:

```text
Orders + Customers -> JoinOrdersCustomers
JoinOrdersCustomers + Regions -> JoinWithRegions
```

The final schema must contain all selected order columns, noncolliding customer columns, and region columns with the same duplicate-name rules as runtime execution.

- [ ] **Step 2: Run the focused schema tests and confirm RED**

Run:

```powershell
dotnet test tests/THub.Application.Tests/THub.Application.Tests.csproj --filter "FullyQualifiedName~WorkflowTabularSchemaServiceTests"
```

Expected: failure because `WorkflowTabularSchemaService` does not exist.

- [ ] **Step 3: Implement one authoritative schema calculation service**

Expose:

```csharp
public sealed class WorkflowTabularSchemaService
{
    public WorkflowTabularSchemaResult Resolve(
        WorkflowGraph graph,
        string nodeId,
        IReadOnlyDictionary<string, TabularSchema> knownSourceSchemas);
}

public sealed record WorkflowTabularSchemaResult(
    TabularSchema? Schema,
    string? Code,
    string? Message)
{
    public bool IsSuccess => Schema is not null;
}
```

Resolve recursively in graph order, parse settings through `WorkflowNodeSettingsValidator`, and share schema helper methods with the executors rather than maintaining two collision/type algorithms. Do not inspect databases or read rows in this service.

- [ ] **Step 4: Register and rerun schema tests**

Register the service with the same lifetime as the node-settings validator. Run the Step 2 command and expect all selected tests to pass.

### Task 4: Build first-party graphical transform editors

**Files:**

- Create: `src/THub.Web/Components/WorkflowDesigner/TransformEditor.razor`
- Create: `src/THub.Web/Components/WorkflowDesigner/TransformEditor.razor.css`
- Create: `src/THub.Web/Components/WorkflowDesigner/TransformEditorModels.cs`
- Modify: `src/THub.Web/Components/Pages/Designer.razor`
- Modify: `src/THub.Web/Components/Pages/Designer.razor.css`
- Test: `tests/THub.Web.Tests/WorkflowDesignerLocalizationTests.cs`

- [ ] **Step 1: Add a failing localization-contract test**

The test must load neutral and `zh-TW` RESX files and assert that every new transform label key exists in both with a nonempty value. Include at least:

```text
Calculated columns
Aggregate rows
Distinct rows
Sort rows
Union rows
Join type
Full outer
Match columns
Add key pair
Group by
Aggregate columns
Sort keys
Keep all rows
Remove duplicate rows
Match columns by name
Match columns by position
Maximum buffered rows
```

- [ ] **Step 2: Run the focused web test and confirm RED**

Run:

```powershell
dotnet test tests/THub.Web.Tests/THub.Web.Tests.csproj --filter "FullyQualifiedName~WorkflowDesignerLocalizationTests"
```

Expected: failure because the resource keys and transform editor are absent.

- [ ] **Step 3: Extract the transform editor from the 2,500-line page**

`TransformEditor.razor` owns only typed transform settings and raises one `SettingsJsonChanged` callback. It receives:

```csharp
[Parameter, EditorRequired] public WorkflowNodeKind Kind { get; set; }
[Parameter, EditorRequired] public string SettingsJson { get; set; } = "{}";
[Parameter, EditorRequired] public IReadOnlyList<WorkflowInputSchemaModel> Inputs { get; set; } = [];
[Parameter] public IReadOnlyList<string> VariableNames { get; set; } = [];
[Parameter] public EventCallback<string> SettingsJsonChanged { get; set; }
```

Keep advanced raw JSON under the existing disclosure. Do not move persistence, authorization, graph validation, connection inspection, or execution rules into the component.

- [ ] **Step 4: Implement the join and union editing paths**

Join UI must:

- show the two incoming steps by name;
- allow swapping left/right without rewiring the DAG;
- add/remove/reorder one to sixteen key pairs;
- use propagated schemas for column dropdowns and fall back to bounded text inputs when a source schema has not been loaded;
- select inner, left outer, or full outer;
- edit the right-side buffer bound;
- explain that a third table is joined by adding another join step after the current one.

Union UI must show all incoming steps, align by name or position, keep all/remove duplicates, and edit its exact ordered `inputNodeIds` list when links change.

- [ ] **Step 5: Implement single-input transform editing paths**

Use compact row editors:

- Select: checked ordered columns.
- Filter: add/remove condition, column, operator, scalar value; label the AND behavior.
- Derived: output name, THub type, nullable, JavaScript expression.
- Aggregate: ordered group columns plus name/operation/source-column rows and maximum group count.
- Distinct: all columns or selected key columns plus maximum retained keys.
- Sort: ordered column, direction, null placement, and maximum buffered rows.

The selected step remains the only configuration surface. Do not add page headers, status cards, history, or global transform documentation to the canvas.

- [ ] **Step 6: Synchronize links and settings**

Replace `SynchronizeJoinInputs` with one method that:

- updates `leftNodeId` and `rightNodeId` for join nodes;
- preserves explicit left/right order when the same two links remain;
- updates ordered `inputNodeIds` for union nodes;
- refuses a seventeenth union input in the UI;
- clears stale validation issues whenever settings change.

- [ ] **Step 7: Rerun the focused web localization test**

Run the Step 2 command. Expected: all selected tests pass.

### Task 5: Complete English and Taiwan Traditional Chinese localization

**Files:**

- Modify: `src/THub.Web/Resources/Localization/SharedResource.resx`
- Modify: `src/THub.Web/Resources/Localization/SharedResource.zh-TW.resx`
- Modify: `src/THub.Web/wwwroot/locales/zh-TW.json`
- Modify: `src/THub.Web/Components/Pages/Designer.razor`
- Modify: `src/THub.Web/Components/WorkflowDesigner/TransformEditor.razor`

- [ ] **Step 1: Replace remaining first-party hard-coded designer strings**

Localize visible labels, placeholders, validation summaries, notifications, empty states, titles, and accessibility names touched by this work. Keep node IDs, column names, schema/object names, and user expressions unchanged.

- [ ] **Step 2: Add reviewed Taiwan terminology**

Use `資料`, `連線`, `資料表`, `欄位`, `設定`, `執行`, `發佈`, `聯結`, `彙總`, `排序`, and `移除重複資料列`. Use placeholders rather than concatenating translated fragments.

- [ ] **Step 3: Synchronize the post-render JSON mirror**

Mirror every new message that can be emitted after render in `wwwroot/locales/zh-TW.json`. The neutral RESX remains the English source contract.

### Task 6: Update truthful capability documentation

**Files:**

- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/data-model.md`
- Modify: `docs/security.md`

- [ ] **Step 1: Update capability statements**

Replace the current implication that `SelectColumns` renames and casts values. State precisely that the core transform set supports selection, typed filtering, calculated columns, bounded aggregate/distinct/sort, compatible-schema union, and chained two-input inner/left/full joins.

- [ ] **Step 2: Document bounds and replay behavior**

Record that joins, sort, aggregate, distinct, and union-distinct are bounded and operate over the engine's replayable materialized intermediates. They do not spill to disk, execute arbitrary SQL, or log row values.

- [ ] **Step 3: Record unsupported advanced scenarios**

List named-output conditional splits, pivot/unpivot, window functions, fuzzy matching, CDC/SCD, row-level error redirection, and external-memory transforms as unsupported rather than planned-as-working.

No ADR is required because the accepted immutable bounded DAG and execution boundary remain unchanged.

### Task 7: Authorized full validation and browser review

**Files:**

- No source changes unless validation exposes an issue.

- [ ] **Step 1: Run formatting verification**

Run:

```powershell
dotnet format THub.slnx --verify-no-changes
```

Expected: exit code 0 and no changed files.

- [ ] **Step 2: Build the solution**

Run:

```powershell
dotnet build THub.slnx --artifacts-path artifacts/validation
```

Expected: build succeeds with zero warnings and zero errors.

- [ ] **Step 3: Run the full suite**

Run:

```powershell
dotnet test THub.slnx --no-build --artifacts-path artifacts/validation
```

Expected: all tests pass with zero failures.

- [ ] **Step 4: Perform the authorized browser checks**

Start the Development web host only with explicit authorization and inspect both `en` and `zh-TW` at desktop and mobile widths. Build:

```text
Orders source + Customers source -> left join
Join output + Regions source -> full join
Three monthly sources -> union distinct
Union -> calculated Revenue -> aggregate by Region -> sort Revenue descending -> target
```

Verify link synchronization, schema dropdowns, keyboard focus, overflow, advanced JSON parity, save/load round-trip, validation messages, and the one-primary-workspace layout.

## Self-review

- Spec coverage: multi-table joining is covered by chained typed joins; common visual ETL transforms are covered by union, derived, aggregate, distinct, sort, select, and filter.
- Scope boundary: named output ports and advanced analytics are explicitly unsupported and not represented as operational.
- Security/data safety: no arbitrary SQL, secrets, row logging, unbounded buffering, or new host authority is introduced.
- Type consistency: enum member, settings record, executor, registry, schema service, UI, tests, and docs use the same names listed in Task 1.
- Localization: every touched first-party UI string has neutral and `zh-TW` coverage plus the post-render mirror when applicable.
- Validation: every production behavior begins with an authorized failing test and ends with focused and full verification.
