Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force

function Get-ReleaseDestinationPlan {
    param(
        [Parameter(Mandatory)][string] $Plugin,
        [Parameter(Mandatory)][bool] $NuGetApiKeyAvailable
    )

    $component = @(Get-PluginRegistry | Where-Object key -CEQ $Plugin)
    if ($component.Count -ne 1) {
        throw "Unknown release component '$Plugin'."
    }

    $component = $component[0]
    if ($component.githubPackagesEligible -isnot [bool] -or $component.nugetOrgEligible -isnot [bool]) {
        throw "$Plugin must explicitly declare each NuGet feed eligibility."
    }
    if (($component.githubPackagesEligible -or $component.nugetOrgEligible) -and $component.artifactFormat -cne 'nupkg') {
        throw "$Plugin cannot publish a non-NuGet artifact to a package feed."
    }

    [pscustomobject]@{
        component = $Plugin
        artifactFormat = $component.artifactFormat
        githubPackagesEligible = $component.githubPackagesEligible
        nugetOrgEligible = $component.nugetOrgEligible
        nugetApiKeyAvailable = $NuGetApiKeyAvailable
        publishToGitHubPackages = $component.githubPackagesEligible
        publishToNuGetOrg = $component.nugetOrgEligible -and $NuGetApiKeyAvailable
        nugetOrgSkipReason = if ($component.nugetOrgEligible -and -not $NuGetApiKeyAvailable) {
            'NUGET_API_KEY is not configured; NuGet.org publishing is intentionally skipped.'
        }
        elseif (-not $component.nugetOrgEligible) {
            "$Plugin artifact format '$($component.artifactFormat)' is not eligible for NuGet.org."
        }
        else {
            $null
        }
    }
}

Export-ModuleMember -Function Get-ReleaseDestinationPlan
