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

function New-RepositorySignedTestPackage {
    param(
        [Parameter(Mandatory)][string] $SourcePath,
        [Parameter(Mandatory)][string] $DestinationPath,
        [string] $PayloadChange,
        [switch] $DuplicatePayloadEntry
    )

    $sourceStream = [IO.File]::OpenRead($SourcePath)
    $sourceArchive = [System.IO.Compression.ZipArchive]::new($sourceStream, [System.IO.Compression.ZipArchiveMode]::Read)
    $destinationStream = [IO.File]::Create($DestinationPath)
    $destinationArchive = [System.IO.Compression.ZipArchive]::new($destinationStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($sourceEntry in $sourceArchive.Entries) {
            $destinationEntry = $destinationArchive.CreateEntry($sourceEntry.FullName)
            $entryInput = $sourceEntry.Open()
            $output = $destinationEntry.Open()
            try { $entryInput.CopyTo($output) } finally { $output.Dispose(); $entryInput.Dispose() }
        }
        if (-not [string]::IsNullOrEmpty($PayloadChange)) {
            $changedEntry = $destinationArchive.CreateEntry('lib/payload.txt')
            $writer = [IO.StreamWriter]::new($changedEntry.Open())
            try { $writer.Write($PayloadChange) } finally { $writer.Dispose() }
        }
        if ($DuplicatePayloadEntry) {
            $duplicateEntry = $destinationArchive.CreateEntry($sourceArchive.Entries[0].FullName)
            $writer = [IO.StreamWriter]::new($duplicateEntry.Open())
            try { $writer.Write('duplicate archive path') } finally { $writer.Dispose() }
        }
        $signatureEntry = $destinationArchive.CreateEntry('.signature.p7s')
        $signatureStream = $signatureEntry.Open()
        try {
            $signatureBytes = [Text.Encoding]::UTF8.GetBytes('synthetic repository signature fixture')
            $signatureStream.Write($signatureBytes, 0, $signatureBytes.Length)
        }
        finally {
            $signatureStream.Dispose()
        }
    }
    finally {
        $destinationArchive.Dispose()
        $destinationStream.Dispose()
        $sourceArchive.Dispose()
        $sourceStream.Dispose()
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
        $nugetWorkflow -notmatch 'Assert-NuGetEnvironmentProtection' -or
        $nugetWorkflow -notmatch 'environment_ready' -or
        $nugetWorkflow -notmatch 'NUGET_API_KEY:\s+\$\{\{\s*secrets\.NUGET_API_KEY\s*\}\}' -or
        $nugetWorkflow -notmatch 'Publish-NuGetPackage\.ps1.*-Publish') {
        throw 'NuGet publication must stay manual, environment-preflighted, gated, and explicitly opted in.'
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

    $expectedPackagePath = Join-Path $validDirectory 'LANCommander.Mcp.0.2.1.nupkg'
    $signedPackagePath = Join-Path $testRoot 'repository-signed.nupkg'
    New-RepositorySignedTestPackage -SourcePath $expectedPackagePath -DestinationPath $signedPackagePath
    $validVerificationOutput = @'
Package name: LANCommander.Mcp
Signature type: Repository
Service index: https://api.nuget.org/v3/index.json
'@
    Assert-NuGetRepositorySignature -PackagePath $signedPackagePath -VerificationExitCode 0 -VerificationOutput $validVerificationOutput
    Compare-NuGetPackagePayload -ExpectedPackagePath $expectedPackagePath -PublishedPackagePath $signedPackagePath

    $changedNuspecPath = Join-Path $testRoot 'changed-nuspec.nupkg'
    New-TestPackage -Path $changedNuspecPath -PackageId 'LANCommander.Mcp' -Version '0.2.1' -Tags @('changed')
    $changedNuspecSignedPath = Join-Path $testRoot 'changed-nuspec-signed.nupkg'
    New-RepositorySignedTestPackage -SourcePath $changedNuspecPath -DestinationPath $changedNuspecSignedPath
    Assert-Fails {
        Compare-NuGetPackagePayload -ExpectedPackagePath $expectedPackagePath -PublishedPackagePath $changedNuspecSignedPath
    } "archive path 'LANCommander.Mcp.nuspec'" 'Changed nuspec retry'

    $changedPayloadPath = Join-Path $testRoot 'changed-payload.nupkg'
    New-RepositorySignedTestPackage -SourcePath $expectedPackagePath -DestinationPath $changedPayloadPath -PayloadChange 'different payload'
    Assert-Fails {
        Compare-NuGetPackagePayload -ExpectedPackagePath $expectedPackagePath -PublishedPackagePath $changedPayloadPath
    } 'different set of archive entries' 'Additional package payload retry'

    $duplicateEntryPath = Join-Path $testRoot 'duplicate-entry.nupkg'
    New-RepositorySignedTestPackage -SourcePath $expectedPackagePath -DestinationPath $duplicateEntryPath -DuplicatePayloadEntry
    Assert-Fails {
        Compare-NuGetPackagePayload -ExpectedPackagePath $expectedPackagePath -PublishedPackagePath $duplicateEntryPath
    } 'duplicate archive path' 'Duplicate archive path retry'
    Assert-Fails {
        Assert-NuGetRepositorySignature -PackagePath $signedPackagePath -VerificationExitCode 1 -VerificationOutput 'signature invalid'
    } 'signature verification failed' 'Invalid NuGet signature'
    Assert-Fails {
        Assert-NuGetRepositorySignature -PackagePath $signedPackagePath -VerificationExitCode 0 -VerificationOutput 'Signature type: Repository'
    } 'not verified as repository-signed' 'Non-NuGet.org repository signature'

    $unsignedPackagePath = Join-Path $testRoot 'unsigned-published.nupkg'
    Copy-Item -LiteralPath $expectedPackagePath -Destination $unsignedPackagePath
    Assert-Fails {
        Assert-NuGetRepositorySignature -PackagePath $unsignedPackagePath -VerificationExitCode 0 -VerificationOutput $validVerificationOutput
    } 'exactly one root .signature.p7s' 'Missing repository signature entry'

    $environment = [pscustomobject]@{
        name = 'nuget.org'
        protection_rules = @([pscustomobject]@{
            type = 'required_reviewers'
            reviewers = @([pscustomobject]@{ type = 'User'; reviewer = [pscustomobject]@{ login = 'release-reviewer' } })
            prevent_self_review = $true
        })
        deployment_branch_policy = [pscustomobject]@{ custom_branch_policies = $true; protected_branches = $false }
    }
    $mainPolicy = [pscustomobject]@{ branch_policies = @([pscustomobject]@{ type = 'branch'; name = 'main' }) }
    Assert-NuGetEnvironmentProtection -Environment $environment -BranchPolicies $mainPolicy
    Assert-Fails {
        $unprotected = $environment | Select-Object *
        $unprotected.name = 'missing'
        Assert-NuGetEnvironmentProtection -Environment $unprotected -BranchPolicies $mainPolicy
    } 'existing nuget.org environment' 'Missing NuGet environment'
    Assert-Fails {
        $noReviewer = [pscustomobject]@{ name = 'nuget.org'; protection_rules = @(); deployment_branch_policy = $environment.deployment_branch_policy }
        Assert-NuGetEnvironmentProtection -Environment $noReviewer -BranchPolicies $mainPolicy
    } 'must require at least one reviewer' 'Unprotected NuGet environment'
    Assert-Fails {
        $otherPolicy = [pscustomobject]@{ branch_policies = @([pscustomobject]@{ type = 'branch'; name = '*' }) }
        Assert-NuGetEnvironmentProtection -Environment $environment -BranchPolicies $otherPolicy
    } 'allow deployments from main only' 'Broad NuGet branch policy'

    Write-Output 'NuGet publication tests passed. Repository-signature fixtures are synthetic and do not replace dotnet nuget verify.'
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
