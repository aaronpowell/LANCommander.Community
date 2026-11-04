[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Server.Mcp', 'Server.RecompCatalog', 'Launcher.MQTT', 'Server.Quake3')]
    [string] $Plugin,
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $ArtifactDirectory,
    [string] $Repository = 'aaronpowell/LANCommander.Community',
    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
$selection = @(Resolve-PluginSelection -Tag $Tag -Plugins @($Plugin))[0]
$component = @(Get-PluginRegistry | Where-Object key -CEQ $Plugin)[0]
if ($component.releaseBlockedReason) {
    throw "$Plugin cannot be released: $($component.releaseBlockedReason)"
}
$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$tagCommit = (& git -C $repositoryRoot rev-parse "$Tag^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $headCommit -cne $tagCommit) {
    throw "Release checkout $headCommit does not match the selected tag commit $tagCommit."
}
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
    throw "Artifact directory does not exist: $artifactRoot"
}

$manifestPath = Join-Path $artifactRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The verified release manifest is missing.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.sourceDirty -eq $true) {
    throw 'Development artifacts built from uncommitted source cannot be published as a reviewed release.'
}
if ($manifest.component -cne $Plugin -or $manifest.version -cne $selection.version -or
    $manifest.sourceCommit -cne $tagCommit) {
    throw "Release manifest does not match $Plugin $($selection.version)."
}
if ($manifest.host -cne $component.host -or $manifest.minimumHostVersion -cne $component.minimumHostVersion) {
    throw 'Release manifest host compatibility does not match the reviewed plugin registry.'
}

$assets = @(Get-ChildItem -LiteralPath $artifactRoot -File)
$expectedAsset = [string]$manifest.asset
if ($assets.Count -ne 3 -or "$expectedAsset.sha256" -notin $assets.Name -or
    $expectedAsset -notin $assets.Name -or 'release-manifest.json' -notin $assets.Name) {
    throw "Expected exactly $expectedAsset, its checksum, and release-manifest.json."
}
$assetPath = Join-Path $artifactRoot $expectedAsset
$actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -cne $manifest.sha256) { throw 'Artifact bytes do not match the verified release manifest.' }
$checksum = (Get-Content -LiteralPath "$assetPath.sha256" -Raw).Trim()
if ($checksum -cne "$actualHash  $expectedAsset") { throw 'Artifact checksum sidecar does not match the bytes.' }

$changelogPath = Join-Path $repositoryRoot ($component.versionFile -replace 'version\.txt$', 'CHANGELOG.md')
$changelog = Get-Content -LiteralPath $changelogPath -Raw
$lines = $changelog -split "\r?\n"
$section = [System.Collections.Generic.List[string]]::new()
$found = $false
foreach ($line in $lines) {
    if ($line -ceq "## $($selection.version)") { $found = $true; continue }
    if ($found -and $line -match '^##\s+') { break }
    if ($found) { $section.Add($line) }
}
if (-not $found -or -not ($section -join "`n").Trim()) {
    throw "No release notes exist for $Plugin $($selection.version)."
}
$notes = $section -join "`n"
$title = "$Plugin $($selection.version)"

