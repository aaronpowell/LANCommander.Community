$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NuGetRelease.psm1') -Force
$publisher = Join-Path $PSScriptRoot 'Publish-NuGetPackage.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "lancommander-nuget-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originalApiKey = [Environment]::GetEnvironmentVariable('NUGET_API_KEY', 'Process')

function New-TestPackage {
    param(
        [Parameter(Mandatory)][string] $Path,
        [string] $PackageId = 'LANCommander.Mcp',
        [string] $Version = '0.2.1',
        [string[]] $Types = @('LANCommander.Plugin', 'LANCommander.Plugin.Server'),
        [string[]] $Tags = @('LANCommander', 'plugin', 'server', 'mcp')
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $typeXml = ($Types | ForEach-Object { "<packageType name=`"$_`" />" }) -join ''
        $tagText = $Tags -join ' '
        $nuspec = @"
<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$PackageId</id>
    <version>$Version</version>
    <authors>test</authors>
    <description>Test package</description>
    <tags>$tagText</tags>
    <packageTypes>$typeXml</packageTypes>
  </metadata>
</package>
"@
        $entry = $archive.CreateEntry("$PackageId.nuspec")
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write($nuspec) } finally { $writer.Dispose() }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

function New-TestArtifact {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [string] $PackageId = 'LANCommander.Mcp',
        [string] $PackageVersion = '0.2.1',
        [string] $NuspecId = $PackageId,
        [string] $NuspecVersion = $PackageVersion,
        [string[]] $Types = @('LANCommander.Plugin', 'LANCommander.Plugin.Server'),
        [string[]] $Tags = @('LANCommander', 'plugin', 'server', 'mcp')
    )

    New-Item -ItemType Directory -Path $Directory | Out-Null
    $assetName = "$PackageId.$PackageVersion.nupkg"
    $assetPath = Join-Path $Directory $assetName
    New-TestPackage -Path $assetPath -PackageId $NuspecId -Version $NuspecVersion -Types $Types -Tags $Tags
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$assetPath.sha256" -Value "$hash  $assetName" -NoNewline
    [ordered]@{
        component = 'Server.Mcp'
        version = $PackageVersion
        host = 'Server'
        minimumHostVersion = '2.2.0-alpha.4'
        maximumHostVersion = $null
        artifactFormat = 'nupkg'
        asset = $assetName
        sha256 = $hash
        sourceCommit = '0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'release-manifest.json')
}

function Assert-Fails {
    param([scriptblock] $Action, [string] $ExpectedText, [string] $Name)
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedText*") {
            throw "$Name failed for the wrong reason: $($_.Exception.Message)"
        }
        return
    }
    throw "$Name unexpectedly succeeded."
}

try {
    Remove-Item Env:NUGET_API_KEY -ErrorAction SilentlyContinue
    $nugetWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/publish-nuget.yml') -Raw
    if ($nugetWorkflow -notmatch '(?m)^  workflow_dispatch:' -or
        $nugetWorkflow -match '(?m)^  push:' -or
        $nugetWorkflow -notmatch "WORKFLOW_REF -cne 'refs/heads/main'" -or
        $nugetWorkflow -notmatch 'name:\s+nuget\.org' -or
        $nugetWorkflow -notmatch 'NUGET_API_KEY:\s+\$\{\{\s*secrets\.NUGET_API_KEY\s*\}\}' -or
        $nugetWorkflow -notmatch 'Publish-NuGetPackage\.ps1.*-Publish') {
        throw 'NuGet publication must stay manual, environment-gated, and explicitly opted in.'
    }
    $tagWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release.yml') -Raw
    if ($tagWorkflow -match 'NUGET_API_KEY|Publish-NuGetPackage') {
        throw 'The tag-triggered release workflow must remain GitHub-only.'
    }
    $publisherSource = Get-Content -LiteralPath $publisher -Raw
    if ($publisherSource -match '--skip-duplicate') {
        throw 'NuGet publication must not hide immutable package conflicts with --skip-duplicate.'
    }

    $validDirectory = Join-Path $testRoot 'valid'
    New-TestArtifact -Directory $validDirectory

    $disabledOutput = (& $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $validDirectory 2>&1) -join "`n"
    if ($disabledOutput -notmatch 'publication is disabled') {
        throw 'The default NuGet path did not remain disabled.'
    }

    $planOutput = (& $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $validDirectory -Publish -PlanOnly 2>&1) -join "`n"
    $plan = $planOutput -split "\r?\n" | Where-Object { $_ -match '^\{"component"' } | Select-Object -Last 1 | ConvertFrom-Json
    if ($plan.component -cne 'Server.Mcp' -or $plan.packageId -cne 'LANCommander.Mcp' -or
        $plan.version -cne '0.2.1' -or $plan.publishRequested -ne $true) {
        throw 'The opt-in package plan did not preserve component, package identity, and version.'
    }
    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.2 -ArtifactDirectory $validDirectory -Publish -PlanOnly
    } 'does not match' 'Release tag selection'

    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $validDirectory -Publish
    } 'NUGET_API_KEY' 'Missing NuGet credential'

    $env:NUGET_API_KEY = 'test-only-not-a-real-key'
    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $validDirectory -Publish
    } 'protected GitHub Actions workflow' 'Manual publisher guard'
    Remove-Item Env:NUGET_API_KEY -ErrorAction SilentlyContinue

    $wrongIdDirectory = Join-Path $testRoot 'wrong-id'
    New-TestArtifact -Directory $wrongIdDirectory -NuspecId 'LANCommander.Wrong'
    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $wrongIdDirectory -Publish -PlanOnly
    } 'identity/version mismatch' 'Package identity validation'

    $wrongVersionDirectory = Join-Path $testRoot 'wrong-version'
    New-TestArtifact -Directory $wrongVersionDirectory -NuspecVersion '9.9.9'
    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $wrongVersionDirectory -Publish -PlanOnly
    } 'identity/version mismatch' 'Package version validation'

    $wrongTypesDirectory = Join-Path $testRoot 'wrong-types'
    New-TestArtifact -Directory $wrongTypesDirectory -Types @('LANCommander.Plugin')
    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $wrongTypesDirectory -Publish -PlanOnly
    } 'Package types' 'Package type validation'

    $wrongChecksumDirectory = Join-Path $testRoot 'wrong-checksum'
    New-TestArtifact -Directory $wrongChecksumDirectory
    Set-Content -LiteralPath (Join-Path $wrongChecksumDirectory 'LANCommander.Mcp.0.2.1.nupkg.sha256') -Value 'bad checksum'
    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $wrongChecksumDirectory -Publish -PlanOnly
    } 'checksum sidecar' 'Artifact integrity validation'

    $launcherDirectory = Join-Path $testRoot 'launcher'
    New-Item -ItemType Directory -Path $launcherDirectory | Out-Null
    Assert-Fails {
        & $publisher -Plugin Launcher.MQTT -Tag launcher-mqtt-v0.2.0 -ArtifactDirectory $launcherDirectory -Publish -PlanOnly
    } 'not eligible for NuGet publication' 'ZIP ineligibility'

    $hash = ('a' * 64)
    if ((Resolve-NuGetPublicationDisposition -ExpectedHash $hash -PublishedHash $null) -cne 'publish') {
        throw 'A missing NuGet version should be published.'
    }
    if ((Resolve-NuGetPublicationDisposition -ExpectedHash $hash -PublishedHash $hash) -cne 'already-published') {
        throw 'An identical published package should be idempotently accepted.'
    }
    Assert-Fails {
        Resolve-NuGetPublicationDisposition -ExpectedHash $hash -PublishedHash ('b' * 64)
    } 'different bytes' 'Immutable NuGet version check'

    Write-Output 'NuGet publication tests passed.'
}
finally {
    if ($null -eq $originalApiKey) {
        Remove-Item Env:NUGET_API_KEY -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_API_KEY = $originalApiKey
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
