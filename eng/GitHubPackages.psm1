function Get-GitHubNuGetPackageEntries {
    param([Parameter(Mandatory)][string] $PackagePath)

    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::OpenRead($PackagePath)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if ($entries.ContainsKey($entry.FullName)) {
                throw "GitHub Packages NuGet archive contains a duplicate path: $($entry.FullName)"
            }

            $entryStream = $entry.Open()
            try {
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream))
                $entries.Add($entry.FullName, $hash)
            }
            finally {
                $entryStream.Dispose()
            }
        }
        return ,$entries
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

function Compare-GitHubNuGetPackagePayload {
    param(
        [Parameter(Mandatory)][string] $ExpectedPackagePath,
        [Parameter(Mandatory)][string] $PublishedPackagePath
    )

    $expectedEntries = Get-GitHubNuGetPackageEntries -PackagePath $ExpectedPackagePath
    $publishedEntries = Get-GitHubNuGetPackageEntries -PackagePath $PublishedPackagePath
    if ($expectedEntries.Count -ne $publishedEntries.Count) {
        throw 'GitHub Packages version contains a different set of package entries.'
    }

    foreach ($path in $expectedEntries.Keys) {
        if (-not $publishedEntries.ContainsKey($path) -or
            $expectedEntries[$path] -cne $publishedEntries[$path]) {
            throw "GitHub Packages version differs from the release asset at '$path'."
        }
    }
}

function Get-GitHubNuGetPackageBaseAddress {
    param([Parameter(Mandatory)][object] $ServiceIndex)

    $resources = @($ServiceIndex.resources | Where-Object {
        $types = @($_.'@type')
        @($types | Where-Object { [string]$_ -match '^PackageBaseAddress/' }).Count -gt 0
    })
    if ($resources.Count -ne 1) {
        throw 'GitHub Packages service index must expose exactly one NuGet flat-container resource.'
    }

    $baseAddress = [Uri][string]$resources[0].'@id'
    if (-not $baseAddress.IsAbsoluteUri -or $baseAddress.Scheme -cne 'https' -or
        $baseAddress.Host -cne 'nuget.pkg.github.com') {
        throw 'GitHub Packages service index returned an unexpected package download endpoint.'
    }
    return $baseAddress.AbsoluteUri.TrimEnd('/')
}

function New-GitHubNuGetConfigFile {
    param(
        [Parameter(Mandatory)][string] $Owner,
        [Parameter(Mandatory)][string] $Token,
        [Parameter(Mandatory)][string] $FeedUrl,
        [Parameter(Mandatory)][string] $Path
    )

    $document = [System.Xml.XmlDocument]::new()
    $document.AppendChild($document.CreateXmlDeclaration('1.0', 'utf-8', $null)) | Out-Null
    $configuration = $document.CreateElement('configuration')
    $document.AppendChild($configuration) | Out-Null

    $sources = $document.CreateElement('packageSources')
    $configuration.AppendChild($sources) | Out-Null
    $sources.AppendChild($document.CreateElement('clear')) | Out-Null
    $source = $document.CreateElement('add')
    $source.SetAttribute('key', 'github')
    $source.SetAttribute('value', $FeedUrl)
    $sources.AppendChild($source) | Out-Null

    $credentials = $document.CreateElement('packageSourceCredentials')
    $configuration.AppendChild($credentials) | Out-Null
    $github = $document.CreateElement('github')
    $credentials.AppendChild($github) | Out-Null
    foreach ($credential in @(
        @{ key = 'Username'; value = $Owner },
        @{ key = 'ClearTextPassword'; value = $Token }
    )) {
        $entry = $document.CreateElement('add')
        $entry.SetAttribute('key', $credential.key)
        $entry.SetAttribute('value', $credential.value)
        $github.AppendChild($entry) | Out-Null
    }

    $document.Save($Path)
    return $Path
}

Export-ModuleMember -Function Compare-GitHubNuGetPackagePayload, Get-GitHubNuGetPackageBaseAddress, New-GitHubNuGetConfigFile
