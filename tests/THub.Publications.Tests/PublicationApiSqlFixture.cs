using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;
using THub.Infrastructure.Publications;

namespace THub.Publications.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PublicationApiSqlCollection : ICollectionFixture<PublicationApiSqlFixture>
{
    public const string Name = "Publication API SQL integration";
}

public sealed class PublicationApiSqlFixture : IAsyncLifetime
{
    private const string DatabasePrefix = "THub_PublicationApiTests_";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-23T12:00:00Z");

    private readonly PublicationCatalogDelayController _delayController = new();
    private readonly PublicationApiWebApplicationFactory _factory;
    private HttpClient? _client;
    private bool _databaseCreated;

    public PublicationApiSqlFixture()
    {
        DatabaseName = $"{DatabasePrefix}{Guid.NewGuid():N}";
        ConnectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={DatabaseName};Integrated Security=true;Encrypt=false";
        _factory = new PublicationApiWebApplicationFactory(
            ConnectionString,
            DatabaseName,
            new FixedTimeProvider(Now),
            _delayController);
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public HttpClient Client => _client
        ?? throw new InvalidOperationException("The SQL publication fixture has not initialized.");

    public string PrimarySlug { get; private set; } = string.Empty;

    public string BoundedSlug { get; private set; } = string.Empty;

    public TokenCredential PrimaryTokenA { get; private set; } = null!;

    public TokenCredential PrimaryTokenB { get; private set; } = null!;

    public TokenCredential RevokedToken { get; private set; } = null!;

    public TokenCredential ExpiredToken { get; private set; } = null!;

    public TokenCredential BoundedPublicationToken { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _ = _factory.Services;
        await using var scope = _factory.Services.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<THubDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        ValidateDatabaseTarget(db.Database.GetDbConnection().Database);
        await db.Database.MigrateAsync();
        _databaseCreated = true;

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE [dbo].[ApiIntegrationRows]
            (
                [Id] int NOT NULL,
                [Name] nvarchar(100) NOT NULL,
                [IsActive] bit NOT NULL,
                [Amount] decimal(12, 2) NULL,
                [CreatedAt] datetimeoffset(7) NOT NULL,
                CONSTRAINT [PK_ApiIntegrationRows] PRIMARY KEY ([Id])
            );

            INSERT INTO [dbo].[ApiIntegrationRows]
                ([Id], [Name], [IsActive], [Amount], [CreatedAt])
            VALUES
                (1, N'Alpha', 1, 12.50, '2026-07-20T08:00:00+00:00'),
                (2, N'Beta', 0, NULL, '2026-07-20T09:00:00+00:00'),
                (3, N'Gamma', 1, 31.75, '2026-07-20T10:00:00+00:00'),
                (4, N'Delta', 1, 48.00, '2026-07-20T11:00:00+00:00');
            """);

        var serializer = scope.ServiceProvider.GetRequiredService<ConnectionConfigurationSerializer>();
        var sourceConnection = new DataConnection(
            "Publication API integration source",
            ConnectionKind.SqlServer,
            serializer.Serialize(new SqlServerConnectionConfiguration(
                "(localdb)\\MSSQLLocalDB",
                DatabaseName,
                encrypt: false,
                connectTimeoutSeconds: 15,
                commandTimeoutSeconds: 10,
                maximumBatchRows: 100)),
            "integration-test",
            Now.AddHours(-2));
        db.Connections.Add(sourceConnection);

        var primaryPublication = new Publication(
            Guid.NewGuid(),
            "sql-backed-api",
            "SQL-backed API integration publication",
            PublicationKind.RestApi,
            "integration-test",
            Now.AddHours(-2));
        var boundedPublication = new Publication(
            Guid.NewGuid(),
            "bounded-schema-api",
            "Bounded schema integration publication",
            PublicationKind.RestApi,
            "integration-test",
            Now.AddHours(-2));
        db.Publications.AddRange(primaryPublication, boundedPublication);
        await db.SaveChangesAsync();

        var inspector = scope.ServiceProvider.GetRequiredService<IPublicationSourceSchemaInspector>();
        var inspection = await inspector.InspectObjectAsync(
            sourceConnection,
            "dbo",
            "ApiIntegrationRows",
            CancellationToken.None);
        Assert.Equal(PublicationSourceInspectionStatus.Success, inspection.Status);
        var source = Assert.IsType<PublicationSourceObjectInspectionDto>(inspection.Value);

        var primaryAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = "id",
            ["Name"] = "name",
            ["IsActive"] = "isActive",
            ["Amount"] = "amount",
            ["CreatedAt"] = "createdAt",
        };
        var boundedAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = "identifier_column_exposed_to_intentionally_exceed_the_schema_response_byte_limit",
            ["Name"] = "descriptive_name_column_exposed_to_intentionally_exceed_the_schema_response_limit",
            ["IsActive"] = "active_status_column_exposed_to_intentionally_exceed_the_schema_response_limit",
            ["Amount"] = "decimal_amount_column_exposed_to_intentionally_exceed_the_schema_response_limit",
            ["CreatedAt"] = "creation_timestamp_exposed_to_intentionally_exceed_the_schema_response_limit",
        };

        var primaryVersion = CreateVersion(
            primaryPublication,
            sourceConnection.Id,
            source,
            primaryAliases,
            new PublicationVersionSettings(
                defaultPageSize: 2,
                maximumPageSize: 10,
                requestsPerWindow: 1_000,
                rateLimitWindowSeconds: 60,
                maximumConcurrentRequests: 50,
                editorWindowSize: 10,
                requestTimeoutSeconds: 1,
                commandTimeoutSeconds: 10,
                maximumResponseBytes: 1024 * 1024));
        var boundedVersion = CreateVersion(
            boundedPublication,
            sourceConnection.Id,
            source,
            boundedAliases,
            new PublicationVersionSettings(
                defaultPageSize: 2,
                maximumPageSize: 10,
                requestsPerWindow: 100,
                rateLimitWindowSeconds: 60,
                maximumConcurrentRequests: 10,
                editorWindowSize: 10,
                requestTimeoutSeconds: 5,
                commandTimeoutSeconds: 10,
                maximumResponseBytes: PublicationVersionSettings.MinimumAllowedResponseBytes));
        db.PublicationVersions.AddRange(primaryVersion, boundedVersion);
        await db.SaveChangesAsync();

        primaryPublication.ActivateVersion(primaryVersion, "integration-test", Now.AddHours(-1));
        boundedPublication.ActivateVersion(boundedVersion, "integration-test", Now.AddHours(-1));
        await db.SaveChangesAsync();

        var tokenGenerator = scope.ServiceProvider.GetRequiredService<PublicationTokenGenerator>();
        PrimaryTokenA = CreateToken(
            tokenGenerator,
            primaryPublication.Id,
            "Primary rotation A",
            Now.AddMinutes(-30),
            Now.AddDays(1));
        PrimaryTokenB = CreateToken(
            tokenGenerator,
            primaryPublication.Id,
            "Primary rotation B",
            Now.AddMinutes(-20),
            Now.AddDays(1));
        RevokedToken = CreateToken(
            tokenGenerator,
            primaryPublication.Id,
            "Revoked credential",
            Now.AddHours(-2),
            Now.AddDays(1));
        RevokedToken.Entity.Revoke("integration-test", Now.AddHours(-1));
        ExpiredToken = CreateToken(
            tokenGenerator,
            primaryPublication.Id,
            "Expired credential",
            Now.AddDays(-2),
            Now.AddDays(-1));
        BoundedPublicationToken = CreateToken(
            tokenGenerator,
            boundedPublication.Id,
            "Different publication credential",
            Now.AddMinutes(-10),
            Now.AddDays(1));
        db.PublicationAccessTokens.AddRange(
            PrimaryTokenA.Entity,
            PrimaryTokenB.Entity,
            RevokedToken.Entity,
            ExpiredToken.Entity,
            BoundedPublicationToken.Entity);
        await db.SaveChangesAsync();

        PrimarySlug = primaryPublication.Slug;
        BoundedSlug = boundedPublication.Slug;
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
    }

    public void DelayNextSchemaCatalogReadBeyondRequestTimeout() =>
        _delayController.Arm(findVersionCallNumber: 2, delay: TimeSpan.FromSeconds(5));

    public async Task<IReadOnlyDictionary<Guid, TokenUsage>> ReadTokenUsageAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<THubDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.PublicationAccessTokens
            .AsNoTracking()
            .ToDictionaryAsync(
                token => token.Id,
                token => new TokenUsage(token.AcceptedRequestCount, token.LastUsedAtUtc));
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        try
        {
            if (_databaseCreated)
            {
                if (!DatabaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean up an unexpected database name.");
                }

                var options = new DbContextOptionsBuilder<THubDbContext>()
                    .UseSqlServer(ConnectionString)
                    .Options;
                await using var db = new THubDbContext(options);
                ValidateDatabaseTarget(db.Database.GetDbConnection().Database);
                await db.Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            _factory.Dispose();
        }
    }

    private static PublicationVersion CreateVersion(
        Publication publication,
        Guid connectionId,
        PublicationSourceObjectInspectionDto source,
        IReadOnlyDictionary<string, string> aliases,
        PublicationVersionSettings settings)
    {
        var versionId = Guid.NewGuid();
        var columns = source.Columns.Select(column => new PublicationColumn(
            Guid.NewGuid(),
            versionId,
            column.Ordinal,
            column.Name,
            aliases[column.Name],
            column.DataType ?? throw new InvalidOperationException("The integration source column is unsupported."),
            column.SourceTypeName,
            column.IsNullable,
            isReadable: true,
            isFilterable: true,
            isSortable: true,
            isWritable: false,
            column.IsKey,
            column.KeyOrdinal,
            isConcurrencyToken: false,
            column.IsGenerated,
            column.MaximumLength,
            column.NumericPrecision,
            column.NumericScale)).ToArray();
        return new PublicationVersion(
            versionId,
            publication.Id,
            1,
            connectionId,
            source.Schema,
            source.Name,
            source.Kind,
            source.SchemaFingerprint,
            PublicationConcurrencyMode.ReadOnly,
            settings,
            columns,
            "integration-test",
            Now.AddMinutes(-90));
    }

    private static TokenCredential CreateToken(
        PublicationTokenGenerator generator,
        Guid publicationId,
        string name,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var material = generator.Generate();
        var entity = new PublicationAccessToken(
            Guid.NewGuid(),
            publicationId,
            name,
            material.Selector,
            Convert.ToBase64String(material.Verifier),
            algorithmVersion: 1,
            material.DisplayPrefix,
            "integration-test",
            createdAtUtc,
            expiresAtUtc);
        return new TokenCredential(entity, material.PlaintextToken);
    }

    private void ValidateDatabaseTarget(string actualDatabaseName)
    {
        if (!DatabaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            || !string.Equals(actualDatabaseName, DatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing publication integration database access. Expected exact isolated database '{DatabaseName}'.");
        }
    }

    public sealed record TokenCredential(
        PublicationAccessToken Entity,
        string PlaintextToken);

    public sealed record TokenUsage(
        long AcceptedRequestCount,
        DateTimeOffset? LastUsedAtUtc);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal sealed class PublicationApiWebApplicationFactory(
    string connectionString,
    string expectedDatabaseName,
    TimeProvider timeProvider,
    PublicationCatalogDelayController delayController) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:THub", connectionString);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:THub"] = connectionString,
                ["Serilog:FilePath"] = "logs/thub-publication-integration-.json",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextFactory<THubDbContext>>();
            services.AddSingleton<IDbContextFactory<THubDbContext>>(
                new IsolatedTHubDbContextFactory(connectionString, expectedDatabaseName));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(timeProvider);
            services.RemoveAll<IPublicationCatalogStore>();
            services.AddScoped<SqlPublicationCatalogStore>();
            services.AddSingleton(delayController);
            services.AddScoped<IPublicationCatalogStore>(provider =>
                new DelayingPublicationCatalogStore(
                    provider.GetRequiredService<SqlPublicationCatalogStore>(),
                    provider.GetRequiredService<PublicationCatalogDelayController>()));
        });
    }
}

internal sealed class IsolatedTHubDbContextFactory : IDbContextFactory<THubDbContext>
{
    private readonly DbContextOptions<THubDbContext> _options;

    public IsolatedTHubDbContextFactory(
        string connectionString,
        string expectedDatabaseName)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!expectedDatabaseName.StartsWith(
                "THub_PublicationApiTests_",
                StringComparison.Ordinal)
            || !string.Equals(
                builder.InitialCatalog,
                expectedDatabaseName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Publication integration tests require an isolated prefixed database.");
        }

        _options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(maxRetryCount: 5))
            .Options;
    }

    public THubDbContext CreateDbContext() => new(_options);

    public Task<THubDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}

internal sealed class PublicationCatalogDelayController
{
    private readonly object _sync = new();
    private int _remainingCalls;
    private TimeSpan _delay;
    private bool _armed;

    public void Arm(int findVersionCallNumber, TimeSpan delay)
    {
        if (findVersionCallNumber < 1 || delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(findVersionCallNumber));
        }

        lock (_sync)
        {
            _remainingCalls = findVersionCallNumber;
            _delay = delay;
            _armed = true;
        }
    }

    public TimeSpan? TakeDelay()
    {
        lock (_sync)
        {
            if (!_armed)
            {
                return null;
            }

            _remainingCalls--;
            if (_remainingCalls > 0)
            {
                return null;
            }

            _armed = false;
            return _delay;
        }
    }
}

internal sealed class DelayingPublicationCatalogStore(
    SqlPublicationCatalogStore inner,
    PublicationCatalogDelayController delayController) : IPublicationCatalogStore
{
    public Task<IReadOnlyList<Publication>> ListAsync(
        PublicationCatalogQuery query,
        CancellationToken cancellationToken) => inner.ListAsync(query, cancellationToken);

    public Task<Publication?> FindAsync(
        Guid publicationId,
        CancellationToken cancellationToken) => inner.FindAsync(publicationId, cancellationToken);

    public Task<Publication?> FindBySlugAsync(
        string slug,
        CancellationToken cancellationToken) => inner.FindBySlugAsync(slug, cancellationToken);

    public Task<IReadOnlyList<PublicationVersion>> ListVersionsAsync(
        Guid publicationId,
        CancellationToken cancellationToken) => inner.ListVersionsAsync(publicationId, cancellationToken);

    public async Task<PublicationVersion?> FindVersionAsync(
        Guid publicationId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        if (delayController.TakeDelay() is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return await inner.FindVersionAsync(publicationId, versionId, cancellationToken);
    }

    public Task<int> GetNextVersionNumberAsync(
        Guid publicationId,
        CancellationToken cancellationToken) => inner.GetNextVersionNumberAsync(publicationId, cancellationToken);

    public Task<PublicationCatalogWriteStatus> AddPublicationAsync(
        Publication publication,
        CancellationToken cancellationToken) => inner.AddPublicationAsync(publication, cancellationToken);

    public Task<PublicationCatalogWriteStatus> AddVersionAsync(
        PublicationVersion version,
        CancellationToken cancellationToken) => inner.AddVersionAsync(version, cancellationToken);

    public Task<PublicationCatalogWriteStatus> UpdatePublicationAsync(
        Publication publication,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken) =>
        inner.UpdatePublicationAsync(publication, expectedUpdatedAtUtc, cancellationToken);
}
