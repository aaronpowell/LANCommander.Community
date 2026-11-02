using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanCommander.HomeAssistant.Core;

public sealed record LaunchCommand(
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("gameId")] Guid GameId,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

public static class LaunchCommandParser
{
    public const int MaximumPayloadBytes = 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool TryParse(ReadOnlySpan<byte> payload, out LaunchCommand? command)
    {
        command = null;
        if (payload.IsEmpty || payload.Length > MaximumPayloadBytes)
            return false;

        try
        {
            command = JsonSerializer.Deserialize<LaunchCommand>(payload, Options);
            return command is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public static class LaunchCommandValidator
{
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(5);

    public static string? Validate(LaunchCommand command, DateTimeOffset now)
    {
        if (command.RequestId == Guid.Empty)
            return "requestId must be a non-empty UUID";
        if (command.Action is not ("launch" or "install" or "select" or "launch_selected" or "install_selected"))
            return "unsupported action";
        if (command.GameId == Guid.Empty && command.Action is not ("launch_selected" or "install_selected"))
            return "gameId must be a non-empty UUID";
        if (command.IssuedAt > now + AllowedClockSkew)
            return "issuedAt is in the future";
        if (command.ExpiresAt <= command.IssuedAt)
            return "expiresAt must be later than issuedAt";
        if (command.ExpiresAt - command.IssuedAt > MaximumLifetime)
            return "command lifetime exceeds the maximum";
        if (command.ExpiresAt <= now)
            return "command has expired";

        return null;
    }
}
