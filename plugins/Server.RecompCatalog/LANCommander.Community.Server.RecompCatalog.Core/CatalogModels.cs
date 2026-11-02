using System.Text.Json.Serialization;

namespace LANCommander.Community.Server.RecompCatalog.Core;

public sealed record CatalogIndex
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("lists")]
    public IReadOnlyList<CatalogListReference> Lists { get; init; } = [];
}

public sealed record CatalogListReference
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("remoteLocation")]
    public string RemoteLocation { get; init; } = "";
}

public sealed record CatalogList
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("apps")]
    public IReadOnlyList<CatalogEntry> Apps { get; init; } = [];
}

public sealed record CatalogEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("repository")]
    public string Repository { get; init; } = "";

    [JsonPropertyName("repositorySource")]
    public string? RepositorySource { get; init; }

    [JsonPropertyName("folderName")]
    public string FolderName { get; init; } = "";

    [JsonPropertyName("appIconUrl")]
    public string? AppIconUrl { get; init; }

    [JsonPropertyName("releaseAssetFilter")]
    public string? ReleaseAssetFilter { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("filesToAdd")]
    public IReadOnlyList<string> FilesToAdd { get; init; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Project) ? Name : $"{Name} - {Project}";
    public bool IsGitHub => string.IsNullOrWhiteSpace(RepositorySource)
        || RepositorySource.Equals("github", StringComparison.OrdinalIgnoreCase);
}

public sealed record CatalogSource(
    string Id,
    Uri Location,
    string Name,
    string Description,
    string Version,
    IReadOnlyList<CatalogEntry> Entries);
