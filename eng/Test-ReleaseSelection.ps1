$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force

function Assert-Equal {
    param($Expected, $Actual, [string] $Name)
    if (($Expected | ConvertTo-Json -Compress -Depth 5) -cne ($Actual | ConvertTo-Json -Compress -Depth 5)) {
        throw "$Name failed. Expected '$Expected'; got '$Actual'."
    }
}

function Assert-Fails {
    param([scriptblock] $Action, [string] $Name)
    try { & $Action } catch { return }
    throw "$Name unexpectedly succeeded."
}

$single = @(Resolve-PluginSelection -Plugins @('Server.Mcp'))
Assert-Equal 'Server.Mcp' $single[0].key 'Single-component selection'
Assert-Equal '0.2.1' $single[0].version 'Single-component version'

$tag = @(Resolve-PluginSelection -Tag 'launcher-mqtt-v0.2.0')
Assert-Equal 'Launcher.MQTT' $tag[0].key 'Component-tag selection'
Assert-Equal '0.2.0' $tag[0].version 'Component-tag SemVer'

$quake = @(Resolve-PluginSelection -Tag 'server-quake3-v0.1.0-alpha.1')
Assert-Equal 'Server.Quake3' $quake[0].key 'Quake component-tag selection'
Assert-Equal '0.1.0-alpha.1' $quake[0].version 'Quake independent prerelease'
Assert-Equal 4 @(Get-PluginRegistry).Count 'Complete component registry'

$batch = @(Resolve-PluginSelection -Plugins @('Server.Mcp', 'Server.RecompCatalog', 'Launcher.MQTT'))
Assert-Equal @('Server.Mcp', 'Server.RecompCatalog', 'Launcher.MQTT') @($batch | ForEach-Object key) 'Coordinated LC batch'
Assert-Equal @('0.2.1', '0.4.2', '0.2.0') @($batch | ForEach-Object version) 'Independent batch versions'

$all = @(Resolve-PluginSelection -Plugins @('Server.Mcp', 'Server.RecompCatalog', 'Launcher.MQTT', 'Server.Quake3'))
Assert-Equal @('0.2.1', '0.4.2', '0.2.0', '0.1.0-alpha.1') @($all | ForEach-Object version) 'Four-component independent versions'
$quakeMetadata = @(Get-PluginRegistry | Where-Object key -CEQ 'Server.Quake3')[0]
Assert-Equal '2.2.0-beta.3' $quakeMetadata.minimumHostVersion 'Published Quake host API baseline'
Assert-Equal '2.2.0-beta.3' $quakeMetadata.hostPackageVersion 'Quake package host baseline'

foreach ($version in @('1.2.3', '0.3.0-beta.1', '1.2.3-rc.2+build.7')) {
    if (-not (Test-PluginSemVer $version)) { throw "Valid SemVer was rejected: $version" }
}
foreach ($version in @('01.2.3', '1.02.3', '1.2', '1.2.3-', '1.2.3-01', 'v1.2.3')) {
    if (Test-PluginSemVer $version) { throw "Invalid SemVer was accepted: $version" }
}

Assert-Fails { Resolve-PluginSelection -Plugins @('Server.Unknown') } 'Unknown component'
Assert-Fails { Resolve-PluginSelection -Plugins @('Server.Mcp', 'Server.Mcp') } 'Duplicate component'
Assert-Fails { Resolve-PluginSelection -Tag 'server-mcp-v0.2.0' } 'Tag/version mismatch'
Assert-Fails { Resolve-PluginSelection -Tag 'plugin-v0.2.1' } 'Unknown tag component'
Assert-Fails { Resolve-PluginSelection -Tag 'server-mcp-v0.2.1-beta.01' } 'Malformed prerelease'
Assert-Fails { Resolve-PluginSelection -Plugins @('Server.RecompCatalog') -Tag 'server-mcp-v0.2.1' } 'Conflicting tag and selection'
Assert-Fails { Resolve-PluginSelection } 'Missing selection'

Write-Output 'Release selection tests passed.'
