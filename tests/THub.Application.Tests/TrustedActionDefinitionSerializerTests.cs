using THub.Application.Actions;
using THub.Domain.Actions;

namespace THub.Application.Tests;

public sealed class TrustedActionDefinitionSerializerTests
{
    private readonly TrustedActionDefinitionSerializer serializer = new();

    [Fact]
    public void WebhookDefinitionRejectsCredentialsOverHttp()
    {
        var exception = Assert.Throws<TrustedActionDefinitionException>(() =>
            serializer.Deserialize(
                TrustedActionKind.Webhook,
                """
                {
                  "destination": "http://example.test/hook",
                  "method": "POST",
                  "contentType": "application/json",
                  "authentication": "bearer",
                  "allowPrivateAddresses": false,
                  "timeoutSeconds": 30,
                  "maximumRequestBytes": 1024,
                  "maximumResponseBytes": 1024,
                  "headers": {}
                }
                """));

        Assert.Equal("trusted-action.webhook.authentication-tls", exception.Code);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    public void ExecutableDefinitionRejectsShellsAndScriptHosts(string path)
    {
        var json =
            $$"""
              {
                "executablePath": {{System.Text.Json.JsonSerializer.Serialize(path)}},
                "workingDirectory": "C:\\Windows",
                "arguments": [],
                "environment": {},
                "timeoutSeconds": 30,
                "maximumOutputCharacters": 1024,
                "loadUserProfile": false
              }
              """;

        var exception = Assert.Throws<TrustedActionDefinitionException>(() =>
            serializer.Deserialize(TrustedActionKind.Executable, json));

        Assert.Equal("trusted-action.executable.shell", exception.Code);
    }

    [Fact]
    public void ExecutableDefinitionAcceptsOnlyKnownArgumentPlaceholders()
    {
        var exception = Assert.Throws<TrustedActionDefinitionException>(() =>
            serializer.Deserialize(
                TrustedActionKind.Executable,
                """
                {
                  "executablePath": "C:\\Tools\\approved.exe",
                  "workingDirectory": "C:\\Tools",
                  "arguments": ["--value", "{workflowInput}"],
                  "environment": {},
                  "timeoutSeconds": 30,
                  "maximumOutputCharacters": 1024,
                  "loadUserProfile": false
                }
                """));

        Assert.Equal("trusted-action.executable.argument-template", exception.Code);
    }
}
