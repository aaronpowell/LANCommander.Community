using System.Net;
using System.Text.Json;
using Xunit;

namespace LANCommander.Community.Server.DiscordWebhook.Tests;

public sealed class DiscordWebhookTests
{
    // Synthetic credentials; every request is intercepted by FakeHandler.
    private const string Endpoint = "https://discord.com/api/webhooks/123456789012345678/synthetic-token";

    [Theory]
    [InlineData(Endpoint)]
    [InlineData("https://discordapp.com/api/webhooks/123/synthetic_token")]
    [InlineData("https://discord.com/api/v10/webhooks/123/synthetic-token")]
    public void AcceptsSupportedIncomingWebhookEndpoints(string value)
    {
        Assert.Equal("[Discord webhook endpoint redacted]", DiscordWebhookEndpoint.Parse(value).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://discord.com/api/webhooks/123/synthetic-token")]
    [InlineData("https://discord.com.evil.example/api/webhooks/123/synthetic-token")]
    [InlineData("https://127.0.0.1/api/webhooks/123/synthetic-token")]
    [InlineData("https://discord.com:444/api/webhooks/123/synthetic-token")]
    [InlineData("https://user:password@discord.com/api/webhooks/123/synthetic-token")]
    [InlineData(Endpoint + "?wait=true")]
    [InlineData(Endpoint + "#fragment")]
    [InlineData(Endpoint + "/messages/123")]
    [InlineData(Endpoint + "/")]
    [InlineData("https://discord.com/api/webhooks/id/synthetic-token")]
    [InlineData("https://discord.com/api/webhooks/123/")]
    [InlineData("https://discord.com/api/webhooks/123/%73ynthetic-token")]
    [InlineData("https://discord.com/api/webhooks/123/../123/synthetic-token")]
    [InlineData("https://discord.com\\api\\webhooks\\123\\synthetic-token")]
    [InlineData(" " + Endpoint)]
    public void RejectsUnsafeOrUnsupportedEndpointsWithoutEchoingInput(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => DiscordWebhookEndpoint.Parse(value));
        Assert.DoesNotContain("synthetic", exception.ToString());
        Assert.DoesNotContain("password", exception.ToString());
        Assert.DoesNotContain("https://", exception.ToString());
    }

    [Fact]
    public void SerializesDiscordShapeAndEscapesUserContentWithoutEnablingMentions()
    {
        const string text = "Quote \" slash \\ newline\n @everyone <@123> \U0001F3AE";
        var message = new DiscordMessage(text,
            [new(Title: "Game", Description: text, Color: 0x123456,
                Fields: [new("Name", text, true)], Footer: "Footer", Author: "LANCommander")],
            Username: "Community");
        using var json = JsonDocument.Parse(DiscordPayload.Serialize(message));
        var root = json.RootElement;
        Assert.Equal(text, root.GetProperty("content").GetString());
        Assert.Equal("Community", root.GetProperty("username").GetString());
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        var embed = root.GetProperty("embeds")[0];
        Assert.Equal(text, embed.GetProperty("description").GetString());
        Assert.Equal(0x123456, embed.GetProperty("color").GetInt32());
        Assert.Equal("Footer", embed.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal("LANCommander", embed.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(text, embed.GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.True(embed.GetProperty("fields")[0].GetProperty("inline").GetBoolean());
        Assert.False(root.TryGetProperty("payload", out _));
    }

    [Fact]
    public void SupportsContentOnlyOrEmbedOnlyAndOmitsUnsetFields()
    {
        using var content = JsonDocument.Parse(DiscordPayload.Serialize(new("Hello")));
        Assert.False(content.RootElement.TryGetProperty("embeds", out _));
        Assert.False(content.RootElement.TryGetProperty("username", out _));
        using var embed = JsonDocument.Parse(DiscordPayload.Serialize(new(Embeds: [new(Description: "Hello")])));
        Assert.False(embed.RootElement.TryGetProperty("content", out _));
        Assert.False(embed.RootElement.GetProperty("embeds")[0].TryGetProperty("fields", out _));
    }

    public static IEnumerable<object[]> InvalidMessages()
    {
        yield return [new DiscordMessage()];
        yield return [new DiscordMessage(" ")];
        yield return [new DiscordMessage(new string('x', 2001))];
        yield return [new DiscordMessage("OK", Username: new string('x', 81))];
        yield return [new DiscordMessage(Embeds: Enumerable.Repeat(new DiscordEmbed(Title: "x"), 11).ToArray())];
        yield return [new DiscordMessage(Embeds: [new()])];
        yield return [new DiscordMessage(Embeds: [new(Title: new string('x', 257))])];
        yield return [new DiscordMessage(Embeds: [new(Description: new string('x', 4097))])];
        yield return [new DiscordMessage(Embeds: [new(Footer: new string('x', 2049))])];
        yield return [new DiscordMessage(Embeds: [new(Author: new string('x', 257))])];
        yield return [new DiscordMessage(Embeds: [new(Title: "x", Color: -1)])];
        yield return [new DiscordMessage(Embeds: [new(Title: "x", Color: 0x1000000)])];
        yield return [new DiscordMessage(Embeds: [new(Fields: [new("", "x")])])];
        yield return [new DiscordMessage(Embeds: [new(Fields: [new("x", " ")])])];
        yield return [new DiscordMessage(Embeds: [new(Fields: [new(new string('x', 257), "x")])])];
        yield return [new DiscordMessage(Embeds: [new(Fields: [new("x", new string('x', 1025))])])];
        yield return [new DiscordMessage(Embeds: [new(Fields: Enumerable.Repeat(new DiscordEmbedField("x", "x"), 26).ToArray())])];
        yield return [new DiscordMessage(Embeds: [new(Description: new string('x', 3000)), new(Description: new string('x', 3001))])];
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void RejectsInvalidPayloadsBeforeSending(DiscordMessage message)
    {
        Assert.Throws<ArgumentException>(() => DiscordPayload.Serialize(message));
    }

    [Fact]
    public void AcceptsExactContentAndCombinedEmbedBounds()
    {
        var message = new DiscordMessage(new string('x', 2000),
            [new(Description: new string('x', 4096), Footer: new string('x', 1904))],
            new string('x', 80));
        using var json = JsonDocument.Parse(DiscordPayload.Serialize(message));
        Assert.Equal(2000, json.RootElement.GetProperty("content").GetString()!.Length);
    }

    [Theory]
    [InlineData(200, DiscordDeliveryStatus.Delivered)]
    [InlineData(204, DiscordDeliveryStatus.Delivered)]
    [InlineData(302, DiscordDeliveryStatus.Rejected)]
    [InlineData(400, DiscordDeliveryStatus.Rejected)]
    [InlineData(401, DiscordDeliveryStatus.Rejected)]
    [InlineData(404, DiscordDeliveryStatus.Rejected)]
    [InlineData(408, DiscordDeliveryStatus.TransientFailure)]
    [InlineData(500, DiscordDeliveryStatus.TransientFailure)]
    [InlineData(503, DiscordDeliveryStatus.TransientFailure)]
    public async Task MapsHttpStatusWithoutReturningReceiverContent(int code, DiscordDeliveryStatus expected)
    {
        var handler = new FakeHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Endpoint + "?wait=true", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal("Hello", json.RootElement.GetProperty("content").GetString());
            return new((HttpStatusCode)code)
            {
                ReasonPhrase = Endpoint,
                Content = new StringContent(Endpoint)
            };
        });
        using var client = new DiscordWebhookClient(handler);
        var result = await client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"));
        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.HttpStatusCode);
        Assert.DoesNotContain("synthetic", result.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("2.5", "{\"retry_after\":1.25}", 2.5)]
    [InlineData("1", "{\"retry_after\":2.75,\"global\":true}", 2.75)]
    [InlineData(null, "{\"retry_after\":0.125}", 0.125)]
    [InlineData(null, "untrusted response", null)]
    [InlineData(null, "{\"retry_after\":-1}", null)]
    [InlineData(null, "{\"retry_after\":1e300}", null)]
    [InlineData(null, "{\"retry_after\":\"5\"}", null)]
    [InlineData(null, "[]", null)]
    public async Task SurfacesRateLimitDelayWithoutRetrying(string? header, string body, double? seconds)
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) };
            if (header is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        });
        using var client = new DiscordWebhookClient(handler);
        var result = await client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"));
        Assert.Equal(DiscordDeliveryStatus.RateLimited, result.Status);
        Assert.Equal(seconds, result.RetryAfter?.TotalSeconds);
        Assert.Equal(429, result.HttpStatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BoundsRateLimitResponseBody()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"retry_after\":2,\"padding\":\"" + new string('x', 5000) + "\"}")
        }));
        using var client = new DiscordWebhookClient(handler);
        var result = await client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"));
        Assert.Equal(DiscordDeliveryStatus.RateLimited, result.Status);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task ParsesHttpDateRetryAfter()
    {
        var date = DateTimeOffset.UtcNow.AddMinutes(2);
        using var client = new DiscordWebhookClient(new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}")
            };
            response.Headers.RetryAfter = new(date);
            return Task.FromResult(response);
        }));
        var result = await client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"));
        Assert.Equal(DiscordDeliveryStatus.RateLimited, result.Status);
        Assert.InRange(result.RetryAfter!.Value.TotalSeconds, 100, 120);
    }

    [Fact]
    public async Task RejectsInvalidMessageWithoutInvokingHandlerOrEchoingContent()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Should not send"));
        using var client = new DiscordWebhookClient(handler);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new(new string('x', 2001))));
        Assert.DoesNotContain(new string('x', 100), exception.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RedactsTransportExceptionRatherThanRetainingIt()
    {
        using var client = new DiscordWebhookClient(new FakeHandler((_, _) =>
            throw new HttpRequestException("Failed at " + Endpoint, new IOException(Endpoint))));
        var result = await client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"));
        Assert.Equal(DiscordDeliveryStatus.TransientFailure, result.Status);
        Assert.DoesNotContain("synthetic", result.ToString());
        Assert.Null(result.HttpStatusCode);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndDoesNotBecomeRetryableFailure()
    {
        using var cts = new CancellationTokenSource();
        using var client = new DiscordWebhookClient(new FakeHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.NoContent);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task PreCanceledCallDoesNotSend()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Should not send"));
        using var client = new DiscordWebhookClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"), cancellationToken: new(true)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EnforcesTimeoutOnPendingNetworkOperation()
    {
        using var client = new DiscordWebhookClient(new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.NoContent);
        }));
        var result = await client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"), timeoutSeconds: 1);
        Assert.Equal(DiscordDeliveryStatus.TimedOut, result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task RejectsInvalidTimeoutBeforeSending(int timeout)
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Should not send"));
        using var client = new DiscordWebhookClient(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.SendAsync(DiscordWebhookEndpoint.Parse(Endpoint), new("Hello"), timeout));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
