using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using LANCommander.Server.UI.Controls;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Host.Tests;

public sealed class PackageLoaderTests
{
    [Fact]
    public void LoadPluginWithSharedHostTypeIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"quake-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var context = new PluginContextLoader(root);
        try
        {
            var package = Environment.GetEnvironmentVariable("QUAKE3_TEST_PACKAGE");
            foreach (var name in new[] { "LANCommander.Community.Server.Quake3", "LANCommander.Community.Server.Quake3.Core" })
            {
                var destination = Path.Combine(root, $"{name}.dll");
                if (package is null)
                    File.Copy(Path.Combine(Path.GetDirectoryName(typeof(Quake3Plugin).Assembly.Location)!, $"{name}.dll"), destination);
                else
                {
                    using var zip = ZipFile.OpenRead(package);
                    var entry = zip.GetEntry($"lib/net10.0/{name}.dll")
                        ?? throw new InvalidDataException($"Missing private assembly {name}.");
                    using var input = entry.Open();
                    using var output = File.Create(destination);
                    input.CopyTo(output);
                }
            }
            using var pluginStream = File.OpenRead(Path.Combine(root, "LANCommander.Community.Server.Quake3.dll"));
            var assembly = context.LoadFromStream(pluginStream);
            var attribute = assembly.GetCustomAttribute<LANCommanderPluginAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal("2.2.0-beta.3", attribute.MinHostVersion);
            Assert.Equal(PluginHost.Server, attribute.Hosts);
            var pluginType = Assert.Single(assembly.GetTypes(), t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
            var plugin = Assert.IsAssignableFrom<IPlugin>(Activator.CreateInstance(pluginType));
            Assert.Equal(Quake3Plugin.PluginId, plugin.Id);
            Assert.Equal("Quake 3 Server Manager", plugin.Name);
            var page = Assert.Single(assembly.GetTypes(), t => typeof(ComponentBase).IsAssignableFrom(t)
                && t.GetCustomAttribute<RouteAttribute>()?.Template == "/Plugins/Quake3");
            Assert.Equal(PluginAccessPolicy.Administrator, page.GetCustomAttribute<PluginAccessAttribute>()!.Policy);

            var services = new ServiceCollection();
            plugin.ConfigureServices(services);
            using var provider = services.BuildServiceProvider();
            var navigation = Assert.Single(provider.GetServices<IServerNavigationExtension>());
            Assert.Equal("Quake 3 Server Manager", navigation.Label);
            Assert.Equal("/Plugins/Quake3", navigation.Href);
            Assert.Equal(nameof(IconType.GameController), navigation.Icon);
        }
        finally
        {
            context.Unload();
            foreach (var name in new[] { "LANCommander.Community.Server.Quake3.dll", "LANCommander.Community.Server.Quake3.Core.dll" })
                File.Delete(Path.Combine(root, name));
            Directory.Delete(root);
        }
    }

    private sealed class PluginContextLoader(string root) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null) return null;
            if (name.Name.StartsWith("LANCommander.Community.Server.Quake3", StringComparison.Ordinal))
            {
                using var stream = File.OpenRead(Path.Combine(root, $"{name.Name}.dll"));
                return LoadFromStream(stream);
            }
            return Default.LoadFromAssemblyName(name);
        }
    }
}
