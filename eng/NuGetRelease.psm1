function Resolve-NuGetPublicationDisposition {
    param(
        [Parameter(Mandatory)][string] $ExpectedHash,
        [AllowNull()][string] $PublishedHash
    )

    if ([string]::IsNullOrWhiteSpace($PublishedHash)) {
        return 'publish'
    }
    if ($PublishedHash -cne $ExpectedHash) {
        throw 'NuGet.org already contains this package version with different bytes; immutable package content will not be replaced.'
    }
    return 'already-published'
}

Export-ModuleMember -Function Resolve-NuGetPublicationDisposition
