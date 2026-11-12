$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GitHubPackages.psm1') -Force
$testComponent = @(Get-PluginRegistry | Where-Object key -CEQ 'Server.Mcp')[0]
$testVersion = Get-PluginVersion $testComponent
$testTag = "$($testComponent.tagPrefix)-v$testVersion"
$wrongTag = "$($testComponent.tagPrefix)-v99.99.99"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "lancommander-github-packages-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originalGitHubToken = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN', 'Process')

function New-TestPackage {
    param([Parameter(Mandatory)][string] $Path, [string] $Payload = 'package payload')

    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $nuspec = @"
<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$($testComponent.packageId)</id>
    <version>$testVersion</version>
    <authors>test</authors>
    <description>Test package</description>
    <tags>LANCommander plugin server mcp</tags>
    <packageTypes><packageType name="LANCommander.Plugin" /><packageType name="LANCommander.Plugin.Server" /></packageTypes>
    <repository type="git" url="https://github.com/aaronpowell/LANCommander.Community" />
  </metadata>
</package>
"@
        foreach ($entryData in @(
            @{ name = 'LANCommander.Community.Server.Mcp.nuspec'; value = $nuspec },
            @{ name = 'lib/net10.0/payload.dll'; value = $Payload }
        )) {
            $entry = $archive.CreateEntry($entryData.name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($entryData.value) } finally { $writer.Dispose() }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

function New-RepackedPackage {
    param(
        [Parameter(Mandatory)][string] $SourcePath,
        [Parameter(Mandatory)][string] $DestinationPath,
        [string] $ChangedPath,
        [string] $AddedPath,
        [switch] $DuplicateFirstEntry
    )

    $sourceStream = [IO.File]::OpenRead($SourcePath)
    $sourceArchive = [System.IO.Compression.ZipArchive]::new($sourceStream, [System.IO.Compression.ZipArchiveMode]::Read)
    $destinationStream = [IO.File]::Create($DestinationPath)
    $destinationArchive = [System.IO.Compression.ZipArchive]::new($destinationStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($sourceEntry in @($sourceArchive.Entries | Sort-Object FullName -Descending)) {
            $entry = $destinationArchive.CreateEntry($sourceEntry.FullName)
            $input = $sourceEntry.Open()
            $output = $entry.Open()
            try {
                if ($sourceEntry.FullName -ceq 'lib/net10.0/payload.dll' -and $ChangedPath) {
                    $writer = [IO.StreamWriter]::new($output)
                    try { $writer.Write('changed payload') } finally { $writer.Dispose() }
                }
                else {
                    $input.CopyTo($output)
                }
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
        if ($AddedPath) {
            $entry = $destinationArchive.CreateEntry($AddedPath)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('additional content') } finally { $writer.Dispose() }
        }
        if ($DuplicateFirstEntry) {
            $entry = $destinationArchive.CreateEntry($sourceArchive.Entries[0].FullName)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('duplicate entry') } finally { $writer.Dispose() }
        }
    }
    finally {
        $destinationArchive.Dispose()
        $destinationStream.Dispose()
        $sourceArchive.Dispose()
        $sourceStream.Dispose()
    }
}

function Assert-Fails {
    param([scriptblock] $Action, [string] $ExpectedText, [string] $Name)
    try { & $Action } catch {
        if ($_.Exception.Message -notlike "*$ExpectedText*") {
            throw "$Name failed for the wrong reason: $($_.Exception.Message)"
        }
        return
    }
    throw "$Name unexpectedly succeeded."
}

try {
    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release-plugin.yml') -Raw
    $buildJob = [regex]::Match($workflow, '(?ms)^  build:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
    $githubPackagesJob = [regex]::Match($workflow, '(?ms)^  publish-github-packages:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
    if ($workflow -notmatch 'workflow_call:' -or
        $workflow -notmatch '(?ms)^permissions:\r?\n  actions:\s*read\r?\n  contents:\s*read\r?\n  packages:\s*write' -or
        $buildJob -notmatch '(?ms)^\s+permissions:\r?\n\s+actions:\s*read\r?\n\s+contents:\s*read' -or
        $buildJob -notmatch "if:\s+inputs\.artifact_run_id != ''" -or
        $buildJob -notmatch 'github-token:\s+\$\{\{\s*github\.token\s*\}\}' -or
        $buildJob -notmatch 'run-id:\s+\$\{\{\s*inputs\.artifact_run_id\s*\}\}' -or
        $buildJob -notmatch 'inputs\.artifact_run_attempt' -or
        $buildJob -notmatch 'Publish-Release\.ps1.*-PlanOnly' -or
        $githubPackagesJob -notmatch "if:\s+needs\.build\.outputs\.github_packages_eligible == 'true'" -or
        $githubPackagesJob -notmatch "ref:\s*\$\{\{\s*github\.event_name == 'workflow_dispatch' && github\.sha \|\| inputs\.tag\s*\}\}" -or
        $githubPackagesJob -notmatch 'packages:\s+write' -or
        $githubPackagesJob -notmatch 'GITHUB_TOKEN:\s+\$\{\{\s*github\.token\s*\}\}' -or
        $githubPackagesJob -notmatch 'Publish-GitHubPackage\.ps1.*-Publish' -or
        $githubPackagesJob -notmatch 'actions/download-artifact@' -or
        $githubPackagesJob -match 'NUGET_API_KEY|dotnet pack|Validate-Plugin\.ps1') {
        throw 'GitHub Packages must publish the selected eligible component automatically from the shared validated artifact.'
    }
    if (Test-Path -LiteralPath (Join-Path $repositoryRoot '.github/workflows/publish-github-packages.yml')) {
        throw 'The separate manual GitHub Packages workflow must not remain.'
    }
    $releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release.yml') -Raw
    if ($releaseWorkflow -notmatch 'actions:\s+read' -or
        $releaseWorkflow -notmatch 'packages:\s+write' -or
        $releaseWorkflow -notmatch 'workflow_dispatch:' -or
        $releaseWorkflow -notmatch "RUN_REF -cne 'refs/heads/main'" -or
        $releaseWorkflow -notmatch 'artifact_run_id:' -or
        $releaseWorkflow -notmatch 'artifact_run_attempt:' -or
        $releaseWorkflow -notmatch 'artifact_run_id:\s+\$\{\{\s*needs\.plan\.outputs\.artifact_run_id\s*\}\}' -or
        $releaseWorkflow -notmatch 'nuget_api_key:\s+\$\{\{\s*secrets\.NUGET_API_KEY\s*\}\}') {
        throw 'Component releases must provide scoped caller permissions, optional NuGet.org secret forwarding, and exact-artifact main-only recovery dispatch.'
    }

    Assert-ReleaseWorkflowContext -Tag $testTag -Repository 'aaronpowell/LANCommander.Community' `
        -ExpectedRepository 'aaronpowell/LANCommander.Community' -Actions 'true' `
        -EventName 'push' -Ref "refs/tags/$testTag"
    Assert-ReleaseWorkflowContext -Tag $testTag -Repository 'aaronpowell/LANCommander.Community' `
        -ExpectedRepository 'aaronpowell/LANCommander.Community' -Actions 'true' `
        -EventName 'workflow_dispatch' -Ref 'refs/heads/main'
    Assert-Fails {
        Assert-ReleaseWorkflowContext -Tag $testTag -Repository 'aaronpowell/LANCommander.Community' `
            -ExpectedRepository 'aaronpowell/LANCommander.Community' -Actions 'true' `
            -EventName 'workflow_dispatch' -Ref 'refs/heads/feature'
    } 'main-branch recovery dispatch' 'Non-main recovery dispatch'

    $artifactDirectory = Join-Path $testRoot 'artifact'
    New-Item -ItemType Directory -Path $artifactDirectory | Out-Null
    $packageName = "$($testComponent.packageId).$testVersion.nupkg"
    $packagePath = Join-Path $artifactDirectory $packageName
    New-TestPackage -Path $packagePath
    & (Join-Path $PSScriptRoot 'Test-NuGetPackage.ps1') -Plugin Server.Mcp -PackagePath $packagePath -ExpectedVersion $testVersion -RequireRepositoryUrl
    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$packagePath.sha256" -Value "$hash  $packageName" -NoNewline
    [ordered]@{
        component = 'Server.Mcp'
        version = $testVersion
        host = 'Server'
        minimumHostVersion = '2.2.0-beta.4'
        maximumHostVersion = $null
        artifactFormat = 'nupkg'
        asset = $packageName
        sha256 = $hash
        sourceCommit = '0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'release-manifest.json')

    $disabledOutput = (& (Join-Path $PSScriptRoot 'Publish-GitHubPackage.ps1') -Plugin Server.Mcp -Tag $testTag -ArtifactDirectory $artifactDirectory -Owner aaronpowell -Repository aaronpowell/LANCommander.Community 2>&1) -join "`n"
    if ($disabledOutput -notmatch 'publishing is disabled') { throw 'The default GitHub Packages path did not remain disabled.' }

    $planOutput = (& (Join-Path $PSScriptRoot 'Publish-GitHubPackage.ps1') -Plugin Server.Mcp -Tag $testTag -ArtifactDirectory $artifactDirectory -Owner aaronpowell -Repository aaronpowell/LANCommander.Community -Publish -PlanOnly 2>&1) -join "`n"
    $planLine = @($planOutput -split "\r?\n" | Where-Object { $_ -match '^\{"component"' }) | Select-Object -Last 1
    $plan = $planLine | ConvertFrom-Json
    if ($plan.component -cne 'Server.Mcp' -or
        $plan.packageId -cne 'LANCommander.Community.Server.Mcp' -or
        $plan.owner -cne 'aaronpowell' -or
        $plan.feedUrl -cne 'https://nuget.pkg.github.com/aaronpowell/index.json' -or
        $plan.publishRequested -ne $true) {
        throw 'The GitHub Packages plan did not preserve component, identity, version, owner, and feed.'
    }
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    Assert-Fails {
        & (Join-Path $PSScriptRoot 'Publish-GitHubPackage.ps1') -Plugin Server.Mcp -Tag $testTag -ArtifactDirectory $artifactDirectory -Owner aaronpowell -Repository aaronpowell/LANCommander.Community -Publish
    } 'GITHUB_TOKEN is unavailable' 'Missing GitHub Packages credential'
    Assert-Fails {
        & (Join-Path $PSScriptRoot 'Publish-GitHubPackage.ps1') -Plugin Server.Mcp -Tag $wrongTag -ArtifactDirectory $artifactDirectory -Owner aaronpowell -Repository aaronpowell/LANCommander.Community -Publish -PlanOnly
    } 'does not match' 'Release tag selection'
    $manifestPath = Join-Path $artifactDirectory 'release-manifest.json'
    $manifestText = Get-Content -LiteralPath $manifestPath -Raw
    try {
        Set-Content -LiteralPath $manifestPath -Value ($manifestText.Replace('"nupkg"', '"zip"'))
        Assert-Fails {
            & (Join-Path $PSScriptRoot 'Publish-GitHubPackage.ps1') -Plugin Server.Mcp -Tag $testTag -ArtifactDirectory $artifactDirectory -Owner aaronpowell -Repository aaronpowell/LANCommander.Community -Publish -PlanOnly
        } 'manifest does not match' 'ZIP artifact rejected by NuGet publisher'
    }
    finally { Set-Content -LiteralPath $manifestPath -Value $manifestText }
    Assert-Fails {
        & (Join-Path $PSScriptRoot 'Publish-GitHubPackage.ps1') -Plugin Server.Mcp -Tag $testTag -ArtifactDirectory $artifactDirectory -Owner someone-else -Repository someone-else/LANCommander.Community -Publish -PlanOnly
    } 'restricted to the canonical' 'Repository/owner guard'

    $equivalentPath = Join-Path $testRoot 'equivalent.nupkg'
    New-RepackedPackage -SourcePath $packagePath -DestinationPath $equivalentPath
    if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $equivalentPath -Algorithm SHA256).Hash) {
        throw 'Repacked fixture must have different archive bytes to prove content comparison is entry-based.'
    }
    Compare-GitHubNuGetPackagePayload -ExpectedPackagePath $packagePath -PublishedPackagePath $equivalentPath

    $changedPath = Join-Path $testRoot 'changed.nupkg'
    New-RepackedPackage -SourcePath $packagePath -DestinationPath $changedPath -ChangedPath payload
    Assert-Fails {
        Compare-GitHubNuGetPackagePayload -ExpectedPackagePath $packagePath -PublishedPackagePath $changedPath
    } "differs from the release asset at 'lib/net10.0/payload.dll'" 'Changed package payload conflict'
    $extraPath = Join-Path $testRoot 'extra.nupkg'
    New-RepackedPackage -SourcePath $packagePath -DestinationPath $extraPath -AddedPath '.signature.p7s'
    Assert-Fails {
        Compare-GitHubNuGetPackagePayload -ExpectedPackagePath $packagePath -PublishedPackagePath $extraPath
    } 'different set of package entries' 'Additional signature entry'
    $duplicatePath = Join-Path $testRoot 'duplicate.nupkg'
    New-RepackedPackage -SourcePath $packagePath -DestinationPath $duplicatePath -DuplicateFirstEntry
    Assert-Fails {
        Compare-GitHubNuGetPackagePayload -ExpectedPackagePath $packagePath -PublishedPackagePath $duplicatePath
    } 'duplicate path' 'Duplicate archive entry'

    $serviceIndex = @'
{"resources":[{"@id":"https://nuget.pkg.github.com/aaronpowell/download/","@type":"PackageBaseAddress/3.0.0"}]}
'@ | ConvertFrom-Json
    if ((Get-GitHubNuGetPackageBaseAddress -ServiceIndex $serviceIndex) -cne 'https://nuget.pkg.github.com/aaronpowell/download') {
        throw 'GitHub Packages flat-container resource was not selected.'
    }

    $publisherSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Publish-GitHubPackage.ps1') -Raw
    if ($publisherSource -notmatch 'New-GitHubNuGetConfigFile\s+-Owner\s+\$Owner\s+-Token\s+\$env:GITHUB_TOKEN') {
        throw 'GitHub Packages publisher must use the tested NuGet source-config generator.'
    }
    $configPath = Join-Path $testRoot 'github-nuget.config'
    $testOwner = 'owner&<"quoted'
    $testToken = 'token&<>"''quoted'
    New-GitHubNuGetConfigFile -Owner $testOwner -Token $testToken `
        -FeedUrl 'https://nuget.pkg.github.com/owner/index.json' -Path $configPath | Out-Null
    $config = [System.Xml.XmlDocument]::new()
    $config.Load($configPath)
    $sourceNode = $config.SelectSingleNode('/configuration/packageSources/add[@key="github"]')
    $clearNode = $config.SelectSingleNode('/configuration/packageSources/clear')
    $usernameNode = $config.SelectSingleNode('/configuration/packageSourceCredentials/github/add[@key="Username"]')
    $tokenNode = $config.SelectSingleNode('/configuration/packageSourceCredentials/github/add[@key="ClearTextPassword"]')
    if ($null -eq $sourceNode -or $sourceNode.GetAttribute('value') -cne 'https://nuget.pkg.github.com/owner/index.json' -or
        $null -eq $clearNode -or $null -eq $usernameNode -or $usernameNode.GetAttribute('value') -cne $testOwner -or
        $null -eq $tokenNode -or $tokenNode.GetAttribute('value') -cne $testToken) {
        throw 'Generated NuGet source config must contain valid, correctly escaped source and credential values.'
    }

    Assert-Fails {
        Get-GitHubNuGetPackageBaseAddress -ServiceIndex (@'
{"resources":[{"@id":"https://example.invalid/","@type":"PackageBaseAddress/3.0.0"}]}
'@ | ConvertFrom-Json)
    } 'unexpected package download endpoint' 'Untrusted feed endpoint'

    Write-Output 'GitHub Packages publication tests passed. No feed request or package publication was performed.'
}
finally {
    if ($null -eq $originalGitHubToken) {
        Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    }
    else {
        $env:GITHUB_TOKEN = $originalGitHubToken
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
