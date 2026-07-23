using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using THub.Application.Connections;
using THub.Domain.Connections;
using THub.Domain.Workflows;

namespace THub.Application.Workflows.Management;

public sealed record WorkflowPackageExport(
    string FileName,
    string Content);

public sealed record WorkflowPackageImport(
    WorkflowDetailsDto Workflow,
    IReadOnlyList<string> Warnings);

public sealed class WorkflowPackageService(
    IWorkflowManagementRepository workflowRepository,
    IDataConnectionStore connectionStore,
    WorkflowCatalogService workflowCatalog,
    WorkflowGraphSerializer graphSerializer,
    TimeProvider timeProvider)
{
    public const int CurrentPackageSchemaVersion = 1;
    public const int MaximumPackageCharacters = 2_500_000;

    private static readonly JsonSerializerOptions PackageOptions = CreatePackageOptions();

    public async Task<WorkflowOperationResult<WorkflowPackageExport>> ExportAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        if (workflowId == Guid.Empty)
        {
            return Failure<WorkflowPackageExport>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.id.required",
                "A workflow id is required.");
        }

        var workflow = await workflowRepository.GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return Failure<WorkflowPackageExport>(
                WorkflowOperationStatus.NotFound,
                "workflow.not-found",
                "The workflow was not found.");
        }

        WorkflowGraph graph;
        try
        {
            graph = graphSerializer.Deserialize(workflow.GraphJson);
        }
        catch (WorkflowGraphSerializationException exception)
        {
            return Failure<WorkflowPackageExport>(
                WorkflowOperationStatus.InvalidState,
                "workflow.export.graph-invalid",
                exception.Message);
        }

        var connections = await connectionStore.ListAsync(cancellationToken);
        var references = GetConnectionIds(graph)
            .Select(id =>
            {
                var connection = connections.SingleOrDefault(candidate => candidate.Id == id);
                return new ConnectionReferenceContract(
                    id,
                    connection?.Name,
                    connection?.Kind);
            })
            .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.SourceId)
            .ToArray();

        using var graphDocument = JsonDocument.Parse(workflow.GraphJson);
        var contract = new WorkflowPackageContract(
            CurrentPackageSchemaVersion,
            timeProvider.GetUtcNow(),
            new WorkflowContract(
                workflow.Name,
                workflow.Description,
                workflow.Status,
                workflow.CronExpression,
                workflow.TimeZoneId,
                graphDocument.RootElement.Clone()),
            references);
        var content = JsonSerializer.Serialize(contract, PackageOptions);
        var fileName = $"{SanitizeFileName(workflow.Name)}.thub-workflow.json";
        return WorkflowOperationResult<WorkflowPackageExport>.Success(new(fileName, content));
    }

    public async Task<WorkflowOperationResult<WorkflowPackageImport>> ImportAsync(
        string packageJson,
        string owner,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return Failure<WorkflowPackageImport>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.import.empty",
                "Select a workflow package to import.");
        }
        if (packageJson.Length > MaximumPackageCharacters)
        {
            return Failure<WorkflowPackageImport>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.import.too-large",
                $"A workflow package cannot exceed {MaximumPackageCharacters} characters.");
        }
        if (string.IsNullOrWhiteSpace(owner))
        {
            return Failure<WorkflowPackageImport>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.import.owner-required",
                "An importing identity is required.");
        }

        WorkflowPackageContract? package;
        try
        {
            package = JsonSerializer.Deserialize<WorkflowPackageContract>(
                packageJson,
                PackageOptions);
        }
        catch (JsonException exception)
        {
            return Failure<WorkflowPackageImport>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.import.invalid-json",
                $"The workflow package is malformed: {exception.Message}");
        }

        if (package is null
            || package.PackageSchemaVersion != CurrentPackageSchemaVersion
            || package.Workflow is null
            || package.Workflow.Graph.ValueKind != JsonValueKind.Object)
        {
            return Failure<WorkflowPackageImport>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.import.unsupported-schema",
                $"The package must use workflow package schema version {CurrentPackageSchemaVersion}.");
        }

        WorkflowGraph graph;
        try
        {
            graph = graphSerializer.Deserialize(package.Workflow.Graph.GetRawText());
        }
        catch (WorkflowGraphSerializationException exception)
        {
            return Failure<WorkflowPackageImport>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.import.graph-invalid",
                exception.Message);
        }

        var availableConnections = await connectionStore.ListAsync(cancellationToken);
        var referenceById = (package.ConnectionReferences ?? [])
            .GroupBy(reference => reference.SourceId)
            .ToDictionary(group => group.Key, group => group.First());
        var remap = new Dictionary<Guid, Guid>();
        var warnings = new List<string>();
        foreach (var sourceId in GetConnectionIds(graph))
        {
            referenceById.TryGetValue(sourceId, out var reference);
            var exact = availableConnections.SingleOrDefault(connection =>
                connection.Id == sourceId
                && ReferenceMatches(reference, connection));
            if (exact is not null)
            {
                remap[sourceId] = exact.Id;
                continue;
            }

            var matches = reference is { Name: not null, Kind: not null }
                ? availableConnections.Where(connection =>
                    connection.Kind == reference.Kind
                    && string.Equals(
                        connection.Name,
                        reference.Name,
                        StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            if (matches.Length == 1)
            {
                remap[sourceId] = matches[0].Id;
                continue;
            }

            warnings.Add(reference?.Name is { } name
                ? $"Connection '{name}' ({reference.Kind}) could not be resolved; update the imported draft before publishing."
                : $"Connection reference '{sourceId}' could not be resolved; update the imported draft before publishing.");
            remap[sourceId] = Guid.Empty;
        }

        var remappedGraph = RemapConnections(graph, remap);
        var create = await workflowCatalog.CreateAsync(
            new CreateWorkflowCommand(
                package.Workflow.Name,
                package.Workflow.Description,
                owner,
                graphSerializer.Serialize(remappedGraph),
                package.Workflow.CronExpression,
                package.Workflow.TimeZoneId),
            cancellationToken);
        if (!create.IsSuccess)
        {
            return WorkflowOperationResult<WorkflowPackageImport>.Failure(
                create.Status,
                create.Issues.ToArray());
        }

        return WorkflowOperationResult<WorkflowPackageImport>.Success(
            new(create.Value!, warnings.AsReadOnly()));
    }

    private static WorkflowGraph RemapConnections(
        WorkflowGraph graph,
        IReadOnlyDictionary<Guid, Guid> remap)
    {
        var variables = graph.Variables
            .Select(variable => variable.ConnectionId is { } id
                && remap.TryGetValue(id, out var mapped)
                    ? variable with { ConnectionId = mapped }
                    : variable)
            .ToArray();
        var nodes = graph.Nodes.Select(node =>
        {
            var settings = JsonNode.Parse(node.SettingsJson)?.AsObject()
                ?? new JsonObject();
            if (settings["connectionId"] is JsonValue value
                && value.TryGetValue<Guid>(out var id)
                && remap.TryGetValue(id, out var mapped))
            {
                settings["connectionId"] = mapped;
            }
            return node with { SettingsJson = settings.ToJsonString() };
        }).ToArray();
        return graph with { Nodes = nodes, Variables = variables };
    }

    private static IReadOnlySet<Guid> GetConnectionIds(WorkflowGraph graph)
    {
        var ids = new HashSet<Guid>(
            graph.Variables
                .Where(variable => variable.ConnectionId is not null)
                .Select(variable => variable.ConnectionId!.Value));
        foreach (var node in graph.Nodes)
        {
            using var settings = JsonDocument.Parse(node.SettingsJson);
            if (settings.RootElement.TryGetProperty("connectionId", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetGuid(out var id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static bool ReferenceMatches(
        ConnectionReferenceContract? reference,
        DataConnection connection) =>
        reference is null
        || (reference.Kind is null || reference.Kind == connection.Kind)
        && (reference.Name is null
            || string.Equals(reference.Name, connection.Name, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "workflow" : sanitized;
    }

    private static WorkflowOperationResult<T> Failure<T>(
        WorkflowOperationStatus status,
        string code,
        string message)
        where T : class =>
        WorkflowOperationResult<T>.Failure(status, new WorkflowIssue(code, message));

    private static JsonSerializerOptions CreatePackageOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record WorkflowPackageContract(
        int PackageSchemaVersion,
        DateTimeOffset ExportedAtUtc,
        WorkflowContract Workflow,
        IReadOnlyList<ConnectionReferenceContract>? ConnectionReferences);

    private sealed record WorkflowContract(
        string Name,
        string? Description,
        WorkflowStatus SourceStatus,
        string? CronExpression,
        string TimeZoneId,
        JsonElement Graph);

    private sealed record ConnectionReferenceContract(
        Guid SourceId,
        string? Name,
        ConnectionKind? Kind);
}
