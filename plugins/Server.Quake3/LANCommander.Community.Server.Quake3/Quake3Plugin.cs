using System.Reflection;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: LANCommanderPlugin(typeof(LANCommander.Community.Server.Quake3.Quake3Plugin),
    Id = LANCommander.Community.Server.Quake3.Quake3Plugin.PluginId,
    MinHostVersion = "2.2.0-beta.4", Hosts = PluginHost.Server)]

namespace LANCommander.Community.Server.Quake3;

public sealed class Quake3Plugin : IPlugin
{
    public const string PluginId = "io.github.aaronpowell.lancommander.quake3";
    public string Id => PluginId;
    public string Name => "Quake 3 Server Manager";
    public string Author => "LANCommander Community contributors";
    public string Version => typeof(Quake3Plugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServerRouteAssemblyExtension>(
            new Quake3Routes(typeof(Quake3Plugin).Assembly));
        services.AddSingleton<IServerNavigationExtension, Quake3Navigation>();
        services.AddSingleton<Quake3Runtime>();
        services.AddSingleton<Quake3Settings>();
        services.AddScoped<Quake3Authorization>();
        services.AddScoped<Quake3Administration>();
    }

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.Services.GetRequiredService<Quake3Runtime>().Initialize(context.DataDirectory);
        context.Logger.LogInformation("Quake 3 Server Manager initialized using host plugin data storage");
        return Task.CompletedTask;
    }
}

internal sealed record Quake3Routes(Assembly Assembly) : IServerRouteAssemblyExtension;

internal sealed class Quake3Navigation : IServerNavigationExtension
{
    public string Id => Quake3Plugin.PluginId;
    public string Label => "Quake 3 Server Manager";
    public string Href => "/Plugins/Quake3";
    public string? Icon => "GameController";
    public int Order => 110;
    public PluginAccessPolicy Access => PluginAccessPolicy.Administrator;
}
