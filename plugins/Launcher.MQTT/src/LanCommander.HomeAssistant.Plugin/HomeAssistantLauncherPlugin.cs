using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using LanCommander.HomeAssistant.Core;
using LanCommander.HomeAssistant.Mqtt;
using LANCommander.SDK.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LANCommander.Launcher.Plugins.Extensions;
using System.Reflection;

[assembly: LANCommanderPlugin(
    typeof(LanCommander.HomeAssistant.Plugin.HomeAssistantLauncherPlugin),
    Id = "com.aaronpowell.lancommander.homeassistant",
    Hosts = PluginHost.Launcher,
    MinHostVersion = "2.2.0-alpha.4",
    MaxHostVersion = "2.2.0-alpha.4")]

namespace LanCommander.HomeAssistant.Plugin;

public sealed class HomeAssistantLauncherPlugin : IPlugin
{
    public string Id => "com.aaronpowell.lancommander.homeassistant";
    public string Name => "LANCommander MQTT";
    public string Version => typeof(HomeAssistantLauncherPlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HomeAssistantLauncherPlugin).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
    public string Author => "Aaron Powell";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<HomeAssistantRuntime>();
        services.AddSingleton<ISettingsPageExtension, HomeAssistantSettingsExtension>();
    }

    public async Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        if (context.Host != PluginHost.Launcher
            || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            context.Logger.LogInformation("Home Assistant MQTT is inactive outside the GUI Launcher.");
            return;
        }

        var logger = context.Logger;
        var runtime = context.Services.GetRequiredService<HomeAssistantRuntime>();
        await runtime.InitializeAsync(context.PluginDirectory, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Home Assistant MQTT transport started; broker connection and Launcher readiness are reported separately.");

        desktop.Exit += async (_, _) =>
        {
            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not stop the Home Assistant MQTT transport.");
            }
        };
    }
}
