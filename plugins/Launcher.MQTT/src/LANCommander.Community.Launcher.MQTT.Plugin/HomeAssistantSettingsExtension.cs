using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LANCommander.Launcher.Plugins.Extensions;
using LANCommander.Community.Launcher.MQTT.Core;
using LANCommander.Community.Launcher.MQTT.Transport;
using Microsoft.Extensions.Logging;

namespace LANCommander.Community.Launcher.MQTT;

public sealed class HomeAssistantSettingsExtension(
    HomeAssistantRuntime runtime, ILogger<HomeAssistantSettingsExtension> logger) : ISettingsPageExtension
{
    public string Title => "Home Assistant MQTT";
    public int Order => 50;

    public Control BuildContent()
    {
        var settings = runtime.Settings;
        var panel = new StackPanel { Spacing = 8 };
        var status = new TextBlock { Text = runtime.Status, TextWrapping = TextWrapping.Wrap };
        var host = Field(panel, "MQTT broker hostname / IP", settings.BrokerHost);
        var port = Field(panel, "MQTT port (1883 plaintext / 8883 TLS; explicit)", settings.BrokerPort.ToString());
        var username = Field(panel, "Broker username", settings.BrokerUsername);
        var password = Field(panel, "Broker password (stored in local config)", settings.BrokerPassword);
        password.PasswordChar = '*';
        var tls = new CheckBox { Content = "Use TLS (certificate errors are ignored)", IsChecked = settings.UseTls };
        panel.Children.Add(tls);
        panel.Children.Add(new TextBlock
        {
            Text = "Plaintext exposes credentials/commands. Unverified TLS permits broker impersonation. Use trusted LAN access controls and broker ACLs.",
            TextWrapping = TextWrapping.Wrap
        });
        var clientId = Field(panel, "Stable MQTT client ID", settings.ClientId);
        var prefix = Field(panel, "MQTT topic prefix", settings.TopicPrefix);
        var all = new CheckBox { Content = "Expose all games in the signed-in user's visible library", IsChecked = settings.ExposureMode == "all" };
        panel.Children.Add(all);
        var search = Field(panel, "Search games by title or UUID (selection is preserved while filtering)", "");
        var refresh = new Button { Content = "Refresh library" };
        panel.Children.Add(refresh);
        var choices = new StackPanel { Spacing = 4 };
        panel.Children.Add(new ScrollViewer { Content = choices, MaxHeight = 300 });
        var selected = settings.Favorites.ToDictionary(game => game.Id, game => game.Title);
        IReadOnlyList<CatalogGame> library = [];

        void Render()
        {
            choices.Children.Clear();
            var filter = search.Text ?? "";
            foreach (var game in library.Where(game => game.Option.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                var box = new CheckBox
                {
                    Content = $"{game.Option} - {(game.IsInstalled ? "installed" : "not installed")}",
                    IsChecked = selected.ContainsKey(game.Id),
                    IsEnabled = all.IsChecked != true
                };
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true) selected[game.Id] = game.Title;
                    else selected.Remove(game.Id);
                };
                choices.Children.Add(box);
            }
        }

        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try
            {
                library = await runtime.Host.GetLibraryAsync(CancellationToken.None);
                Render();
                status.Text = $"Loaded {library.Count} accessible games. {runtime.Status}";
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not refresh Home Assistant settings library.");
                status.Text = $"Refresh failed ({exception.GetType().Name}). Check Launcher logs.";
            }
            finally { refresh.IsEnabled = true; }
        };
        search.TextChanged += (_, _) => Render();
        all.IsCheckedChanged += (_, _) => Render();
        var save = new Button { Content = "Save and apply", HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                if (!int.TryParse(port.Text, out var portNumber))
                    throw new InvalidOperationException("Port must be a number.");
                var updated = new HomeAssistantPluginSettings(host.Text ?? "", portNumber,
                    clientId.Text ?? "", prefix.Text ?? "",
                    selected.Select(game => new FavoriteGame(game.Key, game.Value)).ToArray(),
                    username.Text ?? "", password.Text ?? "", tls.IsChecked == true,
                    all.IsChecked == true ? "all" : "selected");
                await runtime.ApplyAsync(updated, CancellationToken.None);
                status.Text = runtime.Status;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not save Home Assistant settings.");
                status.Text = exception is InvalidOperationException ? exception.Message
                    : $"Save failed ({exception.GetType().Name}). Check Launcher logs.";
            }
            finally { save.IsEnabled = true; }
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Select only changes the HA target. Install selected queues a normal install; Run selected requires installation first. Configure exactly one install directory in Launcher settings. Save/apply is blocked during remote operations. First eight chosen games keep legacy launch buttons.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(save);
        panel.Children.Add(status);
        return panel;
    }

    private static TextBox Field(StackPanel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label });
        var field = new TextBox { Text = value, MinWidth = 250, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(field);
        return field;
    }
}
