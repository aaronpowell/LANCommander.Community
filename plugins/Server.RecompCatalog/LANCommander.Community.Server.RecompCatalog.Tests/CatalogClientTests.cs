using System.Net;
using System.Text;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.Community.Server.RecompCatalog.Providers;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class CatalogClientTests
{
    [Fact]
    public async Task LoadsIndexAndCatalogLists()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var fixture = request.RequestUri!.AbsolutePath.EndsWith("index.json")
                ? "index.json"
                : "apps.json";
            var json = File.ReadAllText(Fixture(fixture));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var client = new CatalogClient(new HttpClient(handler));

        var sources = await client.LoadAsync(new Uri("https://catalog.test/index.json"));

        var source = Assert.Single(sources);
        Assert.Equal("Fixture Recomps", source.Name);
        Assert.Equal("Fixture Game - Fixture Recompiled", Assert.Single(source.Entries).DisplayName);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
