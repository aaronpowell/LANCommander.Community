$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'ReleaseDestinations.psm1') -Force

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

$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release-plugin.yml') -Raw
$callerWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release.yml') -Raw
$nugetKeyJob = [regex]::Match($releaseWorkflow, '(?ms)^  nuget-key:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
$buildJob = [regex]::Match($releaseWorkflow, '(?ms)^  build:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
if ($releaseWorkflow -notmatch 'workflow_call:' -or
    $releaseWorkflow -notmatch 'nuget_api_key:\s*\r?\n\s+required:\s+false' -or
    $nugetKeyJob -notmatch 'NUGET_API_KEY:\s+\$\{\{\s*secrets\.nuget_api_key\s*\}\}' -or
    $nugetKeyJob -notmatch 'available=true' -or $nugetKeyJob -notmatch 'available=false' -or
    $buildJob -match 'secrets\.nuget_api_key|NUGET_API_KEY:\s+' -or
    $buildJob -notmatch 'NUGET_API_KEY_AVAILABLE:\s+\$\{\{\s*needs\.nuget-key\.outputs\.available\s*\}\}' -or
    $releaseWorkflow -notmatch "if:\s+needs\.build\.outputs\.github_packages_eligible == 'true'" -or
    $releaseWorkflow -notmatch "if:\s+needs\.build\.outputs\.nuget_org_eligible == 'true' && needs\.build\.outputs\.nuget_api_key_available == 'true'" -or
    $releaseWorkflow -notmatch "if:\s+needs\.build\.outputs\.nuget_org_eligible == 'true' && needs\.build\.outputs\.nuget_api_key_available != 'true'" -or
    $releaseWorkflow -notmatch 'artifact_name:\s+\$\{\{\s*steps\.destinations\.outputs\.artifact_name\s*\}\}' -or
    $releaseWorkflow -notmatch 'name:\s+\$\{\{\s*needs\.build\.outputs\.artifact_name\s*\}\}' -or
    $releaseWorkflow -notmatch 'RELEASE_ARTIFACT_NAME:\s+plugin-release-\$\{\{\s*inputs\.plugin\s*\}\}-\$\{\{\s*github\.run_id\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}' -or
    $releaseWorkflow -match '^\s*environment:\s*$') {
    throw 'The tag release must share one validated artifact and automatically gate feeds on registry eligibility and token availability.'
}
foreach ($job in @('publish-github-packages', 'publish-nuget-org')) {
    $body = [regex]::Match($releaseWorkflow, "(?ms)^  ${job}:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)").Groups['body'].Value
    if (-not $body -or $body -notmatch 'actions/download-artifact@' -or $body -match 'dotnet pack|Validate-Plugin\.ps1') {
        throw "$job must promote the build artifact without rebuilding."
    }
}
$githubJob = [regex]::Match($releaseWorkflow, '(?ms)^  publish-github-packages:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
if ($githubJob -notmatch 'packages:\s+write' -or $githubJob -notmatch "contents:\s+read" -or $githubJob -match 'NUGET_API_KEY') {
    throw 'GitHub Packages credentials must be limited to the package job and must not depend on a NuGet.org key.'
}
$nugetJob = [regex]::Match($releaseWorkflow, '(?ms)^  publish-nuget-org:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:|\z)').Groups['body'].Value
if ($nugetJob -notmatch 'NUGET_API_KEY:\s+\$\{\{\s*secrets\.nuget_api_key\s*\}\}' -or $nugetJob -match 'packages:\s+write|environment:') {
    throw 'NuGet.org must use the optional API key only in its package publishing job without an extra environment gate.'
}
if ($callerWorkflow -notmatch 'nuget_api_key:\s+\$\{\{\s*secrets\.NUGET_API_KEY\s*\}\}' -or
    $callerWorkflow -notmatch 'server-quake3:' -or
    $releaseWorkflow -match '(?s)publish-nuget-org:.*?NUGET_API_KEY.*?publish-github-packages:') {
    throw 'The tag caller must pass the optional key to the reusable workflow without coupling the feeds.'
}
if ((Test-Path -LiteralPath (Join-Path $repositoryRoot '.github/workflows/publish-nuget.yml')) -or
    (Test-Path -LiteralPath (Join-Path $repositoryRoot '.github/workflows/publish-github-packages.yml'))) {
    throw 'Separate manual feed publication workflows must not remain.'
}

$serverWithoutKey = Get-ReleaseDestinationPlan -Plugin Server.Mcp -NuGetApiKeyAvailable $false
if (-not $serverWithoutKey.publishToGitHubPackages -or $serverWithoutKey.publishToNuGetOrg -or
    $serverWithoutKey.nugetOrgSkipReason -notmatch 'intentionally skipped') {
    throw 'Server releases must always select GitHub Packages and report the missing-key NuGet.org skip.'
}
$serverWithKey = Get-ReleaseDestinationPlan -Plugin Server.RecompCatalog -NuGetApiKeyAvailable $true
if (-not $serverWithKey.publishToGitHubPackages -or -not $serverWithKey.publishToNuGetOrg) {
    throw 'A server release with a NuGet.org key must target both package feeds.'
}
$quakeWithKey = Get-ReleaseDestinationPlan -Plugin Server.Quake3 -NuGetApiKeyAvailable $true
if (-not $quakeWithKey.publishToGitHubPackages -or -not $quakeWithKey.publishToNuGetOrg) {
    throw 'Quake3 nupkg must remain eligible for both package feeds.'
}
$launcherWithKey = Get-ReleaseDestinationPlan -Plugin Launcher.MQTT -NuGetApiKeyAvailable $true
if ($launcherWithKey.artifactFormat -cne 'zip' -or
    $launcherWithKey.publishToGitHubPackages -or
    $launcherWithKey.publishToNuGetOrg) {
    throw 'Launcher ZIP releases must not select either NuGet feed, even when a NuGet.org key exists.'
}
Assert-Fails {
    Get-ReleaseDestinationPlan -Plugin Server.Unknown -NuGetApiKeyAvailable $true
} "Unknown release component 'Server.Unknown'." 'Unknown release component'

Write-Output 'Automatic release destination selection tests passed.'
