namespace THub.Application.Workflows;

public sealed class WorkflowGraphSerializationException : Exception
{
    public WorkflowGraphSerializationException(string message)
        : base(message)
    {
    }

    public WorkflowGraphSerializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

