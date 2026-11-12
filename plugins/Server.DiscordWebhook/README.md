# Discord webhook action groundwork

**Not an installable LANCommander plugin yet.** This directory contains a
non-packable Discord payload and single-attempt delivery library, with offline
tests. It deliberately has no plugin registration, event consumer, subscription
store, background worker, configuration page, or release-registry entry.
No existing plugin or generic webhook behavior is changed.

## Host contract findings

Inspected the official repository at
[`d5fe374e11e46479c848dd89523ae6963b4e7e8f`](https://github.com/LANCommander/LANCommander/tree/d5fe374e11e46479c848dd89523ae6963b4e7e8f)
(main, 2026-11-12) and the published
[`v2.2.0-beta.4`](https://github.com/LANCommander/LANCommander/releases/tag/v2.2.0-beta.4)
contracts (source `675befe0673c03aded2b45995694661ce454ba95`).
The public NuGet `LANCommander.Server.Plugins` feed's latest version at
inspection was `2.2.0-beta.4`; no newer package supplies the missing action API.
This library itself has no host dependency and makes no host compatibility
claim for an integrated plugin.

The public
[`ServerEventContracts.cs`](https://github.com/LANCommander/LANCommander/blob/d5fe374e11e46479c848dd89523ae6963b4e7e8f/LANCommander.Server.Plugins/Messaging/ServerEventContracts.cs)
contains:

- `IServerEventConsumer.HandleAsync(ServerEvent, CancellationToken)`, registered
  in `IPlugin.ConfigureServices`. Every consumer receives every published event.
- `IServerEventTransport.ConfigureTransport(StandardConfigurer<ITransport>, string)`
  and `ConfigureSubscriptions(StandardConfigurer<ISubscriptionStorage>)`,
  also registered in `ConfigureServices`. The first registered transport replaces
  the in-memory **Rebus broker transport**; it is not an outbound webhook target.
  Implementing it as a Discord HTTP sender would not provide broker semantics.

[`AddServerEvents`](https://github.com/LANCommander/LANCommander/blob/d5fe374e11e46479c848dd89523ae6963b4e7e8f/LANCommander.Server.Services/Events/EventServiceCollectionExtensions.cs)
registers the concrete generic webhook consumer/handler and subscribes the bus to
`ServerEvent`. The
[`publisher`](https://github.com/LANCommander/LANCommander/blob/d5fe374e11e46479c848dd89523ae6963b4e7e8f/LANCommander.Server.Services/Events/ServerEventPublisher.cs)
publishes independently of generic webhook subscriptions, after saving built-in
outboxes. The
[`dispatcher`](https://github.com/LANCommander/LANCommander/blob/d5fe374e11e46479c848dd89523ae6963b4e7e8f/LANCommander.Server.Services/Events/ServerEventDispatcher.cs)
fans out to all consumers with the host's application-stopping token.
Consumer failures are logged by type and dropped, not retried. The default event
transport is in-memory. Durable generic webhook recovery does not replay events
to other consumers; `IServerEventOutbox` is in host Services, not the public
plugin contracts.

The built-in
[`WebhookSubscription`](https://github.com/LANCommander/LANCommander/blob/d5fe374e11e46479c848dd89523ae6963b4e7e8f/LANCommander.Server.Data/Models/WebhookSubscription.cs)
has endpoint, signing secret, event filter, enabled, timeout and attempt settings,
but no provider/action identifier or action-specific configuration. Its
[`handler`](https://github.com/LANCommander/LANCommander/blob/d5fe374e11e46479c848dd89523ae6963b4e7e8f/LANCommander.Server.Services/Events/WebhookDeliveryHandler.cs)
posts stored event-envelope JSON directly, with no plugin transformation or
delivery callback. Settings and APIs are administrator-only; saved signing
secrets are protected and not returned, but endpoint URLs are returned unchanged.
A Discord URL token cannot safely be treated as an ordinary visible endpoint.

Therefore a Discord action **inside the host's existing subscription system**
requires a public action-provider registry/discovery contract, provider ID and
secret-aware configuration schema/persistence/UI on subscriptions, and a
host-selected delivery callback with cancellation, status and retry-after
semantics. Those APIs do not exist in beta.4 or inspected main. No fake action,
reflection, internal API dependency, or host change is included here.

A separate plugin-owned Discord subscriptions UI could instead use
`IServerEventConsumer`, but that changes the product boundary: the plugin would
own filtering, protected credentials, storage, administration, queueing and
retries. Its durability could begin only after consumer receipt; event loss
before receipt or on enqueue failure would remain possible. That alternative
requires explicit approval before implementation.

## Reusable Discord behavior

`DiscordWebhookEndpoint.Parse` permits canonical HTTPS incoming webhook URLs on
`discord.com` and `discordapp.com`, using `/api/webhooks/{id}/{token}` or
`/api/v10/webhooks/{id}/{token}`. It rejects credentials, queries, fragments,
custom ports, encoded/normalized paths and arbitrary hosts. The endpoint object's
string representation is redacted; validation errors never echo the input.
Do not log, serialize or display raw endpoint input.

`DiscordPayload.Serialize` creates Discord JSON, not a LANCommander envelope.
Supported fields are content, optional username, and rich embeds containing
title, description, RGB color, fields, footer text and author name. All mention
parsing is disabled, including user/role/everyone mentions. JSON serialization
handles quoting, escaping and Unicode without string interpolation.

Validation rejects rather than truncates: content 2,000; username 80; at most
10 embeds; title/author 256; description 4,096; footer 2,048; at most 25 fields
per embed; field name 256 and value 1,024; combined embed text 6,000. Lengths
use UTF-16 code units (conservative for supplementary characters). Content or
a nonempty text embed is required. No attachment, forum/thread, component,
avatar, bot token, Discord SDK or arbitrary JSON support is included.

`DiscordWebhookClient.SendAsync` performs exactly one POST, adds controlled
`wait=true` so Discord confirms the message was saved, and accepts a 1-30 second
timeout (default 10). Cancellation propagates; timeouts and transport errors
become bounded typed results. Responses, reason phrases and exception messages
are never logged or returned. Redirects, proxies, cookies and trace-header
propagation are disabled on the default handler. There is no HTTP-client-factory
request logger. The injectable handler is for trusted implementations/tests only.
Host-installed HTTP tracing/profilers can still observe URLs: disable or redact
webhook paths in that instrumentation before integrating a credentialed client.

2xx is delivered, 408/5xx/network errors are transient, other failures
(including redirects and missing/deleted webhooks) are rejected. HTTP 429
returns a rate-limit result and the greater valid delay from `Retry-After`
(seconds or HTTP date) and JSON `retry_after` (fractional seconds). Untrusted
rate-limit bodies are limited to 4 KiB. Invalid/missing delay remains explicitly
unknown (`null`), not success. There is **no automatic retry, sleep, rate-limit
bucket coordinator, persistent delivery history or restart recovery**. A future
adapter must schedule retries without shortening supplied delays, coordinate
per-webhook/global limits, and avoid retrying permanent failures. An ambiguous
network timeout may follow a successful send; retrying can duplicate messages.
Do not use the single-attempt client as a complete notification service.

Discord references:
[execute webhook](https://docs.discord.com/developers/resources/webhook#execute-webhook),
[embed limits](https://docs.discord.com/developers/resources/message#embed-object-embed-limits),
[rate limits](https://docs.discord.com/developers/topics/rate-limits).

## Development

Requires .NET 10 SDK. All delivery tests use an in-process fake handler and
synthetic endpoints; no network messages or real credentials are used.

```powershell
dotnet test .\plugins\Server.DiscordWebhook\tests\LANCommander.Community.Server.DiscordWebhook.Tests\LANCommander.Community.Server.DiscordWebhook.Tests.csproj -c Release --nologo
pwsh .\eng\Test-ReleaseSelection.ps1
pwsh .\eng\Test-Repository.ps1
```

CI runs the independent tests. Host registration/discovery, permissions/UI,
installation, delivery persistence and real Discord delivery are not exercised
or claimed. The reserved future project/package/assembly identity is
`LANCommander.Community.Server.DiscordWebhook`. It remains non-packable until a
supported integration is implemented and verified; no version, changelog,
package types, registry entry, release allowlist or publication claim is added.
