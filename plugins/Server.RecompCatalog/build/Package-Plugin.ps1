#Requires -Version 7
<#
.SYNOPSIS
    Builds the Recomp Catalog server plugin and packages it as a NuGet package.

.DESCRIPTION
    The package contains the plugin and its private assemblies under lib/net10.0 for installation
    through LANCommander's Plugins page or a local NuGet feed. Packaging fails if any
    host-provided assembly would be shipped, because duplicate copies break contract and Razor
    type identity inside the plugin's isolated load context.

.PARAMETER Version
    Package version. Defaults to the <Version> in the plugin project. A leading "v" is removed.

.PARAMETER OutputDirectory
    Where the .nupkg and its .sha256 file are written. Defaults to ./artifacts.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path (Split-Path -Parent $PSScriptRoot) -Parent
$repositoryRoot = Split-Path $repositoryRoot -Parent
Import-Module (Join-Path $repositoryRoot 'eng/PluginRelease.psm1') -Force
$configuredVersion = @(Resolve-PluginSelection -Plugins @('Server.RecompCatalog'))[0].version
if ($Version -and $Version.TrimStart('v') -cne $configuredVersion) {
    throw "Use plugins/Server.RecompCatalog/version.txt as the version authority ($configuredVersion); update it before packaging a different version."
}

$arguments = @{ Plugin = 'Server.RecompCatalog'; Package = $true }
if ($OutputDirectory) { $arguments.OutputDirectory = $OutputDirectory }
& (Join-Path $repositoryRoot 'eng/Build-Plugin.ps1') @arguments
