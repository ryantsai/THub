using System.ComponentModel.DataAnnotations;

namespace THub.Worker.Publications;

public sealed class PublicationChangeSetWorkerOptions
{
    public const string SectionName = "PublicationApply";

    [Range(100, 60_000)]
    public int PollIntervalMilliseconds { get; set; } = 2_000;

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
}
