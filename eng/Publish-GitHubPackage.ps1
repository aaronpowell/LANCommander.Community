[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Server.Mcp', 'Server.RecompCatalog', 'Server.Quake3', 'Launcher.MQTT')]
    [string] $Plugin,
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $ArtifactDirectory,
    [Parameter(Mandatory)][string] $Owner,
    [Parameter(Mandatory)][string] $Repository,
    [switch] $Publish,
    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GitHubPackages.psm1') -Force

function Get-GitHubPackageResponse {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient] $Client,
        [Parameter(Mandatory)][string] $Uri,
        [Parameter(Mandatory)][string] $Destination
    )

    $response = $Client.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        if ($response.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
            return $false
        }
        if (-not $response.IsSuccessStatusCode) {
            throw "GitHub Packages lookup failed with HTTP $([int]$response.StatusCode)."
        }
        $input = $response.Content.ReadAsStream()
        try {
            $output = [IO.File]::Create($Destination)
            try { $input.CopyTo($output) } finally { $output.Dispose() }
        }
        finally {
            $input.Dispose()
        }
        return $true
    }
    finally {
        $response.Dispose()
    }
}

$selection = @(Resolve-PluginSelection -Tag $Tag -Plugins @($Plugin))[0]
$component = @(Get-PluginRegistry | Where-Object key -CEQ $Plugin)[0]
if ($component.artifactFormat -cne 'nupkg' -or $component.githubPackagesEligible -ne $true) {
    throw "$Plugin does not produce a GitHub Packages-eligible NuGet package."
}
if ($Owner -notmatch '^[A-Za-z0-9-]+$' -or
    $Repository -cne 'aaronpowell/LANCommander.Community' -or $Owner -cne 'aaronpowell') {
    throw 'GitHub Packages publication is restricted to the canonical aaronpowell/LANCommander.Community repository.'
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
& (Join-Path $PSScriptRoot 'Test-NuGetPackage.ps1') -Plugin $Plugin -PackagePath $packagePath -ExpectedVersion $selection.version -RequireRepositoryUrl

$feedUrl = "https://nuget.pkg.github.com/$Owner/index.json"
$plan = [pscustomobject]@{
    component = $Plugin
    tag = $Tag
    packageId = $component.packageId
    version = $selection.version
    owner = $Owner
    feedUrl = $feedUrl
    packagePath = $packagePath
    sha256 = $actualHash
    publishRequested = [bool]$Publish
}
if ($PlanOnly) {
    Write-Output ($plan | ConvertTo-Json -Compress)
    return
}
if (-not $Publish) {
    Write-Output "GitHub Packages publishing is disabled; validated $Plugin $($selection.version) without publishing."
    return
}
if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
    throw 'GitHub Packages publication was explicitly requested, but GITHUB_TOKEN is unavailable.'
}
if ($env:GITHUB_ACTIONS -cne 'true' -or
    $env:GITHUB_REF -cne "refs/tags/$Tag" -or
    $env:GITHUB_REPOSITORY -cne $Repository) {
    throw 'GitHub Packages publication must run from the component release workflow for the exact tag.'
}

$tagCommit = (& git -C $repositoryRoot rev-parse "$Tag^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $manifest.sourceCommit -cne $tagCommit) {
    throw 'GitHub release manifest must match the exact release tag commit.'
}
& git -C $repositoryRoot fetch origin main
if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the reviewed main branch.' }
& git -C $repositoryRoot merge-base --is-ancestor $tagCommit origin/main
if ($LASTEXITCODE -ne 0) { throw 'The release tag must point to a commit already reachable from main.' }

$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.UserAgent.ParseAdd('LANCommander.Community-release/1.0')
$credentialBytes = [Text.Encoding]::UTF8.GetBytes("$Owner`:$($env:GITHUB_TOKEN)")
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new(
    'Basic', [Convert]::ToBase64String($credentialBytes))
