[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'lancommander-source.json') -Raw | ConvertFrom-Json
$destinationPath = [IO.Path]::GetFullPath($Destination)

if (Test-Path -LiteralPath $destinationPath) {
    $existingCommit = (& git -C $destinationPath rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $existingCommit -cne $pin.commit) {
        throw "Existing host source at '$destinationPath' is not the pinned commit $($pin.commit); use a new destination."
    }
    $existingOrigin = (& git -C $destinationPath remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or $existingOrigin -cne $pin.repository) {
        throw "Existing host source at '$destinationPath' has an unexpected origin."
    }
    $existingChanges = @(& git -C $destinationPath status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $existingChanges.Count -gt 0) {
        throw "Existing host source at '$destinationPath' has local changes; use a clean pinned checkout."
    }
    Write-Output $destinationPath
    return
}

$parent = Split-Path -Parent $destinationPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
& git clone --filter=blob:none --no-checkout --depth 1 --branch $pin.tag $pin.repository $destinationPath
if ($LASTEXITCODE -ne 0) { throw 'Could not clone the pinned LANCommander source tag.' }
& git -C $destinationPath checkout --detach $pin.commit
if ($LASTEXITCODE -ne 0) { throw "Could not check out pinned LANCommander commit $($pin.commit)." }

$actualCommit = (& git -C $destinationPath rev-parse HEAD).Trim()
if ($actualCommit -cne $pin.commit) {
    throw "LANCommander source resolved to $actualCommit, expected $($pin.commit)."
}
$actualOrigin = (& git -C $destinationPath remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $actualOrigin -cne $pin.repository) {
    throw "LANCommander source origin '$actualOrigin' does not match $($pin.repository)."
}
foreach ($project in $pin.projects) {
    $projectFile = Join-Path (Join-Path $destinationPath $project) "$project.csproj"
    if (-not (Test-Path -LiteralPath $projectFile)) {
        throw "Pinned LANCommander source is missing required project '$project'."
    }
}

Write-Output $destinationPath
