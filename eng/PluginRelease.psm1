function Get-PluginRegistry {
    $path = Join-Path (Split-Path $PSScriptRoot -Parent) 'eng\plugins.json'
    $registry = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($registry.schemaVersion -ne 1 -or @($registry.plugins).Count -ne 4) {
        throw "Unsupported or incomplete plugin registry: $path"
    }

    $keys = @($registry.plugins | ForEach-Object key)
    if (@($keys | Select-Object -Unique).Count -ne $keys.Count) {
        throw 'Plugin registry contains duplicate keys.'
    }
    return $registry.plugins
}

function Test-PluginSemVer {
    param([Parameter(Mandatory)][string] $Version)

    $pattern = '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
    $match = [regex]::Match($Version, $pattern)
    if (-not $match.Success) {
        return $false
    }

    if ($match.Groups['pre'].Success) {
        foreach ($identifier in $match.Groups['pre'].Value.Split('.')) {
            if ($identifier -match '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier.StartsWith('0')) {
                return $false
            }
        }
    }

    return $true
}

function Get-PluginVersion {
    param([Parameter(Mandatory)] $Plugin)

    $repositoryRoot = Split-Path $PSScriptRoot -Parent
    $path = Join-Path $repositoryRoot $Plugin.versionFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing version file for $($Plugin.key): $path"
    }

    $version = (Get-Content -LiteralPath $path -Raw).Trim()
    if (-not (Test-PluginSemVer $version)) {
        throw "Invalid SemVer '$version' in $path."
    }
    return $version
}

function Assert-ReleaseWorkflowContext {
    param(
        [Parameter(Mandatory)][string] $Tag,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string] $Repository,
        [Parameter(Mandatory)][string] $ExpectedRepository,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string] $Actions,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string] $EventName,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string] $Ref
    )

    $tagPush = $EventName -ceq 'push' -and $Ref -ceq "refs/tags/$Tag"
    $mainRecovery = $EventName -ceq 'workflow_dispatch' -and $Ref -ceq 'refs/heads/main'
    if ($Actions -cne 'true' -or $Repository -cne $ExpectedRepository -or
        (-not $tagPush -and -not $mainRecovery)) {
        throw 'Publication must run through the protected GitHub Actions workflow for an exact component tag or main-branch recovery dispatch.'
    }
}

function Resolve-PluginSelection {
    [CmdletBinding()]
    param(
        [string[]] $Plugins,
        [string] $Tag
    )

    $registry = @(Get-PluginRegistry)
    if ($Tag) {
        $tagMatch = [regex]::Match($Tag, '^(?<prefix>[a-z0-9-]+)-v(?<version>.+)$')
        if (-not $tagMatch.Success -or -not (Test-PluginSemVer $tagMatch.Groups['version'].Value)) {
            throw "Release tag '$Tag' is not a component-qualified SemVer tag."
        }

        $component = @($registry | Where-Object tagPrefix -CEQ $tagMatch.Groups['prefix'].Value)
        if ($component.Count -ne 1) {
            throw "Unknown release tag component '$($tagMatch.Groups['prefix'].Value)'."
        }

        $version = $tagMatch.Groups['version'].Value
        $configuredVersion = Get-PluginVersion $component[0]
        if ($configuredVersion -cne $version) {
            throw "Tag version '$version' does not match $($component[0].versionFile) ($configuredVersion)."
        }
        if ($Plugins -and ($Plugins.Count -ne 1 -or $Plugins[0] -cne $component[0].key)) {
            throw "Tag '$Tag' selects $($component[0].key); it cannot be combined with a different plugin selection."
        }

        return @([pscustomobject]@{ key = $component[0].key; tag = $Tag; version = $version })
    }

    if (-not $Plugins -or $Plugins.Count -eq 0) {
        throw 'Specify one or more allowlisted -Plugins, or a component-qualified -Tag.'
    }

    $selected = [System.Collections.Generic.List[object]]::new()
    foreach ($key in $Plugins) {
        $component = @($registry | Where-Object key -CEQ $key)
        if ($component.Count -ne 1) {
            throw "Unknown plugin '$key'. Select only keys in eng/plugins.json."
        }
        if (@($selected | Where-Object key -CEQ $key).Count -gt 0) {
            throw "Plugin '$key' was selected more than once."
        }
        $selected.Add([pscustomobject]@{
            key = $component[0].key
            version = Get-PluginVersion $component[0]
        })
    }

    return @($selected.ToArray())
}

Export-ModuleMember -Function Assert-ReleaseWorkflowContext, Get-PluginRegistry, Get-PluginVersion, Resolve-PluginSelection, Test-PluginSemVer