$temporaryFiles = [Collections.Generic.List[string]]::new()
try {
    $serviceResponse = $client.GetAsync($feedUrl).GetAwaiter().GetResult()
    try {
        if (-not $serviceResponse.IsSuccessStatusCode) {
            throw "GitHub Packages service-index lookup failed with HTTP $([int]$serviceResponse.StatusCode)."
        }
        $serviceIndex = $serviceResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    }
    finally {
        $serviceResponse.Dispose()
    }
    $baseAddress = Get-GitHubNuGetPackageBaseAddress -ServiceIndex $serviceIndex
    $packageId = $component.packageId.ToLowerInvariant()
    $packageVersion = $selection.version.ToLowerInvariant()
    $escapedPackageId = [Uri]::EscapeDataString($packageId)
    $escapedPackageVersion = [Uri]::EscapeDataString($packageVersion)
    $packageUri = "$baseAddress/$escapedPackageId/$escapedPackageVersion/$escapedPackageId.$escapedPackageVersion.nupkg"
    $existingPath = Join-Path ([IO.Path]::GetTempPath()) "github-package-existing-$([Guid]::NewGuid().ToString('N')).nupkg"
    $temporaryFiles.Add($existingPath)
    if (Get-GitHubPackageResponse -Client $client -Uri $packageUri -Destination $existingPath) {
        Compare-GitHubNuGetPackagePayload -ExpectedPackagePath $packagePath -PublishedPackagePath $existingPath
        Write-Output "GitHub Packages already contains the same package payload for $($component.packageId) $($selection.version); no upload was needed."
        return
    }

    $configPath = Join-Path ([IO.Path]::GetTempPath()) "github-nuget-$([Guid]::NewGuid().ToString('N')).config"
    $temporaryFiles.Add($configPath)
    $configuration = [System.Xml.Linq.XDocument]::new(
        [System.Xml.Linq.XElement]::new('configuration',
            [System.Xml.Linq.XElement]::new('packageSources',
                [System.Xml.Linq.XElement]::new('clear'),
                [System.Xml.Linq.XElement]::new('add',
                    [System.Xml.Linq.XAttribute]::new('key', 'github'),
                    [System.Xml.Linq.XAttribute]::new('value', $feedUrl))),
            [System.Xml.Linq.XElement]::new('packageSourceCredentials',
                [System.Xml.Linq.XElement]::new('github',
                    [System.Xml.Linq.XElement]::new('add',
                        [System.Xml.Linq.XAttribute]::new('key', 'Username'),
                        [System.Xml.Linq.XAttribute]::new('value', $Owner)),
                    [System.Xml.Linq.XElement]::new('add',
                        [System.Xml.Linq.XAttribute]::new('key', 'ClearTextPassword'),
                        [System.Xml.Linq.XAttribute]::new('value', $env:GITHUB_TOKEN))))))
    $configuration.Save($configPath)
    $pushOutput = & dotnet nuget push $packagePath --source github --configfile $configPath --no-symbols --force-english-output 2>&1
    $pushExitCode = $LASTEXITCODE
    $pushMessage = ($pushOutput -join "`n")

    for ($attempt = 1; $attempt -le 12; $attempt++) {
        if (Test-Path -LiteralPath $existingPath) { Remove-Item -LiteralPath $existingPath -Force }
        if (Get-GitHubPackageResponse -Client $client -Uri $packageUri -Destination $existingPath) {
            Compare-GitHubNuGetPackagePayload -ExpectedPackagePath $packagePath -PublishedPackagePath $existingPath
            Write-Output "GitHub Packages package verified for $($component.packageId) $($selection.version); downloaded package entries match the immutable release asset."
            return
        }
        if ($attempt -lt 12) { Start-Sleep -Seconds 5 }
    }

    throw "GitHub Packages did not expose a package matching the immutable release asset after push (exit $pushExitCode). $pushMessage"
}
finally {
    $client.Dispose()
    foreach ($path in $temporaryFiles) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}
