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
        [string] $PackageId = 'LANCommander.Community.Server.Mcp',
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
        [string] $PackageId = 'LANCommander.Community.Server.Mcp',
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
        minimumHostVersion = '2.2.0-beta.3'
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
    $releasePluginWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release-plugin.yml') -Raw
    $nugetJob = [regex]::Match($releasePluginWorkflow, '(?ms)^  publish-nuget-org:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
    $skipJob = [regex]::Match($releasePluginWorkflow, '(?ms)^  nuget-org-skipped:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
    if ($releasePluginWorkflow -notmatch "if:\s+needs\.build\.outputs\.nuget_org_eligible == 'true' && needs\.build\.outputs\.nuget_api_key_available == 'true'" -or
        $nugetJob -notmatch 'NUGET_API_KEY:\s+\$\{\{\s*secrets\.nuget_api_key\s*\}\}' -or
        $nugetJob -notmatch 'Publish-NuGetPackage\.ps1.*-Publish' -or
        $nugetJob -notmatch 'actions/download-artifact@' -or
        $nugetJob -match 'environment:|packages:\s+write|dotnet pack|Validate-Plugin\.ps1' -or
        $skipJob -notmatch 'NUGET_API_KEY repository secret is not configured') {
        throw 'NuGet.org must be automatically selected only for eligible packages with a configured API key, or report its intentional skip.'
    }
    if (Test-Path -LiteralPath (Join-Path $repositoryRoot '.github/workflows/publish-nuget.yml')) {
        throw 'The separate manual NuGet.org workflow must not remain.'
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
    if ($plan.component -cne 'Server.Mcp' -or $plan.packageId -cne 'LANCommander.Community.Server.Mcp' -or
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
    } 'protected GitHub Actions workflow' 'Tag workflow guard'
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
    Set-Content -LiteralPath (Join-Path $wrongChecksumDirectory 'LANCommander.Community.Server.Mcp.0.2.1.nupkg.sha256') -Value 'bad checksum'
    Assert-Fails {
        & $publisher -Plugin Server.Mcp -Tag server-mcp-v0.2.1 -ArtifactDirectory $wrongChecksumDirectory -Publish -PlanOnly
    } 'checksum sidecar' 'Artifact integrity validation'

    $launcherDirectory = Join-Path $testRoot 'launcher'
    New-Item -ItemType Directory -Path $launcherDirectory | Out-Null
    Assert-Fails {
        & $publisher -Plugin Launcher.MQTT -Tag launcher-mqtt-v0.2.0 -ArtifactDirectory $launcherDirectory -Publish -PlanOnly
    } 'not eligible for NuGet publication' 'ZIP ineligibility'

    $expectedPackagePath = Join-Path $validDirectory 'LANCommander.Community.Server.Mcp.0.2.1.nupkg'
    $signedPackagePath = Join-Path $testRoot 'repository-signed.nupkg'
    New-RepositorySignedTestPackage -SourcePath $expectedPackagePath -DestinationPath $signedPackagePath
    $validVerificationOutput = @'
Package name: LANCommander.Community.Server.Mcp
Signature type: Repository
Service index: https://api.nuget.org/v3/index.json
'@
    Assert-NuGetRepositorySignature -PackagePath $signedPackagePath -VerificationExitCode 0 -VerificationOutput $validVerificationOutput
    Compare-NuGetPackagePayload -ExpectedPackagePath $expectedPackagePath -PublishedPackagePath $signedPackagePath

    $changedNuspecPath = Join-Path $testRoot 'changed-nuspec.nupkg'
    New-TestPackage -Path $changedNuspecPath -PackageId 'LANCommander.Community.Server.Mcp' -Version '0.2.1' -Tags @('changed')
    $changedNuspecSignedPath = Join-Path $testRoot 'changed-nuspec-signed.nupkg'
    New-RepositorySignedTestPackage -SourcePath $changedNuspecPath -DestinationPath $changedNuspecSignedPath
    Assert-Fails {
        Compare-NuGetPackagePayload -ExpectedPackagePath $expectedPackagePath -PublishedPackagePath $changedNuspecSignedPath
    } "archive path 'LANCommander.Community.Server.Mcp.nuspec'" 'Changed nuspec retry'

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
