#requires -Version 5.1
<#
.SYNOPSIS
    Builds the Noctaxis Windows release package. See docs/releasing.md.

.DESCRIPTION
    Publishes Noctaxis self-contained (no .NET install needed), multi-file and untrimmed, checks the
    result and writes Noctaxis-<version>-<rid>.zip: a folder to extract and run. Nothing is signed,
    uploaded or published. NoctaxisBuildNumber (environment) sets Z of the X.Y.Z version.
    Runs in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
    powershell -File scripts\package.ps1
    powershell -File scripts\package.ps1 -RuntimeIdentifier win-arm64 -OutputDirectory C:\out
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\packages' }
$project = Join-Path $repo 'Noctaxis.Desktop\Noctaxis.Desktop.csproj'

$version = (& dotnet msbuild $project -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'Could not read the Noctaxis version.' }
$name = "Noctaxis-$version-$RuntimeIdentifier"
$work = Join-Path ([IO.Path]::GetTempPath()) ("noctaxis-package-" + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $work $name

try {
    Write-Host "Publishing Noctaxis $version for $RuntimeIdentifier"
    & dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false `
        --artifacts-path (Join-Path $work 'build') -o $root
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    # The package must run on a machine with nothing else installed: the app, its runtime and its
    # native libraries, and the licence texts that go with them.
    $required = 'Noctaxis.Desktop.exe', 'Noctaxis.Desktop.dll', 'Noctaxis.Core.dll', 'libSkiaSharp.dll',
        'libHarfBuzzSharp.dll', 'av_libglesv2.dll', 'coreclr.dll', 'hostfxr.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
        'AWOO-SUPPORTER-SYSTEM.md', 'ThirdParty\Microsoft-NOTICES.txt', 'ThirdParty\DotNet-LICENSE.txt'
    # OpenCV's native runtime is Windows x64 only; elsewhere Noctaxis uses its managed image path.
    if ($RuntimeIdentifier -eq 'win-x64') { $required += 'OpenCvSharpExtern.dll' }
    $missing = $required | Where-Object { -not (Test-Path (Join-Path $root $_)) }
    if ($missing) { throw "$name is missing: $($missing -join ', ')" }

    # Ship the operative project terms and the complete retained notice collection, not stale copies.
    if ((Get-Content -LiteralPath (Join-Path $root 'LICENSE') -TotalCount 1) -ne '# Awoo Licence v2.0') {
        throw 'The package does not contain Awoo Licence v2.0.'
    }
    foreach ($relative in 'LICENSE', 'AWOO-SUPPORTER-SYSTEM.md', 'THIRD_PARTY_NOTICES.md') {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $repo $relative) -Algorithm SHA256).Hash
        $packageHash = (Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash
        if ($sourceHash -ne $packageHash) { throw "Stale package licensing file: $relative" }
    }
    $noticeRoot = Join-Path $repo 'Noctaxis.Desktop\ThirdParty'
    foreach ($notice in Get-ChildItem -LiteralPath $noticeRoot -Recurse -File) {
        $relative = 'ThirdParty\' + $notice.FullName.Substring($noticeRoot.Length + 1)
        $packaged = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $packaged -PathType Leaf) -or
            (Get-FileHash -LiteralPath $notice.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $packaged -Algorithm SHA256).Hash) {
            throw "Missing or changed third-party notice: $relative"
        }
    }
    $catalogueSource = Join-Path $repo 'Noctaxis.Core\Data\OpenNGC-LICENSE.txt'
    $cataloguePackaged = Join-Path $root 'ThirdParty\OpenNGC-LICENSE.txt'
    if (-not (Test-Path -LiteralPath $cataloguePackaged -PathType Leaf) -or
        (Get-FileHash -LiteralPath $catalogueSource -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $cataloguePackaged -Algorithm SHA256).Hash) {
        throw 'Missing or changed OpenNGC licence.'
    }
    if (Get-ChildItem $root -Filter 'lib*.pdb') { throw 'Native debug symbols were published.' }
    Copy-Item (Join-Path $repo 'packaging\windows\README.txt') (Join-Path $root 'README.txt')

    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $zip = Join-Path $OutputDirectory "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    # Entry by entry, so every name uses '/' as the zip format requires: Windows PowerShell's
    # CreateFromDirectory writes '\'.
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $root -Recurse -File) {
            $entry = "$name/" + $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry,
                [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
    Write-Host "Wrote $zip"
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
}
