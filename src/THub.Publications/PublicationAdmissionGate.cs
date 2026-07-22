using System.Collections.Concurrent;
using THub.Application.Publications;

namespace THub.Publications;

/// <summary>
/// Single-host, process-local admission required by ADR-0011. Keys include the immutable active
/// version so a policy change receives a fresh limiter. Scale-out requires a distributed gate.
/// </summary>
public sealed class PublicationAdmissionGate(TimeProvider timeProvider)
{
    private const int MaximumTrackedPartitions = 100_000;
    private readonly ConcurrentDictionary<AdmissionKey, AdmissionState> _states = new();
    private long _admissionAttempts;

    public AdmissionResult TryEnter(ValidatedPublicationTokenDto token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var now = timeProvider.GetUtcNow();
        var key = new AdmissionKey(token.TokenId, token.PublicationVersionId);
        if (!_states.TryGetValue(key, out var state))
        {
            if (_states.Count >= MaximumTrackedPartitions)
            {
                RemoveIdlePartitions(now, force: true);
                if (_states.Count >= MaximumTrackedPartitions)
                {
                    return AdmissionResult.Unavailable;
                }
            }

            state = _states.GetOrAdd(key, _ => new AdmissionState(token, now));
        }

        var result = state.TryEnter(token, now);
        if (Interlocked.Increment(ref _admissionAttempts) % 256 == 0)
        {
            RemoveIdlePartitions(now, force: false);
        }

        return result;
    }

    private void RemoveIdlePartitions(DateTimeOffset now, bool force)
    {
        var scanned = 0;
        foreach (var item in _states)
        {
            if (!force && scanned++ >= 512)
            {
                break;
            }

            if (item.Value.CanRemove(now))
            {
                _states.TryRemove(item);
            }
        }
    }

    private readonly record struct AdmissionKey(Guid TokenId, Guid VersionId);

    private sealed class AdmissionState
    {
        private readonly object _sync = new();
        private DateTimeOffset _windowStartedAtUtc;
        private DateTimeOffset _lastTouchedAtUtc;
        private int _requestsInWindow;
        private int _activeRequests;
        private int _requestsPerWindow;
        private int _windowSeconds;
        private int _maximumConcurrentRequests;

        public AdmissionState(ValidatedPublicationTokenDto token, DateTimeOffset now)
        {
            _windowStartedAtUtc = now;
            _lastTouchedAtUtc = now;
            ApplyPolicy(token);
        }

        public AdmissionResult TryEnter(
            ValidatedPublicationTokenDto token,
            DateTimeOffset now)
        {
            lock (_sync)
            {
                _lastTouchedAtUtc = now;
                if (PolicyChanged(token))
                {
                    ApplyPolicy(token);
                    _windowStartedAtUtc = now;
                    _requestsInWindow = 0;
                }

                var window = TimeSpan.FromSeconds(_windowSeconds);
                if (now - _windowStartedAtUtc >= window || now < _windowStartedAtUtc)
                {
                    _windowStartedAtUtc = now;
                    _requestsInWindow = 0;
                }

                if (_activeRequests >= _maximumConcurrentRequests)
                {
                    return AdmissionResult.Rejected(
                        TimeSpan.FromSeconds(1),
                        "publication.concurrent_limit");
                }

                if (_requestsInWindow >= _requestsPerWindow)
                {
                    var retryAfter = (_windowStartedAtUtc + window) - now;
                    return AdmissionResult.Rejected(
                        retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1),
                        "publication.rate_limit");
                }

                _activeRequests++;
                _requestsInWindow++;
                return AdmissionResult.Accepted(new AdmissionLease(this));
            }
        }

        public bool CanRemove(DateTimeOffset now)
        {
            lock (_sync)
            {
                var idleThreshold = TimeSpan.FromSeconds(Math.Max(_windowSeconds * 2L, 120));
                return _activeRequests == 0 && now - _lastTouchedAtUtc >= idleThreshold;
            }
        }

        private void Exit()
        {
            lock (_sync)
            {
                if (_activeRequests <= 0)
                {
                    throw new InvalidOperationException("Publication admission lease underflow.");
                }

                _activeRequests--;
            }
        }

        private bool PolicyChanged(ValidatedPublicationTokenDto token) =>
            _requestsPerWindow != token.RequestsPerWindow
            || _windowSeconds != token.RateLimitWindowSeconds
            || _maximumConcurrentRequests != token.MaximumConcurrentRequests;

        private void ApplyPolicy(ValidatedPublicationTokenDto token)
        {
            _requestsPerWindow = token.RequestsPerWindow;
            _windowSeconds = token.RateLimitWindowSeconds;
            _maximumConcurrentRequests = token.MaximumConcurrentRequests;
        }

        private sealed class AdmissionLease(AdmissionState owner) : IDisposable
        {
            private AdmissionState? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}

public sealed record AdmissionResult
{
    private AdmissionResult(
        bool isAccepted,
        bool isUnavailable,
        IDisposable? lease,
        TimeSpan? retryAfter,
        string? reasonCode)
    {
        IsAccepted = isAccepted;
        IsUnavailable = isUnavailable;
        Lease = lease;
        RetryAfter = retryAfter;
        ReasonCode = reasonCode;
    }

    public bool IsAccepted { get; }
    public bool IsUnavailable { get; }
    public IDisposable? Lease { get; }
    public TimeSpan? RetryAfter { get; }
    public string? ReasonCode { get; }

    public static AdmissionResult Unavailable { get; } =
        new(false, true, null, null, "publication.admission_unavailable");

    public static AdmissionResult Accepted(IDisposable lease) =>
        new(true, false, lease, null, null);

    public static AdmissionResult Rejected(TimeSpan retryAfter, string reasonCode) =>
        new(false, false, null, retryAfter, reasonCode);
}
