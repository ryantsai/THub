namespace THub.Domain.Publications;

public enum PublicationKind
{
    RestApi,
    Editor,
}

public enum PublicationState
{
    Draft,
    Active,
    Disabled,
    Archived,
}

public enum PublicationSourceObjectKind
{
    Table,
    View,
}

public enum PublicationDataType
{
    Boolean,
    Byte,
    Int16,
    Int32,
    Int64,
    Decimal,
    Single,
    Double,
    Date,
    DateTime,
    DateTimeOffset,
    Time,
    Guid,
    String,
    Binary,
}

public enum PublicationConcurrencyMode
{
    ReadOnly,
    RowVersion,
    OriginalValues,
}

public enum PublicationLookupMode
{
    ListValidation,
    DropDown,
    ServerFiltered,
}

public enum PublicationOperation
{
    View,
    Insert,
    Update,
    Delete,
    Approve,
}

public enum PublicationAccessTokenStatus
{
    Active,
    Expired,
    Revoked,
}

public enum PublicationChangeSetStatus
{
    Pending,
    Approved,
    Rejected,
    Applying,
    Applied,
    Conflict,
    Failed,
}

public enum PublicationChangeOperation
{
    Insert,
    Update,
    Delete,
}
