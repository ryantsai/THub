using THub.Domain.Publications;

namespace THub.Application.Publications;

public static class PublicationColumnMutationPolicy
{
    public static bool CanSupplyOnInsert(PublicationColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return !column.IsGenerated &&
               !column.IsConcurrencyToken &&
               (column.IsWritable || column.IsKey);
    }

    public static bool CanSetOnUpdate(PublicationColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return column.IsWritable &&
               !column.IsKey &&
               !column.IsGenerated &&
               !column.IsConcurrencyToken;
    }
}
