namespace THub.Domain.Alerts;

internal static class EmailPolicy
{
    public const int MaximumAddressLength = 320;

    private const string DotAtomCharacters =
        "!#$%&'*+-/=?^_`{|}~.";

    public static string Address(string value, string parameterName)
    {
        var address = DomainGuard.Require(value, parameterName, MaximumAddressLength);
        if (address.Any(char.IsWhiteSpace) || address.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Email addresses cannot contain whitespace or control characters.",
                parameterName);
        }

        var separator = address.LastIndexOf('@');
        if (separator <= 0
            || separator != address.IndexOf('@')
            || separator == address.Length - 1
            || separator > 64)
        {
            throw new ArgumentException("A valid Email address is required.", parameterName);
        }

        var localPart = address[..separator];
        if (localPart.StartsWith('.')
            || localPart.EndsWith('.')
            || localPart.Contains("..", StringComparison.Ordinal)
            || localPart.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || DotAtomCharacters.Contains(character))))
        {
            throw new ArgumentException(
                "Email addresses must use an ASCII dot-atom local part.",
                parameterName);
        }

        _ = Domain(address[(separator + 1)..], parameterName);
        return address;
    }

    public static string Domain(string value, string parameterName)
    {
        var domain = DomainGuard.Require(value, parameterName, 253)
            .TrimEnd('.')
            .ToLowerInvariant();

        if (domain.Length == 0
            || domain.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException("A valid ASCII domain is required.", parameterName);
        }

        var labels = domain.Split('.');
        if (labels.Any(label =>
                label.Length is < 1 or > 63
                || label.StartsWith('-')
                || label.EndsWith('-')))
        {
            throw new ArgumentException("A valid ASCII domain is required.", parameterName);
        }

        return domain;
    }

    public static string SmtpHost(string value, string parameterName)
    {
        var host = DomainGuard.Require(value, parameterName, 253);
        if (host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || host.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid SMTP host name or IP address is required.", parameterName);
        }

        if (System.Net.IPAddress.TryParse(host, out _))
        {
            return host;
        }

        return Domain(host, parameterName);
    }

    public static string GetDomain(string address) =>
        address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant();
}
