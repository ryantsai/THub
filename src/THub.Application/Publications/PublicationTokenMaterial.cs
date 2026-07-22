namespace THub.Application.Publications;

public sealed record PublicationTokenMaterial(
    string Selector,
    string DisplayPrefix,
    byte[] Verifier,
    string PlaintextToken,
    string Algorithm)
{
    public const string CurrentAlgorithm = "SHA256-V1";
}

