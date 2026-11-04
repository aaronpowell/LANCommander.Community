function Get-NuGetPackageEntries {
    param([Parameter(Mandatory)][string] $PackagePath)

    $stream = [IO.File]::OpenRead($PackagePath)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if ($entries.ContainsKey($entry.FullName)) {
                throw "NuGet package contains a duplicate archive path: $($entry.FullName)"
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

function Assert-NuGetRepositorySignature {
    param(
        [Parameter(Mandatory)][string] $PackagePath,
        [Parameter(Mandatory)][int] $VerificationExitCode,
        [Parameter(Mandatory)][string] $VerificationOutput
    )

    if ($VerificationExitCode -ne 0) {
        throw "NuGet.org package signature verification failed: $VerificationOutput"
    }
    if ($VerificationOutput -notmatch '(?im)(?:signature\s+type|type\s+of\s+signature)\s*:\s*repository\b' -or
        $VerificationOutput -notmatch '(?im)service\s+index(?:\s+url)?\s*:\s*https://api\.nuget\.org/v3/index\.json\b') {
        throw 'Published package is not verified as repository-signed by NuGet.org.'
    }

    $entries = Get-NuGetPackageEntries -PackagePath $PackagePath
    $signatureCount = @($entries.Keys | Where-Object { $_ -ceq '.signature.p7s' }).Count
    if ($signatureCount -ne 1) {
        throw 'Verified NuGet.org package must contain exactly one root .signature.p7s entry.'
    }
}

function Compare-NuGetPackagePayload {
    param(
        [Parameter(Mandatory)][string] $ExpectedPackagePath,
        [Parameter(Mandatory)][string] $PublishedPackagePath
    )

    $expectedEntries = Get-NuGetPackageEntries -PackagePath $ExpectedPackagePath
    $publishedEntries = Get-NuGetPackageEntries -PackagePath $PublishedPackagePath
    $expectedPaths = @($expectedEntries.Keys | Where-Object { $_ -cne '.signature.p7s' })
    $publishedPaths = @($publishedEntries.Keys | Where-Object { $_ -cne '.signature.p7s' })

    if ($expectedPaths.Count -ne $publishedPaths.Count) {
        throw 'NuGet.org package payload has a different set of archive entries.'
    }
    foreach ($path in $expectedPaths) {
        if (-not $publishedEntries.ContainsKey($path) -or
            $expectedEntries[$path] -cne $publishedEntries[$path]) {
            throw "NuGet.org package payload differs at archive path '$path'."
        }
    }
}

Export-ModuleMember -Function Assert-NuGetRepositorySignature, Compare-NuGetPackagePayload
