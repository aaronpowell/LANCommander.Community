[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Server.Mcp', 'Server.RecompCatalog')]
    [string] $Plugin,
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion,
    [switch] $RequireRepositoryUrl
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
$component = @(Get-PluginRegistry | Where-Object key -CEQ $Plugin)[0]
if ($component.artifactFormat -cne 'nupkg') {
    throw "$Plugin does not produce a NuGet package."
}

$package = Get-Item -LiteralPath $PackagePath
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $nuspecs = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
    if ($nuspecs.Count -ne 1) {
        throw "Expected one root nuspec in $($package.Name); found $($nuspecs.Count)."
    }

    $reader = [System.IO.StreamReader]::new($nuspecs[0].Open())
    try { $nuspec = [System.Xml.Linq.XDocument]::Load($reader) } finally { $reader.Dispose() }
    $metadata = @($nuspec.Root.Elements() | Where-Object { $_.Name.LocalName -eq 'metadata' })[0]
    if (-not $metadata) { throw "Missing nuspec metadata in $($package.Name)." }

    $metadataFields = @{}
    foreach ($element in $metadata.Elements()) {
        $metadataFields[$element.Name.LocalName] = $element
    }
    $packageId = [string]$metadataFields['id'].Value
    $version = [string]$metadataFields['version'].Value
    if ($packageId -cne $component.packageId -or $version -cne $ExpectedVersion) {
        throw "Package identity/version mismatch: $packageId $version; expected $($component.packageId) $ExpectedVersion."
    }

    $typesElement = $metadataFields['packageTypes']
    $types = if ($typesElement) {
        @($typesElement.Elements() | Where-Object { $_.Name.LocalName -eq 'packageType' } |
            ForEach-Object { [string]$_.Attribute('name').Value })
    }
    else { @() }
    $expectedTypes = @($component.packageTypes)
    if (@(Compare-Object $expectedTypes $types).Count -gt 0) {
        throw "Package types in $($package.Name) do not match the release registry."
    }

    $tags = if ($metadataFields['tags']) {
        @(([string]$metadataFields['tags'].Value).Split([char[]]@(' ', "`t", "`r", "`n", ';'), [StringSplitOptions]::RemoveEmptyEntries))
    }
    else { @() }
    foreach ($requiredTag in @($component.packageTags)) {
        if ($requiredTag -notin $tags) {
            throw "Package $($package.Name) is missing required search tag '$requiredTag'."
        }
    }

    if ($RequireRepositoryUrl) {
        $repositoryElement = $metadataFields['repository']
        $repositoryUrl = if ($repositoryElement) { $repositoryElement.Attribute('url') }
        if (-not $repositoryUrl -or
            [string]$repositoryUrl.Value -cne 'https://github.com/aaronpowell/LANCommander.Community') {
            throw "Package $($package.Name) must identify the canonical LANCommander.Community repository."
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Output "Verified NuGet discovery metadata for $Plugin $ExpectedVersion."
