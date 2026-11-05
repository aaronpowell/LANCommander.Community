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
    if ($Plugin -eq 'Launcher.MQTT' -and $testProject -match 'HostLoader\.Tests') {
        continue # The host tests run against the extracted nupkg after packaging.
    }
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
    Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
    $version = Get-PluginVersion $component
    foreach ($publisher in @('Publish-GitHubPackage.ps1', 'Publish-NuGetPackage.ps1')) {
        $arguments = @{
            Plugin = $Plugin
            Tag = "launcher-mqtt-v$version"
            ArtifactDirectory = $packageOutput
            Publish = $true
            PlanOnly = $true
        }
        if ($publisher -eq 'Publish-GitHubPackage.ps1') {
            $arguments.Owner = 'aaronpowell'
            $arguments.Repository = 'aaronpowell/LANCommander.Community'
        }
        $output = & (Join-Path $PSScriptRoot $publisher) @arguments
        $plans = @($output | Where-Object { $_ -match '^\{"component"' })
        if ($plans.Count -ne 1) { throw "$publisher did not return exactly one publication plan." }
        $plan = $plans[0] | ConvertFrom-Json
        if ($plan.component -cne $Plugin -or $plan.version -cne $version -or
            $plan.packageId -cne $component.packageId -or $plan.publishRequested -ne $true) {
            throw "$publisher did not select the exact launcher NuGet artifact."
        }
    }
    if (-not (Test-Path -LiteralPath $env:LANCOMMANDER_HA_TEMPLATE_EXPORT)) {
        throw 'Launcher host-loader tests did not export the packaged discovery templates.'
    }
    & python (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/tools/Test-HaTemplates.py') `
        $env:LANCOMMANDER_HA_TEMPLATE_EXPORT `
        --script (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/examples/home-assistant-launch-game.yaml')
    if ($LASTEXITCODE -ne 0) { throw 'Home Assistant template tests failed.' }
}

Write-Output "Validation passed for $Plugin."
