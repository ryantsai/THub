namespace THub.Domain.Alerts;

public sealed class EmailAttachment
{
    public const int AbsoluteMaximumBytes = 10 * 1024 * 1024;
    public const int MaximumFileNameLength = 128;
    public const int MaximumMediaTypeLength = 100;

    private readonly byte[] _content;

    public EmailAttachment(string fileName, string mediaType, ReadOnlyMemory<byte> content)
    {
        FileName = DomainGuard.Require(fileName, nameof(fileName), MaximumFileNameLength);
        if (FileName != Path.GetFileName(FileName)
            || FileName is "." or ".."
            || FileName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || FileName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An Email attachment file name must be a safe leaf name.",
                nameof(fileName));
        }

        MediaType = DomainGuard.Require(mediaType, nameof(mediaType), MaximumMediaTypeLength);
        if (MediaType.Any(character =>
                char.IsControl(character)
                || character is not (>= ' ' and <= '~')))
        {
            throw new ArgumentException(
                "An Email attachment media type must contain only visible ASCII characters.",
                nameof(mediaType));
        }

        if (content.Length is < 1 or > AbsoluteMaximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                $"An Email attachment must contain between 1 and {AbsoluteMaximumBytes} bytes.");
        }

        _content = content.ToArray();
    }

    public string FileName { get; }

    public string MediaType { get; }

    public ReadOnlyMemory<byte> Content => _content;
}
