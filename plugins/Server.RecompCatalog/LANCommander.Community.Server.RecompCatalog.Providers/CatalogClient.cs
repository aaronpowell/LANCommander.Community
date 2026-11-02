using System.Text.Json;
using LANCommander.Community.Server.RecompCatalog.Core;

namespace LANCommander.Community.Server.RecompCatalog.Providers;

public sealed class CatalogClient(HttpClient httpClient) : ICatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<CatalogSource>> LoadAsync(
        Uri indexLocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indexLocation);
        var index = await GetAsync<CatalogIndex>(indexLocation, cancellationToken);

        if (index.Version < 1 || index.Lists.Count == 0)
            throw new InvalidDataException("The catalog index does not contain any lists.");

        var sources = new List<CatalogSource>();
        foreach (var reference in index.Lists)
        {
            if (!Uri.TryCreate(reference.RemoteLocation, UriKind.Absolute, out var location)
                || location.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Catalog list '{reference.Id}' has an invalid URL.");

            var list = await GetAsync<CatalogList>(location, cancellationToken);
            var entries = list.Apps
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                    && !string.IsNullOrWhiteSpace(entry.Repository))
                .ToList();

            sources.Add(new CatalogSource(
                reference.Id,
                location,
                list.Name,
                list.Description,
                list.Version,
                entries));
        }

        return sources;
    }

    private async Task<T> GetAsync<T>(Uri location, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(location, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"'{location}' returned an empty JSON document.");
    }
}
