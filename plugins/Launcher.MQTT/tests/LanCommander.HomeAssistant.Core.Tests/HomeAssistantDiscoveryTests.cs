using System.Text.Json;
using LanCommander.HomeAssistant.Mqtt;
using LanCommander.HomeAssistant.Core;
using Xunit;

namespace LanCommander.HomeAssistant.Core.Tests;

public sealed class HomeAssistantDiscoveryTests
{
    [Fact]
    public void Discovery_creates_one_device_with_safe_favorite_buttons_and_status_entities()
    {
        var clientId = "lc_livingroom_1234";
        var gameId = Guid.NewGuid();
        var publications = HomeAssistantDiscovery.Create(
            clientId,
            "lancommander/livingroom",
            [new FavoriteGame(gameId, "A game")]);

        Assert.Equal(5, publications.Count);
        Assert.Contains(publications, publication =>
            publication.Topic == $"homeassistant/button/{clientId}/launch_{gameId:N}/config");

        var button = JsonDocument.Parse(publications.Single(publication => publication.Topic.Contains("/button/")).Payload);
        var root = button.RootElement;
        Assert.Equal($"lancommander/livingroom/command/{gameId:D}", root.GetProperty("command_topic").GetString());
        Assert.False(root.GetProperty("retain").GetBoolean());
        var commandTemplate = root.GetProperty("command_template").GetString()!;
        Assert.DoesNotContain("uuid(", commandTemplate);
        Assert.Contains(HomeAssistantDiscovery.RequestIdExpression, commandTemplate);
        Assert.Contains("{% set issued = now() %}", commandTemplate);
        Assert.Contains("(issued + timedelta(seconds=30)).isoformat()", commandTemplate);
        Assert.Contains(gameId.ToString(), root.GetProperty("command_template").GetString());

        var entities = publications.Select(publication => JsonDocument.Parse(publication.Payload).RootElement.Clone()).ToList();
        Assert.Equal(5, entities.Select(entity => entity.GetProperty("unique_id").GetString()).Distinct().Count());
        Assert.All(entities, entity =>
        {
            Assert.Equal("lancommander/livingroom/availability", entity.GetProperty("availability_topic").GetString());
            Assert.Equal(clientId, entity.GetProperty("device").GetProperty("identifiers")[0].GetString());
            Assert.True(entity.TryGetProperty("origin", out _));
        });
    }

    [Fact]
    public void Large_catalog_uses_one_nonoptimistic_select_and_separate_bounded_action_buttons()
    {
        var catalog = Enumerable.Range(0, 2000).Select(index =>
            new CatalogGame(Guid.NewGuid(), index % 2 == 0 ? "Duplicate title" : $"Game {index}", index % 3 == 0)).ToArray();
        var publications = HomeAssistantDiscovery.Create("lc_large", "test/large", [], catalog);
        Assert.Equal(8, publications.Count);
        var select = JsonDocument.Parse(publications.Single(item => item.Topic.Contains("/select/")).Payload).RootElement;
        var options = select.GetProperty("options").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(2000, options.Length);
        Assert.Equal(2000, options.Distinct().Count());
        Assert.False(select.GetProperty("optimistic").GetBoolean());
        Assert.False(select.GetProperty("retain").GetBoolean());
        Assert.Equal("test/large/selection", select.GetProperty("state_topic").GetString());
        Assert.Equal(2, publications.Count(item => item.Topic.Contains("/button/")));
        Assert.Contains(publications, item => item.Topic.EndsWith("/install_selected/config"));
        Assert.Contains(publications, item => item.Topic.EndsWith("/launch_selected/config"));
    }

    [Fact]
    public void Mqtt_settings_reject_invalid_identity_and_topic_wildcards()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new MqttConnectionSettings("broker", 1883, "bad/id", "lancommander/livingroom").Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new MqttConnectionSettings("broker", 1883, "lc_valid_123", "lancommander/+").Validate());
    }
}
