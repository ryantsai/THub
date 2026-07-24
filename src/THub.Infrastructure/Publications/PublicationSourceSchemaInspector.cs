using THub.Application.Publications;
using THub.Domain.Connections;

namespace THub.Infrastructure.Publications;

public sealed class PublicationSourceSchemaInspector(
    SqlPublicationSourceSchemaInspector sqlServer,
    RelationalPublicationSourceSchemaInspector relational)
    : IPublicationSourceSchemaInspector
{
    public Task<PublicationSourceInspectionResult<PublicationSourceObjectPageDto>> ListObjectsAsync(
        DataConnection connection,
        string? search,
        int take,
        CancellationToken cancellationToken) =>
        connection.Kind switch
        {
            ConnectionKind.SqlServer => sqlServer.ListObjectsAsync(
                connection,
                search,
                take,
                cancellationToken),
            ConnectionKind.MySql or ConnectionKind.PostgreSql or ConnectionKind.Oracle =>
                relational.ListObjectsAsync(connection, search, take, cancellationToken),
            _ => Task.FromResult(
                new PublicationSourceInspectionResult<PublicationSourceObjectPageDto>(
                    PublicationSourceInspectionStatus.Unsupported,
                    null))
        };

    public Task<PublicationSourceInspectionResult<PublicationSourceObjectInspectionDto>> InspectObjectAsync(
        DataConnection connection,
        string schema,
        string objectName,
        CancellationToken cancellationToken) =>
        connection.Kind switch
        {
            ConnectionKind.SqlServer => sqlServer.InspectObjectAsync(
                connection,
                schema,
                objectName,
                cancellationToken),
            ConnectionKind.MySql or ConnectionKind.PostgreSql or ConnectionKind.Oracle =>
                relational.InspectObjectAsync(connection, schema, objectName, cancellationToken),
            _ => Task.FromResult(
                new PublicationSourceInspectionResult<PublicationSourceObjectInspectionDto>(
                    PublicationSourceInspectionStatus.Unsupported,
                    null))
        };
}
