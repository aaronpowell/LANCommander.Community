[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Server.Mcp', 'Server.RecompCatalog', 'Launcher.MQTT', 'Server.Quake3')]
    [string] $Plugin,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$component = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugins.json') -Raw |
    ConvertFrom-Json | Select-Object -ExpandProperty plugins | Where-Object key -CEQ $Plugin)[0]
if (-not $component) { throw "Plugin '$Plugin' is missing from the release registry." }
$testProjects = @($component.tests | ForEach-Object { Join-Path $repositoryRoot $_ })
$packageOutput = if ($OutputDirectory) { $OutputDirectory } else {
    Join-Path $repositoryRoot "artifacts/validation-$($component.tagPrefix)"
}

if ($Plugin -eq 'Launcher.MQTT') {
    $hostSource = Join-Path $repositoryRoot '.cache/lancommander-beta3'
    & (Join-Path $PSScriptRoot 'Prepare-LANCommanderSource.ps1') -Destination $hostSource | Out-Null
    $env:LANCOMMANDER_SOURCE_ROOT = $hostSource
    $env:LANCOMMANDER_MQTT_PLUGIN_PATH = Join-Path $repositoryRoot 'plugins/Launcher.MQTT/src/LANCommander.Community.Launcher.MQTT.Plugin/bin/Release/net10.0/plugin-package/LANCommander.Community.Launcher.MQTT.Plugin.dll'
    $env:LANCOMMANDER_HA_TEMPLATE_EXPORT = Join-Path ([IO.Path]::GetTempPath()) "lancommander-ha-discovery-$PID.json"
}

foreach ($testProject in $testProjects) {
    & dotnet test $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed for ${Plugin}: $testProject" }
}

if ($Plugin -eq 'Server.Mcp') {
    Push-Location (Join-Path $repositoryRoot 'plugins/Server.Mcp')
    try {
        & node --test tests/setup.test.mjs
        if ($LASTEXITCODE -ne 0) { throw 'MCP Node tests failed.' }
    }
    finally { Pop-Location }
}

if (Test-Path -LiteralPath $packageOutput) {
    throw "Output directory already exists: $packageOutput. Use a fresh path to prevent overwriting artifacts."
}
& (Join-Path $PSScriptRoot 'Build-Plugin.ps1') -Plugin $Plugin -Package -OutputDirectory $packageOutput
if ($LASTEXITCODE -ne 0) { throw "Package validation failed for $Plugin." }

if ($Plugin -eq 'Launcher.MQTT') {
    if (-not (Test-Path -LiteralPath $env:LANCOMMANDER_HA_TEMPLATE_EXPORT)) {
        throw 'Launcher host-loader tests did not export the packaged discovery templates.'
    }
    & python (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/tools/Test-HaTemplates.py') `
        $env:LANCOMMANDER_HA_TEMPLATE_EXPORT `
        --script (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/examples/home-assistant-launch-game.yaml')
    if ($LASTEXITCODE -ne 0) { throw 'Home Assistant template tests failed.' }
}

Write-Output "Validation passed for $Plugin."
