[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    $dlls = @($zip.Entries | Where-Object FullName -like '*.dll' | ForEach-Object FullName | Sort-Object)
    $expected = @(
        'lib/net10.0/LANCommander.Community.Server.Quake3.Core.dll',
        'lib/net10.0/LANCommander.Community.Server.Quake3.dll'
    ) | Sort-Object
    if (($dlls -join '|') -cne ($expected -join '|')) {
        throw "Unexpected Quake package assembly inventory: $($dlls -join ', ')."
    }
    $nuspecs = @($zip.Entries | Where-Object FullName -like '*.nuspec')
    if ($nuspecs.Count -ne 1) { throw 'Quake package must contain one nuspec.' }
    $reader = [IO.StreamReader]::new($nuspecs[0].Open())
    try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($spec.NameTable)
    $ns.AddNamespace('n', $spec.DocumentElement.NamespaceURI)
    $metadata = $spec.SelectSingleNode('/n:package/n:metadata', $ns)
    if ($metadata.id -cne 'LANCommander.Community.Server.Quake3' -or
        $metadata.version -cne $ExpectedVersion) {
        throw 'Quake package identity/version mismatch.'
    }
    $types = @($spec.SelectNodes('//n:packageType', $ns) | ForEach-Object { $_.name } | Sort-Object)
    if (($types -join '|') -cne 'LANCommander.Plugin|LANCommander.Plugin.Server') {
        throw 'Quake package must declare both LC plugin package types.'
    }
    if ($spec.SelectNodes('//n:dependency', $ns).Count -ne 0) {
        throw 'Host-provided dependencies must not be declared as plugin runtime dependencies.'
    }
    if (-not $zip.GetEntry('README.md')) { throw 'Quake package README is missing.' }
}
finally { $zip.Dispose() }
$previousPackage = $env:QUAKE3_TEST_PACKAGE
try {
    $env:QUAKE3_TEST_PACKAGE = [IO.Path]::GetFullPath($PackagePath)
    & dotnet test (Join-Path $PSScriptRoot '../LANCommander.Community.Server.Quake3.Host.Tests/LANCommander.Community.Server.Quake3.Host.Tests.csproj') `
        -c Release --no-build --no-restore --nologo --filter 'FullyQualifiedName~PackageLoaderTests'
    if ($LASTEXITCODE -ne 0) { throw 'Packaged Quake loader contract check failed.' }
}
finally {
    $env:QUAKE3_TEST_PACKAGE = $previousPackage
}
Write-Output "Quake package metadata and private-only inventory verified: $ExpectedVersion"
