using THub.Application.Publications;

namespace THub.Publications.Tests;

public sealed class PublicationAdmissionGateTests
{
    [Fact]
    public void EnforcesConcurrencyBeforeMeteringLeaseIsReleased()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-07-23T00:00:00Z"));
        var gate = new PublicationAdmissionGate(clock);
        var context = CreateContext(requests: 10, seconds: 60, concurrency: 1);

        var first = gate.TryEnter(context);
        var second = gate.TryEnter(context);

        Assert.True(first.IsAccepted);
        Assert.False(second.IsAccepted);
        Assert.Equal("publication.concurrent_limit", second.ReasonCode);

        first.Lease!.Dispose();
        var third = gate.TryEnter(context);
        Assert.True(third.IsAccepted);
        third.Lease!.Dispose();
    }

    [Fact]
    public void EnforcesFixedWindowAndResetsAfterConfiguredWindow()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-07-23T00:00:00Z"));
        var gate = new PublicationAdmissionGate(clock);
        var context = CreateContext(requests: 2, seconds: 30, concurrency: 5);

        using (gate.TryEnter(context).Lease!) { }
        using (gate.TryEnter(context).Lease!) { }
        var rejected = gate.TryEnter(context);
        Assert.False(rejected.IsAccepted);
        Assert.Equal("publication.rate_limit", rejected.ReasonCode);

        clock.Advance(TimeSpan.FromSeconds(30));
        var accepted = gate.TryEnter(context);
        Assert.True(accepted.IsAccepted);
        accepted.Lease!.Dispose();
    }

    [Fact]
    public void ActiveVersionGetsAnIndependentPartition()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-07-23T00:00:00Z"));
        var gate = new PublicationAdmissionGate(clock);
        var firstVersion = CreateContext(requests: 1, seconds: 60, concurrency: 1);
        using (gate.TryEnter(firstVersion).Lease!) { }
        Assert.False(gate.TryEnter(firstVersion).IsAccepted);

        var nextVersion = firstVersion with { PublicationVersionId = Guid.NewGuid() };
        var accepted = gate.TryEnter(nextVersion);
        Assert.True(accepted.IsAccepted);
        accepted.Lease!.Dispose();
    }

    private static ValidatedPublicationTokenDto CreateContext(
        int requests,
        int seconds,
        int concurrency) => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            requests,
            seconds,
            concurrency,
            30,
            1_000_000);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
