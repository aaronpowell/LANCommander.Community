[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Server.Mcp', 'Server.RecompCatalog', 'Server.Quake3', 'Launcher.MQTT')]
    [string] $Plugin,
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $ArtifactDirectory,
    [switch] $Publish,
    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NuGetRelease.psm1') -Force

function Get-NuGetPublishedPackagePath {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient] $Client,
        [Parameter(Mandatory)][string] $Uri
    )

    $response = $Client.GetAsync($Uri).GetAwaiter().GetResult()
    try {
        if ($response.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
            return $null
        }
        if (-not $response.IsSuccessStatusCode) {
            throw "NuGet.org package lookup failed with HTTP $([int]$response.StatusCode)."
        }

        $stream = $response.Content.ReadAsStream()
        $packagePath = Join-Path ([IO.Path]::GetTempPath()) "nuget-published-$([Guid]::NewGuid().ToString('N')).nupkg"
        try {
            $file = [IO.File]::Create($packagePath)
            try { $stream.CopyTo($file) } finally { $file.Dispose() }
            return $packagePath
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $response.Dispose()
    }
}

function Test-NuGetPublishedPackage {
    param(
        [Parameter(Mandatory)][string] $ExpectedPackagePath,
        [Parameter(Mandatory)][string] $PublishedPackagePath
    )

    $verificationOutput = & dotnet nuget verify $PublishedPackagePath --all --verbosity normal 2>&1
    $verificationExitCode = $LASTEXITCODE
    Assert-NuGetRepositorySignature -PackagePath $PublishedPackagePath `
        -VerificationExitCode $verificationExitCode -VerificationOutput ($verificationOutput -join "`n")
    Compare-NuGetPackagePayload -ExpectedPackagePath $ExpectedPackagePath -PublishedPackagePath $PublishedPackagePath
}

$selection = @(Resolve-PluginSelection -Tag $Tag -Plugins @($Plugin))[0]
$component = @(Get-PluginRegistry | Where-Object key -CEQ $Plugin)[0]
if ($component.artifactFormat -cne 'nupkg' -or $component.nugetOrgEligible -ne $true) {
    throw "$Plugin produces '$($component.artifactFormat)' artifacts and is not eligible for NuGet publication."
}

$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
    throw "Artifact directory does not exist: $artifactRoot"
}
$manifestPath = Join-Path $artifactRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The verified GitHub release manifest is missing.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expectedAsset = "$($component.packageId).$($selection.version).nupkg"
if ($manifest.component -cne $Plugin -or $manifest.version -cne $selection.version -or
    $manifest.artifactFormat -cne 'nupkg' -or $manifest.asset -cne $expectedAsset -or
    $manifest.host -cne $component.host -or
    $manifest.minimumHostVersion -cne $component.minimumHostVersion -or
    $manifest.maximumHostVersion -cne $component.maximumHostVersion -or
    $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "GitHub release manifest does not match $Plugin $($selection.version)."
}

$assets = @(Get-ChildItem -LiteralPath $artifactRoot -File)
if ($assets.Count -ne 3 -or $expectedAsset -notin $assets.Name -or
    "$expectedAsset.sha256" -notin $assets.Name -or 'release-manifest.json' -notin $assets.Name) {
    throw "Expected exactly $expectedAsset, its checksum, and release-manifest.json."
}
$packagePath = Join-Path $artifactRoot $expectedAsset
$actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -cne $manifest.sha256) { throw 'NuGet package bytes do not match the GitHub release manifest.' }
$checksum = (Get-Content -LiteralPath "$packagePath.sha256" -Raw).Trim()
if ($checksum -cne "$actualHash  $expectedAsset") { throw 'NuGet package checksum sidecar does not match the package bytes.' }
& (Join-Path $PSScriptRoot 'Test-NuGetPackage.ps1') -Plugin $Plugin -PackagePath $packagePath -ExpectedVersion $selection.version

$plan = [pscustomobject]@{
    component = $Plugin
    tag = $Tag
    packageId = $component.packageId
    version = $selection.version
    packagePath = $packagePath
    sha256 = $actualHash
    publishRequested = [bool]$Publish
}
if (-not $Publish) {
    Write-Output "NuGet publication is disabled; validated $Plugin $($selection.version) without publishing."
    return
}
if ($PlanOnly) {
    Write-Output ($plan | ConvertTo-Json -Compress)
    return
}

if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    throw 'NuGet publication was explicitly requested, but NUGET_API_KEY is missing.'
}
if ($env:GITHUB_ACTIONS -cne 'true' -or
    $env:GITHUB_REF -cne "refs/tags/$Tag" -or
    $env:GITHUB_REPOSITORY -cne 'aaronpowell/LANCommander.Community') {
    throw 'NuGet publication must run through the protected GitHub Actions workflow.'
}

$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$tagCommit = (& git -C $repositoryRoot rev-parse "$Tag^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $headCommit -cne $tagCommit -or $manifest.sourceCommit -cne $tagCommit) {
    throw 'NuGet publication checkout and manifest must match the exact release tag commit.'
}

$packageId = $component.packageId.ToLowerInvariant()
$packageVersion = $selection.version.ToLowerInvariant()
$packageUrl = "https://api.nuget.org/v3-flatcontainer/$([Uri]::EscapeDataString($packageId))/$([Uri]::EscapeDataString($packageVersion))/$([Uri]::EscapeDataString($packageId)).$([Uri]::EscapeDataString($packageVersion)).nupkg"
$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.DefaultRequestHeaders.UserAgent.ParseAdd('LANCommander.Community-release/1.0')
try {
    $existingPackagePath = Get-NuGetPublishedPackagePath -Client $httpClient -Uri $packageUrl
    if ($null -ne $existingPackagePath) {
        try {
            Test-NuGetPublishedPackage -ExpectedPackagePath $packagePath -PublishedPackagePath $existingPackagePath
            Write-Output "NuGet.org already contains the same verified package payload for $($component.packageId) $($selection.version); no upload was needed."
            return
        }
        finally {
            Remove-Item -LiteralPath $existingPackagePath -Force -ErrorAction SilentlyContinue
        }
    }

    $pushOutput = & dotnet nuget push $packagePath --source 'https://api.nuget.org/v3/index.json' --api-key $env:NUGET_API_KEY 2>&1
    $pushExitCode = $LASTEXITCODE
    $pushError = ($pushOutput -join "`n").Replace($env:NUGET_API_KEY, '[REDACTED]')
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $publishedPackagePath = Get-NuGetPublishedPackagePath -Client $httpClient -Uri $packageUrl
        if ($null -ne $publishedPackagePath) {
            try {
                Test-NuGetPublishedPackage -ExpectedPackagePath $packagePath -PublishedPackagePath $publishedPackagePath
                Write-Output "NuGet.org package verified: $($component.packageId) $($selection.version) payload matches the release asset."
                return
            }
            finally {
                Remove-Item -LiteralPath $publishedPackagePath -Force -ErrorAction SilentlyContinue
            }
        }
        if ($attempt -lt 12) { Start-Sleep -Seconds 5 }
    }

    throw "NuGet.org did not expose a verified package with the exact release payload after push (exit $pushExitCode). $pushError"
}
finally {
    $httpClient.Dispose()
}
