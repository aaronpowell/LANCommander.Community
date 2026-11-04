[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $PackagePath
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $root = 'Plugins/LANCommander.Community.Launcher.MQTT.Plugin/'
    $required = @(
        "$($root)LANCommander.Community.Launcher.MQTT.Plugin.dll",
        "$($root)LANCommander.Community.Launcher.MQTT.Plugin.deps.json",
        "$($root)LANCommander.Community.Launcher.MQTT.Core.dll",
        "$($root)LANCommander.Community.Launcher.MQTT.Transport.dll",
        "$($root)MQTTnet.dll",
        "$($root)homeassistant.json",
        'package-manifest.json',
        'TEST-GUIDE.md',
        'home-assistant-launch-game.yaml',
        'MQTTnet-LICENSE.txt'
    )
    $files = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object FullName | Sort-Object)
    if (Compare-Object @($required | Sort-Object) $files) {
        throw "Unexpected launcher ZIP inventory. Expected only: $($required -join ', '). Found: $($files -join ', ')."
    }

    $manifestEntry = $archive.GetEntry('package-manifest.json')
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.pluginVersion -cne $ExpectedVersion) {
        throw "ZIP manifest version '$($manifest.pluginVersion)' does not match '$ExpectedVersion'."
    }
    $pinPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'eng/lancommander-source.json'
    $pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json
    if ($manifest.hostCommit -cne $pin.commit -or $manifest.hostTag -cne $pin.tag) {
        throw 'ZIP host source metadata does not match the repository pin.'
    }

    $configEntry = $archive.GetEntry("$($root)homeassistant.json")
    $reader = [System.IO.StreamReader]::new($configEntry.Open())
    try { $config = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($config.BrokerUsername -or $config.BrokerPassword) {
        throw 'The packaged example configuration must not contain broker credentials.'
    }
    if ($manifest.files.Count -ne 6) {
        throw 'ZIP manifest must inventory the six files in the plugin drop-in directory.'
    }
    Write-Output "Verified Launcher.MQTT ZIP: $($package.Name)"
}
finally {
    $archive.Dispose()
}
