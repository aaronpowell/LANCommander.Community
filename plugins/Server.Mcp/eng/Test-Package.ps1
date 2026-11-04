param(
    [Parameter(Mandatory)]
    [string] $PackagePath,
    [Parameter(Mandatory)]
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $PackagePath
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $assemblyEntry = $archive.GetEntry('lib/net10.0/LANCommander.Community.Server.Mcp.dll')
    if (-not $assemblyEntry) { throw 'Missing LANCommander.Community.Server.Mcp plugin assembly.' }
    $temporaryAssembly = Join-Path ([IO.Path]::GetTempPath()) "$([Guid]::NewGuid().ToString('N')).dll"
    try {
        $source = $assemblyEntry.Open()
        $destination = [IO.File]::Create($temporaryAssembly)
        try { $source.CopyTo($destination) } finally { $destination.Dispose(); $source.Dispose() }
        $actualAssemblyName = [Reflection.AssemblyName]::GetAssemblyName($temporaryAssembly).Name
        if ($actualAssemblyName -cne 'LANCommander.Community.Server.Mcp') {
            throw "Assembly simple name '$actualAssemblyName' does not match the project name."
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryAssembly -Force -ErrorAction SilentlyContinue
    }

    $files = @($archive.Entries | ForEach-Object { $_.FullName })
    $expectedDlls = @(
        'LANCommander.Community.Server.Mcp.dll',
        'Microsoft.Extensions.AI.Abstractions.dll',
        'ModelContextProtocol.AspNetCore.dll',
        'ModelContextProtocol.Core.dll',
        'ModelContextProtocol.dll'
    ) | Sort-Object
    $actualDlls = @($files | Where-Object { $_ -like '*.dll' } | ForEach-Object {
        if (-not $_.StartsWith('lib/net10.0/')) {
            throw "Assembly outside plugin folder: $_"
        }
        $_.Substring('lib/net10.0/'.Length)
    }) | Sort-Object
    if (Compare-Object $expectedDlls $actualDlls) {
        throw "Unexpected or missing assemblies: $($actualDlls -join ', ')"
    }

    foreach ($required in @(
        'LANCommander.Community.Server.Mcp.deps.json',
        'LANCommander.Community.Server.Mcp.runtimeconfig.json',
        'LICENSE',
        'THIRD-PARTY-NOTICES.txt',
        'licenses/MCP.txt',
        'licenses/Microsoft.Extensions.txt'
    )) {
        if ("lib/net10.0/$required" -notin $files) {
            throw "Missing plugin file: $required"
        }
    }

    $reader = [System.IO.StreamReader]::new($archive.GetEntry('lib/net10.0/LANCommander.Community.Server.Mcp.deps.json').Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
    foreach ($target in $manifest.targets.Values) {
        foreach ($library in $target.GetEnumerator()) {
            foreach ($kind in @('runtime', 'native', 'runtimeTargets', 'resources')) {
                if (-not $library.Value.ContainsKey($kind)) {
                    continue
                }
                foreach ($asset in $library.Value[$kind].Keys) {
                    if ($asset.EndsWith('/_._')) {
                        continue
                    }
                    $candidates = @("lib/net10.0/$asset")
                    if ($kind -eq 'runtime') {
                        $candidates += "lib/net10.0/$(($asset -split '/')[-1])"
                    }
                    if (-not ($candidates | Where-Object { $_ -in $files })) {
                        throw "Dependency manifest references unpackaged $kind asset '$asset' from '$($library.Key)'."
                    }
                }
            }
        }
    }

    $reader = [System.IO.StreamReader]::new($archive.GetEntry('LANCommander.Community.Server.Mcp.nuspec').Open())
    try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($nuspec.package.metadata.id -cne 'LANCommander.Community.Server.Mcp' -or
        $nuspec.package.metadata.version -cne $ExpectedVersion) {
        throw "Package identity/version mismatch: $($nuspec.package.metadata.id) $($nuspec.package.metadata.version); expected LANCommander.Community.Server.Mcp $ExpectedVersion."
    }
    $types = @($nuspec.package.metadata.packageTypes.packageType | ForEach-Object { $_.name })
    if ('LANCommander.Plugin' -notin $types -or 'LANCommander.Plugin.Server' -notin $types) {
        throw 'Package must declare both LANCommander plugin package types.'
    }
    if ($nuspec.package.metadata.dependencies) {
        throw 'Plugin package must bundle private dependencies, not request NuGet dependency installation.'
    }
    Write-Output "Verified installable plugin package: $($package.Name)"
}
finally {
    $archive.Dispose()
}