Write-Output ([pscustomobject]@{
    repository = $Repository
    tag = $Tag
    component = $Plugin
    version = $selection.version
    assets = @($assets | ForEach-Object { @{ name = $_.Name; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    host = $manifest.host
    minimumHostVersion = $manifest.minimumHostVersion
    maximumHostVersion = $manifest.maximumHostVersion
} | ConvertTo-Json -Depth 5 -Compress)
if ($PlanOnly) { return }

$viewOutput = & gh release view $Tag --repo $Repository --json isDraft,assets 2>&1
$viewExitCode = $LASTEXITCODE
$release = $null
$created = $false
if ($viewExitCode -eq 0) {
    $release = ($viewOutput -join "`n") | ConvertFrom-Json
}
elseif (($viewOutput -join "`n") -match '(?i)(404|release not found)') {
    $notesPath = Join-Path ([IO.Path]::GetTempPath()) "lancommander-release-notes-$([Guid]::NewGuid().ToString('N')).md"
    try {
        Set-Content -LiteralPath $notesPath -Value $notes -Encoding utf8
        $createOutput = & gh release create $Tag --repo $Repository --verify-tag --draft --title $title --notes-file $notesPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not create draft release: $($createOutput -join "`n")" }
        $created = $true
        $viewOutput = & gh release view $Tag --repo $Repository --json isDraft,assets 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not read newly created draft release: $($viewOutput -join "`n")" }
        $release = ($viewOutput -join "`n") | ConvertFrom-Json
    }
    finally {
        Remove-Item -LiteralPath $notesPath -Force -ErrorAction SilentlyContinue
    }
}
else {
    throw "Could not inspect release ${Tag}: $($viewOutput -join "`n")"
}

if ($release.isDraft -ne $true -and $created) {
    throw "Newly created release $Tag is not a draft."
}
$expectedNames = @($assets.Name | Sort-Object)
$unexpected = @($release.assets | Where-Object { $_.name -notin $expectedNames })
if ($unexpected.Count -gt 0) {
    throw "Release contains unexpected assets: $(($unexpected.name) -join ', ')."
}

$downloadDirectory = Join-Path ([IO.Path]::GetTempPath()) "lancommander-release-assets-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
try {
    foreach ($asset in $assets) {
        $remote = @($release.assets | Where-Object name -CEQ $asset.Name)
        if ($remote.Count -gt 1) { throw "Release has duplicate asset name '$($asset.Name)'." }
        if ($remote.Count -eq 0) {
            if ($release.isDraft -ne $true) {
                throw "Published release is missing asset '$($asset.Name)'; published bytes will not be changed."
            }
            $uploadOutput = & gh release upload $Tag --repo $Repository $asset.FullName 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Could not upload '$($asset.Name)': $($uploadOutput -join "`n")" }
            continue
        }

        $downloadOutput = & gh release download $Tag --repo $Repository --dir $downloadDirectory --pattern $asset.Name 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not download existing asset '$($asset.Name)': $($downloadOutput -join "`n")" }
        $downloadPath = Join-Path $downloadDirectory $asset.Name
        if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) {
            throw "Release download did not produce '$($asset.Name)'."
        }
        $remoteHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $localHash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteHash -cne $localHash) {
            throw "Existing release asset '$($asset.Name)' has different bytes. Immutable release assets are never replaced."
        }
        Remove-Item -LiteralPath $downloadPath -Force
    }

    $verifyOutput = & gh release view $Tag --repo $Repository --json isDraft,assets 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not verify uploaded release assets: $($verifyOutput -join "`n")" }
    $verifiedRelease = ($verifyOutput -join "`n") | ConvertFrom-Json
    foreach ($asset in $assets) {
        if ($asset.Name -notin $verifiedRelease.assets.name) {
            throw "Release asset is still missing after upload: $($asset.Name)."
        }
        $verifyOutput = & gh release download $Tag --repo $Repository --dir $downloadDirectory --pattern $asset.Name 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not verify uploaded asset '$($asset.Name)'." }
        $remoteHash = (Get-FileHash -LiteralPath (Join-Path $downloadDirectory $asset.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
        $localHash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteHash -cne $localHash) { throw "Uploaded asset '$($asset.Name)' failed its SHA-256 verification." }
        Remove-Item -LiteralPath (Join-Path $downloadDirectory $asset.Name) -Force
    }

    if ($verifiedRelease.isDraft) {
        $editOutput = & gh release edit $Tag --repo $Repository --draft=false --title $title --notes $notes 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Assets are verified, but the release could not be published: $($editOutput -join "`n")" }
    }
    Write-Output "Published verified release $Tag."
}
finally {
    Remove-Item -LiteralPath $downloadDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
