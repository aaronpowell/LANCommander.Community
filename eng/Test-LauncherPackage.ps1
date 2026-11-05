[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Test-NuGetPackage.ps1') -Plugin Launcher.MQTT -PackagePath $PackagePath -ExpectedVersion $ExpectedVersion -RequireRepositoryUrl
$root = 'lib/net10.0/'
$required = @(
    "${root}LANCommander.Community.Launcher.MQTT.Plugin.dll",
    "${root}LANCommander.Community.Launcher.MQTT.Plugin.deps.json",
    "${root}LANCommander.Community.Launcher.MQTT.Core.dll",
    "${root}LANCommander.Community.Launcher.MQTT.Transport.dll",
    "${root}MQTTnet.dll",
    "${root}homeassistant.example.json"
)
$archive = [IO.Compression.ZipFile]::OpenRead((Get-Item -LiteralPath $PackagePath).FullName)
$temporary = Join-Path ([IO.Path]::GetTempPath()) "launcher-package-$([Guid]::NewGuid().ToString('N'))"
$previousPlugin = $env:LANCOMMANDER_MQTT_PLUGIN_PATH
$previousPackage = $env:LANCOMMANDER_MQTT_PACKAGE_PATH
$previousTestRoot = $env:LANCOMMANDER_MQTT_PACKAGE_TEST_ROOT
$previousVersion = $env:LANCOMMANDER_MQTT_PACKAGE_VERSION
try {
    $reader = [IO.StreamReader]::new(@($archive.Entries | Where-Object FullName -Like '*.nuspec')[0].Open())
    try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($nuspec.SelectNodes("//*[local-name()='dependency']").Count -gt 0) {
        throw 'Launcher packages must bundle private dependencies; the host installer does not restore NuGet dependencies.'
    }
    $files = @($archive.Entries | Where-Object { $_.FullName.StartsWith('lib/') -and $_.Name } | ForEach-Object FullName)
    if (Compare-Object @($required | Sort-Object) @($files | Sort-Object)) {
        throw "Unexpected launcher NuGet payload: $($files -join ', ')."
    }
    foreach ($name in @('README.md', 'MQTTnet-LICENSE.txt', 'examples/TEST-GUIDE.md', 'examples/home-assistant-launch-game.yaml')) {
        if (-not $archive.GetEntry($name)) { throw "Missing launcher package documentation/license: $name" }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry("${root}homeassistant.example.json").Open())
    try { $example = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($example.BrokerUsername -or $example.BrokerPassword) { throw 'Launcher package contains populated credentials.' }
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $pluginDirectory = Join-Path $temporary 'LANCommander.Community.Launcher.MQTT.Plugin'
    New-Item -ItemType Directory -Path $pluginDirectory | Out-Null
    foreach ($name in $required) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($name), (Join-Path $pluginDirectory ($name.Substring($root.Length))))
    }
    $env:LANCOMMANDER_MQTT_PLUGIN_PATH = Join-Path $pluginDirectory 'LANCommander.Community.Launcher.MQTT.Plugin.dll'
    $env:LANCOMMANDER_MQTT_PACKAGE_PATH = (Get-Item -LiteralPath $PackagePath).FullName
    $env:LANCOMMANDER_MQTT_PACKAGE_TEST_ROOT = $temporary
    $env:LANCOMMANDER_MQTT_PACKAGE_VERSION = $ExpectedVersion
    & dotnet test (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/tests/LANCommander.Community.Launcher.MQTT.HostLoader.Tests/LANCommander.Community.Launcher.MQTT.HostLoader.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Launcher NuGet host installation/loader tests failed.' }
}
finally {
    $archive.Dispose()
    $env:LANCOMMANDER_MQTT_PLUGIN_PATH = $previousPlugin
    $env:LANCOMMANDER_MQTT_PACKAGE_PATH = $previousPackage
    $env:LANCOMMANDER_MQTT_PACKAGE_TEST_ROOT = $previousTestRoot
    $env:LANCOMMANDER_MQTT_PACKAGE_VERSION = $previousVersion
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
Write-Output "Verified Launcher.MQTT NuGet payload, installer and loader: $PackagePath"
