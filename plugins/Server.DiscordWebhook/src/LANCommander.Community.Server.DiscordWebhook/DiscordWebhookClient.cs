using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LANCommander.Community.Server.DiscordWebhook;

public enum DiscordDeliveryStatus
{
    Delivered,
    Rejected,
    RateLimited,
    TransientFailure,
    TimedOut
}

public sealed record DiscordDeliveryResult(
    DiscordDeliveryStatus Status,
    int? HttpStatusCode = null,
    TimeSpan? RetryAfter = null);

/// <summary>One attempt only; a future host action adapter must own durable scheduling.</summary>
public sealed class DiscordWebhookClient : IDisposable
{
    private readonly HttpClient _client;

    public DiscordWebhookClient() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ActivityHeadersPropagator = null,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { }

    /// <summary>
    /// Takes ownership of the handler. For tests or trusted handlers only; custom handlers
    /// must not redirect, log request URIs, attach credentials or retry automatically.
    /// </summary>
    public DiscordWebhookClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<DiscordDeliveryResult> SendAsync(
        DiscordWebhookEndpoint endpoint,
        DiscordMessage message,
        int timeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeoutSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be 1-30 seconds.");
        var body = DiscordPayload.Serialize(message);
        // Discord's default wait=false can acknowledge a message that was never saved.
        var address = new UriBuilder(endpoint.Address) { Query = "wait=true" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, address);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return new(DiscordDeliveryStatus.Delivered, code);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var headerDelay = ReadRetryAfter(response);
                var bodyDelay = await ReadBodyRetryAfterAsync(response, timeout.Token);
                var delay = headerDelay is null ? bodyDelay :
                    bodyDelay is null || headerDelay >= bodyDelay ? headerDelay : bodyDelay;
                return new(DiscordDeliveryStatus.RateLimited, code, delay);
            }
            return new(code >= 500 || code == 408
                ? DiscordDeliveryStatus.TransientFailure : DiscordDeliveryStatus.Rejected, code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(DiscordDeliveryStatus.TimedOut);
        }
        catch (HttpRequestException)
        {
            return new(DiscordDeliveryStatus.TransientFailure);
        }
        catch (IOException)
        {
            return new(DiscordDeliveryStatus.TransientFailure);
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;
        var value = values.FirstOrDefault();
        if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            return Delay(seconds);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return Delay(Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds));
        return null;
    }

    private static async Task<TimeSpan?> ReadBodyRetryAfterAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Discord returns retry_after in seconds, potentially fractional. Bound untrusted bodies.
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[4097];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            length += read;
        }
        if (length > 4096)
            return null;
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("retry_after", out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds)
                    ? Delay(seconds) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? Delay(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds - 1
            ? TimeSpan.FromSeconds(seconds) : null;

    public void Dispose() => _client.Dispose();
}
