$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
$plugins = @(Get-PluginRegistry)

foreach ($plugin in $plugins) {
    $version = Get-PluginVersion $plugin
    foreach ($path in @($plugin.project) + @($plugin.tests)) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path))) {
            throw "$($plugin.key) references missing project '$path'."
        }
    }

    [xml] $pluginProjectXml = Get-Content -LiteralPath (Join-Path $repositoryRoot $plugin.project) -Raw
    $assemblyName = $pluginProjectXml.Project.PropertyGroup.AssemblyName
    $packageId = $pluginProjectXml.Project.PropertyGroup.PackageId
    $rootNamespace = $pluginProjectXml.Project.PropertyGroup.RootNamespace
    $projectName = [IO.Path]::GetFileNameWithoutExtension($plugin.project)
    if ($plugin.assemblyName -cne $projectName -or $assemblyName -cne $projectName -or
        $plugin.packageId -cne $projectName -or $packageId -cne $projectName) {
        throw "$($plugin.key) assembly and package IDs must match its project name '$projectName'."
    }
    if ($rootNamespace -cne $plugin.rootNamespace -or $rootNamespace -notmatch '^LANCommander\.Community\.') {
        throw "$($plugin.key) root namespace does not match the Community naming registry."
    }
    $packageTypes = @(([string]$pluginProjectXml.Project.PropertyGroup.PackageType).Split(';', [StringSplitOptions]::RemoveEmptyEntries))
    $expectedTypes = @($plugin.packageTypes)
    if (@(Compare-Object $expectedTypes $packageTypes).Count -gt 0) {
        throw "$($plugin.key) PackageType metadata does not match the release registry."
    }
    $packageTags = @(([string]$pluginProjectXml.Project.PropertyGroup.PackageTags).Split(';', [StringSplitOptions]::RemoveEmptyEntries))
    foreach ($requiredTag in @($plugin.packageTags)) {
        if ($requiredTag -notin $packageTags) {
            throw "$($plugin.key) is missing required package search tag '$requiredTag'."
        }
    }
    if ($plugin.artifactFormat -eq 'zip' -and $expectedTypes.Count -gt 0) {
        throw "$($plugin.key) ZIP artifacts must not claim NuGet package types."
    }
    if ($plugin.artifactFormat -eq 'zip' -and $pluginProjectXml.Project.PropertyGroup.IsPackable -cne 'false') {
        throw "$($plugin.key) ZIP-only project must remain non-packable as a NuGet package."
    }
    if ($plugin.artifactFormat -eq 'nupkg' -and $expectedTypes.Count -eq 0) {
        throw "$($plugin.key) NuGet artifacts must declare their host package types."
    }
    if ($plugin.githubPackagesEligible -isnot [bool] -or
        ($plugin.githubPackagesEligible -and $plugin.artifactFormat -cne 'nupkg')) {
        throw "$($plugin.key) GitHub Packages eligibility must be an explicit bool and only nupkg artifacts may be eligible."
    }
    if ($plugin.githubPackagesEligible -and
        [string]$pluginProjectXml.Project.PropertyGroup.RepositoryUrl -cne 'https://github.com/aaronpowell/LANCommander.Community') {
        throw "$($plugin.key) must link GitHub Packages to the canonical LANCommander.Community repository."
    }
}

$allProjects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'plugins') -Filter '*.csproj' -File -Recurse
foreach ($project in $allProjects) {
    [xml] $projectXml = Get-Content -LiteralPath $project.FullName -Raw
    $projectName = [IO.Path]::GetFileNameWithoutExtension($project.Name)
    $assemblyName = [string]$projectXml.Project.PropertyGroup.AssemblyName
    $packageId = [string]$projectXml.Project.PropertyGroup.PackageId
    if ($assemblyName -and $assemblyName -cne $projectName) {
        throw "Project '$($project.FullName)' overrides AssemblyName '$assemblyName'; expected '$projectName'."
    }
    if ($packageId -and $packageId -cne $projectName) {
        throw "Project '$($project.FullName)' overrides PackageId '$packageId'; expected '$projectName'."
    }
}

$requiredFiles = @(
    'plugins/Server.Mcp/LICENSE',
    'plugins/Server.Mcp/THIRD-PARTY-NOTICES.txt',
    'plugins/Server.Mcp/licenses/MCP.txt',
    'plugins/Server.Mcp/licenses/Microsoft.Extensions.txt',
    'plugins/Server.RecompCatalog/LICENSE',
    'plugins/Launcher.MQTT/examples/MQTTnet-LICENSE.txt'
)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path))) {
        throw "Missing imported license or third-party notice: $path"
    }
}

$example = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/src/LANCommander.Community.Launcher.MQTT.Plugin/homeassistant.example.json') -Raw |
    ConvertFrom-Json
if ($example.BrokerUsername -or $example.BrokerPassword -or $example.UseTls) {
    throw 'The launcher example must contain blank credentials and retain the plaintext default.'
}

$launcherIds = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/src/LANCommander.Community.Launcher.MQTT.Plugin/HomeAssistantLauncherPlugin.cs') -Raw
if ($launcherIds -notmatch 'Id = "com\.aaronpowell\.lancommander\.homeassistant"') {
    throw 'The existing launcher plugin ID changed.'
}
$mcpIds = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Server.Mcp/src/LANCommander.Community.Server.Mcp/McpPlugin.cs') -Raw
if ($mcpIds -notmatch 'io\.github\.aaronpowell\.lancommander\.mcp') {
    throw 'The existing MCP plugin ID changed.'
}
$recompIds = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Server.RecompCatalog/LANCommander.Community.Server.RecompCatalog.Plugin/RecompCatalogPlugin.cs') -Raw
if ($recompIds -notmatch 'dev\.aaronpowell\.lancommander\.recompcatalog') {
    throw 'The existing Recomp Catalog plugin ID changed.'
}

$projects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'plugins') -Filter '*.csproj' -File -Recurse
foreach ($project in $projects) {
    [xml] $xml = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($reference in $xml.SelectNodes('//PackageReference[@Version]')) {
        throw "Central Package Management is bypassed by $($project.FullName): $($reference.Include)."
    }
}

$trackedConfigs = @(& git -C $repositoryRoot ls-files -- 'plugins/Launcher.MQTT/**/homeassistant.json')
if ($LASTEXITCODE -ne 0) {
    throw 'Could not inspect tracked launcher configuration files.'
}
if ($trackedConfigs.Count -gt 0) {
    throw 'A populated launcher homeassistant.json must not be tracked; only the blank example belongs in source control.'
}

Write-Output "Repository checks passed for $($plugins.Count) components."
