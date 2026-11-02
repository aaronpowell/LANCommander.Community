using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using LanCommander.HomeAssistant.Core;

namespace LanCommander.HomeAssistant.Mqtt;

public sealed record FavoriteGame(Guid Id, string Title);

public sealed record DiscoveryPublication(string Topic, byte[] Payload);

public static class HomeAssistantDiscovery
{
    public const string RequestIdExpression =
        "'%04x%04x-%04x-4%03x-%04x-%04x%04x%04x' | format(" +
        "range(65536) | random, range(65536) | random, range(65536) | random, " +
        "range(4096) | random, range(32768, 49152) | random, " +
        "range(65536) | random, range(65536) | random, range(65536) | random)";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<DiscoveryPublication> Create(
        string clientId,
        string topicPrefix,
        IReadOnlyCollection<FavoriteGame> favorites,
        IReadOnlyList<CatalogGame>? catalog = null)
    {
        var device = new
        {
            identifiers = new[] { clientId },
            name = "LANCommander Launcher",
            manufacturer = "LANCommander",
            model = "Home Assistant MQTT plugin",
            sw_version = PluginVersion
        };
        var origin = new
        {
            name = "LANCommander Home Assistant plugin",
            sw = PluginVersion,
            url = "https://github.com/aaronpowell/LANCommander.Community/tree/main/plugins/Launcher.MQTT"
        };
        var availabilityTopic = $"{topicPrefix}/availability";
        var stateTopic = $"{topicPrefix}/state";
        var publications = new List<DiscoveryPublication>();

        foreach (var game in favorites.OrderBy(favorite => favorite.Title, StringComparer.Ordinal))
        {
            var objectId = $"launch_{game.Id:N}";
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = $"Launch {game.Title}",
                unique_id = $"{clientId}_{objectId}",
                command_topic = $"{topicPrefix}/command/{game.Id:D}",
                command_template =
                    "{% set issued = now() %}" +
                    $"{{{{ {{'requestId': ({RequestIdExpression}), 'action': 'launch', 'gameId': '{game.Id:D}', 'issuedAt': issued.isoformat(), 'expiresAt': (issued + timedelta(seconds=30)).isoformat()}} | to_json }}}}",
                availability_topic = availabilityTopic,
                payload_available = "online",
                payload_not_available = "offline",
                qos = 1,
                retain = false,
                device,
                origin
            }, JsonOptions);

            publications.Add(new DiscoveryPublication(
                $"homeassistant/button/{clientId}/{objectId}/config",
                payload));
        }

        AddEntity(publications, clientId, "binary_sensor", "ready", "Launcher ready",
            stateTopic, availabilityTopic, "{{ 'ON' if value_json.ready else 'OFF' }}", device, origin);
        AddEntity(publications, clientId, "sensor", "launch_status", "Launch status",
            stateTopic, availabilityTopic, "{{ value_json.status }}", device, origin);
        AddEntity(publications, clientId, "sensor", "current_game", "Current game",
            stateTopic, availabilityTopic, "{{ value_json.gameTitle or 'None' }}", device, origin);
        AddEntity(publications, clientId, "sensor", "launch_outcome", "Launch outcome",
            $"{topicPrefix}/result", availabilityTopic, "{{ value_json.disposition }}", device, origin,
            "{{ value_json | tojson }}");

        if (catalog is not null)
        {
            publications.Add(new DiscoveryPublication($"homeassistant/select/{clientId}/game/config",
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    name = "Game",
                    unique_id = $"{clientId}_game",
                    command_topic = $"{topicPrefix}/command/select",
                    command_template = CommandTemplate("select", "value[-37:-1]"),
                    state_topic = $"{topicPrefix}/selection",
                    options = catalog.Select(game => game.Option).ToArray(),
                    optimistic = false,
                    qos = 1,
                    retain = false,
                    availability_topic = availabilityTopic,
                    device, origin
                }, JsonOptions)));
            foreach (var action in new[] { "launch_selected", "install_selected" })
            {
                publications.Add(new DiscoveryPublication($"homeassistant/button/{clientId}/{action}/config",
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        name = action == "launch_selected" ? "Run selected" : "Install selected",
                        unique_id = $"{clientId}_{action}",
                        command_topic = $"{topicPrefix}/command/{action}",
                        command_template = CommandTemplate(action, $"'{Guid.Empty:D}'"),
                        qos = 1, retain = false, availability_topic = availabilityTopic,
                        device, origin
                    }, JsonOptions)));
            }
            AddEntity(publications, clientId, "sensor", "installation", "Selected game installation",
                stateTopic, availabilityTopic, "{{ value_json.installStatus }}", device, origin,
                "{{ {'gameId': value_json.selectedGameId, 'progress': value_json.installProgress, 'error': value_json.errorType} | to_json }}");
        }

        return publications;
    }

    private static string CommandTemplate(string action, string gameExpression) =>
        "{% set issued = now() %}" +
        $"{{{{ {{'requestId': ({RequestIdExpression}), 'action': '{action}', 'gameId': {gameExpression}, 'issuedAt': issued.isoformat(), 'expiresAt': (issued + timedelta(seconds=30)).isoformat()}} | to_json }}}}";

    private static string PluginVersion =>
        typeof(HomeAssistantDiscovery).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HomeAssistantDiscovery).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static void AddEntity(
        ICollection<DiscoveryPublication> publications,
        string clientId,
        string component,
        string objectId,
        string name,
        string stateTopic,
        string availabilityTopic,
        string valueTemplate,
        object device,
        object origin,
        string? attributesTemplate = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name,
            unique_id = $"{clientId}_{objectId}",
            state_topic = stateTopic,
            value_template = valueTemplate,
            json_attributes_topic = attributesTemplate is null ? null : stateTopic,
            json_attributes_template = attributesTemplate,
            availability_topic = availabilityTopic,
            payload_available = "online",
            payload_not_available = "offline",
            device,
            origin
        }, JsonOptions);

        publications.Add(new DiscoveryPublication(
            $"homeassistant/{component}/{clientId}/{objectId}/config",
            payload));
    }
}
