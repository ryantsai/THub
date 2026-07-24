using THub.Domain.Auditing;

namespace THub.Application.Auditing;

public sealed record AuditActorContext(
    AuditActorKind Kind,
    string Identifier);

public static class AuditContext
{
    private static readonly AsyncLocal<AuditActorContext?> CurrentActor = new();

    public static AuditActorContext? Current => CurrentActor.Value;

    public static IDisposable Push(AuditActorKind kind, string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var previous = CurrentActor.Value;
        CurrentActor.Value = new AuditActorContext(kind, identifier.Trim());
        return new Scope(previous);
    }

    private sealed class Scope(AuditActorContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentActor.Value = previous;
            _disposed = true;
        }
    }
}
