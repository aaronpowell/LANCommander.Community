param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [Parameter(Mandatory)]
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root 'src/LANCommander.Community.Launcher.MQTT.Plugin/bin/Release/net10.0/plugin-package'
$required = @(
    'LANCommander.Community.Launcher.MQTT.Plugin.dll',
    'LANCommander.Community.Launcher.MQTT.Plugin.deps.json',
    'LANCommander.Community.Launcher.MQTT.Core.dll',
    'LANCommander.Community.Launcher.MQTT.Transport.dll',
    'MQTTnet.dll',
    'homeassistant.json'
)
$actual = @(Get-ChildItem $package -File | Select-Object -ExpandProperty Name)
if (Compare-Object ($required | Sort-Object) ($actual | Sort-Object)) {
    throw 'The staged package does not match the expected inventory. Build and validate Release first.'
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
$version = [IO.File]::ReadAllText((Join-Path $root 'version.txt')).Trim()
if (-not $version -or $version -cne $ExpectedVersion) {
    throw "Launcher version '$version' does not match the selected release version '$ExpectedVersion'."
}
$staging = Join-Path $output "launcher-mqtt-$version"
$zip = "$staging.zip"
if ((Test-Path $staging) -or (Test-Path $zip)) {
    throw "Output already exists at '$staging' or '$zip'; choose a fresh output directory."
}

$plugin = Join-Path $staging 'Plugins/LANCommander.Community.Launcher.MQTT.Plugin'
New-Item $plugin -ItemType Directory -Force | Out-Null
foreach ($file in $required) {
    if ($file -eq 'homeassistant.json') {
        Copy-Item (Join-Path $root 'src/LANCommander.Community.Launcher.MQTT.Plugin/homeassistant.example.json') (Join-Path $plugin $file)
    }
    else {
        Copy-Item (Join-Path $package $file) $plugin
    }
}
Copy-Item (Join-Path $root 'examples/TEST-GUIDE.md') $staging
Copy-Item (Join-Path $root 'examples/home-assistant-launch-game.yaml') $staging
Copy-Item (Join-Path $root 'examples/MQTTnet-LICENSE.txt') $staging

$inventory = Get-ChildItem $plugin -File -Recurse | ForEach-Object {
    [ordered]@{
        path = ([IO.Path]::GetRelativePath($staging, $_.FullName) -replace '\\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest = [ordered]@{
    pluginVersion = $version
    hostTag = 'v2.2.0-alpha.4'
    hostCommit = '4c8c73e47da6d4198c6153cc93ca7bb480d30abe'
    platform = 'Windows; .NET 10; GUI Launcher'
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    limitations = 'Prior HA-to-LC launch PoC confirmed by user. New native settings UI, HA select entities and real queue installation require user-machine testing; see TEST-GUIDE.md.'
    files = @($inventory)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $staging 'package-manifest.json') -Encoding utf8
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Get-Item $zip | Select-Object FullName, Length
Get-FileHash $zip -Algorithm SHA256 | Select-Object Hash
