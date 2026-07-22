using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using THub.Application.Publications;

namespace THub.Publications;

public static class PublicationApiEndpoints
{
    private static readonly HashSet<string> SupportedQueryParameters =
        new(["pageSize", "cursor", "filter", "sort"], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        WriteIndented = false
    };

    public static IEndpointRouteBuilder MapPublicationApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/publications/{slug}/rows", GetRowsAsync)
            .WithName("GetPublishedRows")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        endpoints.MapGet("/api/v1/publications/{slug}/schema", GetSchemaAsync)
            .WithName("GetPublicationSchema")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        return endpoints;
    }

    private static async Task GetRowsAsync(
        HttpContext httpContext,
        string slug,
        PublicationTokenService tokenService,
        PublicationAdmissionGate admissionGate,
        PublicationDataService dataService)
    {
        var parsedQuery = ParseQuery(httpContext.Request.Query);
        if (!parsedQuery.IsSuccess)
        {
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status400BadRequest,
                "Invalid publication query",
                parsedQuery.Error!,
                "publication.query_invalid");
            return;
        }

        using var access = await AuthenticateAdmitAndMeterAsync(
            httpContext,
            slug,
            tokenService,
            admissionGate);
        if (access is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(access.Validated.RequestTimeoutSeconds));
        try
        {
            var result = await dataService.ReadRestRowsAsync(
                access.Authentication,
                parsedQuery.Query!,
                timeout.Token);
            if (!result.IsSuccess)
            {
                await WritePublicationProblemAsync(httpContext, result.Problem!);
                return;
            }

            var payload = new
            {
                data = result.Value!.Rows.Select(row => row.Values),
                nextCursor = result.Value.NextCursor,
                version = access.Validated.PublicationVersionId
            };
            await WriteBoundedJsonAsync(
                httpContext,
                payload,
                access.Validated.MaximumResponseBytes,
                timeout.Token);
        }
        catch (ResponseLimitExceededException)
        {
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status413PayloadTooLarge,
                "Publication response is too large",
                "The response exceeded the active publication's byte limit. Request a smaller page.",
                "publication.response_limit");
        }
        catch (OperationCanceledException) when (
            !httpContext.RequestAborted.IsCancellationRequested)
        {
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status504GatewayTimeout,
                "Publication request timed out",
                "The source did not complete within the active publication timeout.",
                "publication.request_timeout");
        }
    }

    private static async Task GetSchemaAsync(
        HttpContext httpContext,
        string slug,
        PublicationTokenService tokenService,
        PublicationAdmissionGate admissionGate,
        PublicationCatalogService catalogService)
    {
        if (httpContext.Request.Query.Count != 0)
        {
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status400BadRequest,
                "Invalid publication query",
                "The schema endpoint does not accept query parameters.",
                "publication.query_invalid");
            return;
        }

        using var access = await AuthenticateAdmitAndMeterAsync(
            httpContext,
            slug,
            tokenService,
            admissionGate);
        if (access is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(access.Validated.RequestTimeoutSeconds));
        try
        {
            var version = await catalogService.GetVersionAsync(
                access.Validated.PublicationId,
                access.Validated.PublicationVersionId,
                timeout.Token);
            if (!version.IsSuccess)
            {
                await WritePublicationProblemAsync(httpContext, version.Problem!);
                return;
            }

            var value = version.Value!;
            var payload = new
            {
                version = value.Id,
                columns = value.Columns.Where(column => column.IsReadable).Select(column => new
                {
                    name = column.PublicAlias,
                    type = column.DataType.ToString(),
                    nullable = column.IsNullable,
                    filterable = column.IsFilterable,
                    sortable = column.IsSortable,
                    key = column.IsKey
                }),
                paging = new
                {
                    defaultPageSize = value.Settings.DefaultPageSize,
                    maximumPageSize = value.Settings.MaximumPageSize,
                    mode = "keyset"
                },
                filters = new
                {
                    syntax = "filter=alias:operator:value",
                    nullSyntax = "filter=alias:isnull|isnotnull",
                    operators = new
                    {
                        allTypes = new[] { "eq", "ne", "gt", "ge", "lt", "le", "isnull", "isnotnull" },
                        stringOnly = new[] { "startswith", "contains" }
                    }
                }
            };
            await WriteBoundedJsonAsync(
                httpContext,
                payload,
                access.Validated.MaximumResponseBytes,
                timeout.Token);
        }
        catch (ResponseLimitExceededException)
        {
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status413PayloadTooLarge,
                "Publication response is too large",
                "The schema exceeded the active publication's byte limit.",
                "publication.response_limit");
        }
        catch (OperationCanceledException) when (
            !httpContext.RequestAborted.IsCancellationRequested)
        {
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status504GatewayTimeout,
                "Publication request timed out",
                "The schema did not complete within the active publication timeout.",
                "publication.request_timeout");
        }
    }

    private static async Task<PublicationRequestAccess?> AuthenticateAdmitAndMeterAsync(
        HttpContext context,
        string slug,
        PublicationTokenService tokenService,
        PublicationAdmissionGate admissionGate)
    {
        var opaqueToken = ReadBearerToken(context.Request.Headers.Authorization);
        var validated = await tokenService.ValidateForAdmissionAsync(
            slug,
            opaqueToken,
            context.RequestAborted);
        if (!validated.IsSuccess)
        {
            await WritePublicationProblemAsync(context, validated.Problem!, challenge: true);
            return null;
        }

        var admission = admissionGate.TryEnter(validated.Value!);
        if (!admission.IsAccepted)
        {
            if (admission.IsUnavailable)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Publication admission is unavailable",
                    "The publication request could not be admitted safely.",
                    admission.ReasonCode!);
            }
            else
            {
                var seconds = Math.Max(
                    1,
                    (int)Math.Ceiling(admission.RetryAfter?.TotalSeconds ?? 1));
                context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Publication request limit reached",
                    "The token or publication has reached its current request limit.",
                    admission.ReasonCode!);
            }

            return null;
        }

        var lease = admission.Lease!;
        var metered = await tokenService.RecordAcceptedUseAsync(
            validated.Value!,
            context.RequestAborted);
        if (!metered.IsSuccess)
        {
            lease.Dispose();
            await WritePublicationProblemAsync(
                context,
                metered.Problem!,
                challenge: metered.Problem!.Kind == PublicationProblemKind.Unauthorized);
            return null;
        }

        return new PublicationRequestAccess(validated.Value!, metered.Value!, lease);
    }

    private static ParsedQuery ParseQuery(IQueryCollection query)
    {
        if (query.Keys.Any(key => !SupportedQueryParameters.Contains(key)))
        {
            return ParsedQuery.Failure(
                "Only pageSize, cursor, repeated filter, and repeated sort parameters are supported.");
        }

        int? pageSize = null;
        if (query.TryGetValue("pageSize", out var pageValues))
        {
            if (pageValues.Count != 1
                || !int.TryParse(
                    pageValues[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedPageSize))
            {
                return ParsedQuery.Failure("pageSize must be one positive integer.");
            }

            pageSize = parsedPageSize;
        }

        string? cursor = null;
        if (query.TryGetValue("cursor", out var cursorValues))
        {
            if (cursorValues.Count != 1)
            {
                return ParsedQuery.Failure("cursor can be supplied at most once.");
            }

            cursor = cursorValues[0];
        }

        var filters = new List<PublicationFilter>();
        if (query.TryGetValue("filter", out var filterValues))
        {
            if (filterValues.Count > 16)
            {
                return ParsedQuery.Failure("At most 16 filters are supported.");
            }

            foreach (var filterValue in filterValues)
            {
                if (!TryParseFilter(filterValue, out var filter))
                {
                    return ParsedQuery.Failure(
                        "Each filter must use alias:operator:value (or alias:isNull / alias:isNotNull).");
                }

                filters.Add(filter!);
            }
        }

        var sorts = new List<PublicationSort>();
        if (query.TryGetValue("sort", out var sortValues))
        {
            if (sortValues.Count > 8)
            {
                return ParsedQuery.Failure("At most 8 sorts are supported.");
            }

            foreach (var sortValue in sortValues)
            {
                if (string.IsNullOrWhiteSpace(sortValue))
                {
                    return ParsedQuery.Failure("Sort aliases cannot be empty.");
                }

                var descending = sortValue.StartsWith('-');
                var alias = descending ? sortValue[1..] : sortValue.ToString();
                if (string.IsNullOrWhiteSpace(alias))
                {
                    return ParsedQuery.Failure("Sort aliases cannot be empty.");
                }

                if (sorts.Any(existing => string.Equals(
                        existing.ColumnAlias,
                        alias,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return ParsedQuery.Failure("Each sort alias can be supplied at most once.");
                }

                sorts.Add(new PublicationSort(alias, descending));
            }
        }

        return ParsedQuery.Success(new PublicationRestRowsQuery(
            pageSize,
            cursor,
            filters,
            sorts));
    }

    private static bool TryParseFilter(string? value, out PublicationFilter? filter)
    {
        filter = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':', 3, StringSplitOptions.None);
        if (parts.Length is < 2 or > 3 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return false;
        }

        var operation = parts[1].ToLowerInvariant() switch
        {
            "eq" => PublicationFilterOperator.Equal,
            "ne" => PublicationFilterOperator.NotEqual,
            "gt" => PublicationFilterOperator.GreaterThan,
            "ge" => PublicationFilterOperator.GreaterThanOrEqual,
            "lt" => PublicationFilterOperator.LessThan,
            "le" => PublicationFilterOperator.LessThanOrEqual,
            "startswith" => PublicationFilterOperator.StartsWith,
            "contains" => PublicationFilterOperator.Contains,
            "isnull" => PublicationFilterOperator.IsNull,
            "isnotnull" => PublicationFilterOperator.IsNotNull,
            _ => (PublicationFilterOperator?)null
        };
        if (operation is null)
        {
            return false;
        }

        var isNullOperation = operation is PublicationFilterOperator.IsNull
            or PublicationFilterOperator.IsNotNull;
        if ((isNullOperation && parts.Length != 2)
            || (!isNullOperation && parts.Length != 3))
        {
            return false;
        }

        filter = new PublicationFilter(
            parts[0],
            operation.Value,
            isNullOperation ? null : parts[2]);
        return true;
    }

    private static string? ReadBearerToken(StringValues authorizationValues)
    {
        if (authorizationValues.Count != 1)
        {
            return null;
        }

        var value = authorizationValues[0];
        const string prefix = "Bearer ";
        if (value is null
            || value.Length <= prefix.Length
            || value.Length > prefix.Length + PublicationTokenGenerator.MaximumTokenCharacters
            || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = value[prefix.Length..];
        return token.Any(char.IsWhiteSpace) ? null : token;
    }

    private static async Task WritePublicationProblemAsync(
        HttpContext context,
        PublicationProblem problem,
        bool challenge = false)
    {
        var status = problem.Kind switch
        {
            PublicationProblemKind.Validation => StatusCodes.Status400BadRequest,
            PublicationProblemKind.NotFound => StatusCodes.Status404NotFound,
            PublicationProblemKind.Conflict => StatusCodes.Status409Conflict,
            PublicationProblemKind.Forbidden => StatusCodes.Status403Forbidden,
            PublicationProblemKind.Unauthorized => StatusCodes.Status401Unauthorized,
            PublicationProblemKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };
        if (challenge || status == StatusCodes.Status401Unauthorized)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"THub Publications\"";
        }

        await WriteProblemAsync(
            context,
            status,
            status == StatusCodes.Status401Unauthorized
                ? "Invalid publication credential"
                : "Publication request failed",
            status == StatusCodes.Status401Unauthorized
                ? "The bearer token is invalid or unavailable."
                : problem.Message,
            problem.Code);
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string title,
        string detail,
        string code)
    {
        SetResponseHeaders(context.Response);
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"urn:thub:problem:{code}"
        };
        problem.Extensions["code"] = code;
        await Results.Problem(problem).ExecuteAsync(context);
    }

    private static async Task WriteBoundedJsonAsync<T>(
        HttpContext context,
        T value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var buffer = new BoundedMemoryStream(maximumBytes);
        await JsonSerializer.SerializeAsync(buffer, value, JsonOptions, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetResponseHeaders(context.Response);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static void SetResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
    }

    private sealed record ParsedQuery(
        bool IsSuccess,
        PublicationRestRowsQuery? Query,
        string? Error)
    {
        public static ParsedQuery Success(PublicationRestRowsQuery query) => new(true, query, null);
        public static ParsedQuery Failure(string error) => new(false, null, error);
    }

    private sealed class PublicationRequestAccess(
        ValidatedPublicationTokenDto validated,
        AuthenticatedPublicationTokenDto authentication,
        IDisposable lease) : IDisposable
    {
        public ValidatedPublicationTokenDto Validated { get; } = validated;
        public AuthenticatedPublicationTokenDto Authentication { get; } = authentication;
        public void Dispose() => lease.Dispose();
    }
}

internal sealed class BoundedMemoryStream : MemoryStream
{
    private readonly long _maximumLength;

    public BoundedMemoryStream(int maximumLength)
        : base(Math.Min(maximumLength, 64 * 1_024))
    {
        if (maximumLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        _maximumLength = maximumLength;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacity(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        base.Write(buffer);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureCapacity(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureCapacity(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    public override void SetLength(long value)
    {
        if (value > _maximumLength)
        {
            throw new ResponseLimitExceededException();
        }

        base.SetLength(value);
    }

    private void EnsureCapacity(int additionalBytes)
    {
        if (additionalBytes < 0 || Position > _maximumLength - additionalBytes)
        {
            throw new ResponseLimitExceededException();
        }
    }
}

internal sealed class ResponseLimitExceededException : Exception;
