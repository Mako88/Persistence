using System.Net;

namespace Persistence.Api.Tests;

/// <summary>
/// The API also serves the shared web client (a static page) so John/Claude/Ember can watch and talk
/// to the peer through one backend. These assert the page is served at the root and is wired to the
/// real API endpoints it depends on.
/// </summary>
public class WebClientTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture api;

    public WebClientTests(ApiTestFixture api) => this.api = api;

    [Fact]
    public async Task RootServesTheHtmlClient()
    {
        var client = api.CreateClient();

        var res = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);

        var html = await res.Content.ReadAsStringAsync();
        // Wired to the endpoints it actually uses, so a rename of those routes would fail loudly here.
        Assert.Contains("/api/conversation/stream", html);
        Assert.Contains("/api/conversation/send", html);
        Assert.Contains("X-Local-Peer", html);
    }
}
