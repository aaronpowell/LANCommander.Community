[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Server.Mcp', 'Server.RecompCatalog', 'Launcher.MQTT', 'Server.Quake3')]
    [string] $Plugin,
    [switch] $Package,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
$selection = @(Resolve-PluginSelection -Plugins @($Plugin))
$component = @(Get-PluginRegistry | Where-Object key -CEQ $Plugin)[0]
$version = $selection[0].version
$artifactDirectory = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    Join-Path $repositoryRoot "artifacts/$($component.tagPrefix)-v$version"
}

if (Test-Path -LiteralPath $artifactDirectory) {
    throw "Output directory already exists: $artifactDirectory. Use a fresh path to prevent overwriting artifacts."
}
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$project = Join-Path $repositoryRoot $component.project
$packageId = $component.packageId

if ($Plugin -eq 'Launcher.MQTT') {
    $hostSource = if ($env:LANCOMMANDER_SOURCE_ROOT) {
        [IO.Path]::GetFullPath($env:LANCOMMANDER_SOURCE_ROOT)
    }
    else {
        Join-Path $repositoryRoot '.cache/lancommander-beta4'
    }
    & (Join-Path $PSScriptRoot 'Prepare-LANCommanderSource.ps1') -Destination $hostSource | Out-Null
    $sourcePin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'lancommander-source.json') -Raw | ConvertFrom-Json
    $actualCommit = (& git -C $hostSource rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $sourcePin.commit) {
        throw "Launcher.MQTT requires LANCommander source $($sourcePin.commit); found '$actualCommit'."
    }
    $env:LANCOMMANDER_SOURCE_ROOT = $hostSource
    & dotnet build $project -c Release --nologo "-p:LANCommanderSourceRoot=$hostSource"
    if ($LASTEXITCODE -ne 0) { throw "Launcher.MQTT build failed with exit code $LASTEXITCODE." }
    if ($Package) {
        & dotnet pack $project -c Release --no-build -o $artifactDirectory --nologo "-p:LANCommanderSourceRoot=$hostSource"
    }
}
elseif ($Package) {
    & dotnet pack $project -c Release -o $artifactDirectory --nologo
}
else {
    & dotnet build $project -c Release --nologo
}
if ($LASTEXITCODE -ne 0) { throw "Build/pack failed for $Plugin with exit code $LASTEXITCODE." }

if (-not $Package) {
    return
}

$asset = Join-Path $artifactDirectory "$packageId.$version.nupkg"
if (-not (Test-Path -LiteralPath $asset)) {
    throw "Pack did not produce the expected selected-plugin asset: $asset"
}
& (Join-Path $PSScriptRoot 'Test-NuGetPackage.ps1') -Plugin $Plugin -PackagePath $asset -ExpectedVersion $version -RequireRepositoryUrl
if ($Plugin -eq 'Launcher.MQTT') {
    & (Join-Path $PSScriptRoot 'Test-LauncherPackage.ps1') -PackagePath $asset -ExpectedVersion $version
}
else {
    if ($Plugin -eq 'Server.Mcp') {
        & (Join-Path $repositoryRoot 'plugins/Server.Mcp/eng/Test-Package.ps1') -PackagePath $asset -ExpectedVersion $version
    }
    elseif ($Plugin -eq 'Server.Quake3') {
        & (Join-Path $repositoryRoot 'plugins/Server.Quake3/eng/Test-Package.ps1') -PackagePath $asset -ExpectedVersion $version
    }
    else {
        & (Join-Path $PSScriptRoot 'Test-Recomp-Package.ps1') -PackagePath $asset -ExpectedVersion $version
    }
}
if ($LASTEXITCODE -ne 0) { throw "Package verification failed for $Plugin." }

$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$asset.sha256" -Value "$hash  $(Split-Path -Leaf $asset)" -NoNewline
$manifest = [ordered]@{
    component = $Plugin
    version = $version
    host = $component.host
    minimumHostVersion = $component.minimumHostVersion
    maximumHostVersion = $component.maximumHostVersion
    lanCommanderPackageVersion = if ($component.hostPackageVersion) { $component.hostPackageVersion } else { '2.2.0-beta.4' }
    lanCommanderSourceCommit = if ($Plugin -eq 'Launcher.MQTT') { $sourcePin.commit } elseif ($component.hostSourceCommit) { $component.hostSourceCommit } else { $null }
    artifactFormat = $component.artifactFormat
    asset = Split-Path -Leaf $asset
    sha256 = $hash
    sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    sourceDirty = @(& git -C $repositoryRoot status --porcelain).Count -gt 0
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'release-manifest.json') -Encoding utf8

Write-Output "Verified $Plugin ${version}: $asset"
