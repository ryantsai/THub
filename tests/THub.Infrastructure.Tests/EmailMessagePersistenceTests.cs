using THub.Domain.Alerts;
using THub.Infrastructure.Persistence.Configurations;

namespace THub.Infrastructure.Tests;

public sealed class EmailMessagePersistenceTests
{
    [Fact]
    public void RoundTripsHtmlAndAttachmentWithoutBreakingLegacyMessages()
    {
        var message = new EmailMessage(
            ["owner@example.com"],
            "Data",
            "<p>Attached</p>",
            isBodyHtml: true,
            new EmailAttachment(
                "results.csv",
                "text/csv; charset=utf-8",
                new byte[] { 1, 2, 3 }));

        var restored = Serialization.DeserializeMessage(
            Serialization.SerializeMessage(message));
        var attachment = Assert.IsType<EmailAttachment>(restored.Attachment);

        Assert.True(restored.IsBodyHtml);
        Assert.Equal("results.csv", attachment.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, attachment.Content.ToArray());

        var legacy = Serialization.DeserializeMessage(
            """{"recipients":["ops@example.com"],"subject":"Alert","body":"Body"}""");

        Assert.False(legacy.IsBodyHtml);
        Assert.Null(legacy.Attachment);
    }
}
