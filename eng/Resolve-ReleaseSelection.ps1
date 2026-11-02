[CmdletBinding()]
param(
    [string[]] $Plugins,
    [string] $Tag
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
$selection = @(Resolve-PluginSelection -Plugins $Plugins -Tag $Tag)
$selection | ConvertTo-Json -Depth 4 -Compress -AsArray
