[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $PackagePath
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $packageId = 'LANCommander.RecompCatalog.Plugin'
    $expected = @(
        'LANCommander.RecompCatalog.Core.dll',
        'LANCommander.RecompCatalog.Infrastructure.dll',
        'LANCommander.RecompCatalog.Plugin.dll',
        'LANCommander.RecompCatalog.Providers.dll'
    ) | ForEach-Object { "lib/net10.0/$_" }
    $actual = @($archive.Entries | Where-Object { $_.FullName.StartsWith('lib/') -and $_.Name } |
        ForEach-Object FullName | Sort-Object)
    if (Compare-Object @($expected | Sort-Object) $actual) {
        throw "Unexpected Recomp Catalog payload. Expected only: $($expected -join ', '). Found: $($actual -join ', ')."
    }

    $nuspecEntry = $archive.GetEntry("$packageId.nuspec")
    if (-not $nuspecEntry) { throw "Missing $packageId.nuspec." }
    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $nuspec.package.metadata
    if ($metadata.id -cne $packageId -or $metadata.version -cne $ExpectedVersion) {
        throw "Package identity/version mismatch: $($metadata.id) $($metadata.version); expected $packageId $ExpectedVersion."
    }
    $types = @($metadata.packageTypes.packageType | ForEach-Object name)
    if ('LANCommander.Plugin' -notin $types -or 'LANCommander.Plugin.Server' -notin $types) {
        throw 'Package must declare both LANCommander.Plugin and LANCommander.Plugin.Server package types.'
    }
    if (@($metadata.SelectNodes('.//dependency')).Count -gt 0) {
        throw 'Plugin package must not declare NuGet dependencies; LANCommander does not install them.'
    }
    foreach ($required in @('LICENSE', 'README.md')) {
        if (-not $archive.GetEntry($required)) { throw "Package is missing $required." }
    }
    Write-Output "Verified Recomp Catalog package: $($package.Name)"
}
finally {
    $archive.Dispose()
}
