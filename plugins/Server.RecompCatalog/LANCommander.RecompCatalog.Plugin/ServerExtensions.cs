using System.Reflection;
using LANCommander.Server.Plugins;

namespace LANCommander.RecompCatalog.Plugin;

/// <summary>Exposes this assembly's routable Razor components to the server's router.</summary>
internal sealed record RecompCatalogRouteAssemblyExtension(Assembly Assembly) : IServerRouteAssemblyExtension;

/// <summary>Adds the plugin's page to the server's primary navigation.</summary>
internal sealed class RecompCatalogNavigationExtension : IServerNavigationExtension
{
    internal const string Route = "/Plugins/RecompCatalog";

    public string Id => RecompCatalogPlugin.PluginId;
    public string Label => "Recomp Catalog";
    public string Href => Route;
    public string? Icon => "Download";
    public int Order => 100;

    // Visibility only. The server independently enforces the page's own [PluginAccess] policy.
    public PluginAccessPolicy Access => PluginAccessPolicy.Administrator;
}
