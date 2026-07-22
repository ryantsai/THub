using Microsoft.AspNetCore.Mvc.Testing;

namespace THub.Publications.Tests;

[CollectionDefinition(Name)]
public sealed class PublicationHostCollection : ICollectionFixture<WebApplicationFactory<Program>>
{
    public const string Name = "Publication host";
}
