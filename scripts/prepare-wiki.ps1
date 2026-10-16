#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $Destination,
    [string] $RepositoryUrl = 'https://github.com/CallyyllaC/Noctaxis',
    [string] $RepositoryRef = 'main',
    [switch] $ValidateOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'docs/user-guide'
# Public screenshots: the repository's media folder (also read by awoo.ltd), linked from the guide pages.
$images = Join-Path $repo 'images/wiki'
$imageLink = '../../images/wiki/'
$pages = [ordered]@{
    'README.md' = 'Home'; 'about.md' = 'About'; 'getting-started.md' = 'Getting-Started'
    'locations.md' = 'Locations'; 'planner.md' = 'Planner'; 'settings.md' = 'Settings'
}
if ($RepositoryUrl -notmatch '^https://github\.com/([^/]+)/([^/]+)/?$') { throw 'RepositoryUrl must identify a GitHub repository.' }
$owner = $Matches[1]
$repository = $Matches[2]
$RepositoryUrl = $RepositoryUrl.TrimEnd('/')
$rawWiki = "https://raw.githubusercontent.com/wiki/$owner/$repository"
$errors = [Collections.Generic.List[string]]::new()
$contents = @{}
$anchors = @{}
foreach ($name in $pages.Keys) {
    $body = [IO.File]::ReadAllText((Join-Path $source $name))
    $contents[$name] = $body
    $seen = @{}
    $anchors[$name] = @([regex]::Matches($body, '(?m)^#{1,6}\s+(.+)$') | ForEach-Object {
        $slug = $_.Groups[1].Value.Trim().ToLowerInvariant() -replace '[^\p{L}\p{N}_\- ]', '' -replace ' ', '-'
        if ($seen.ContainsKey($slug)) { $seen[$slug]++; "$slug-$($seen[$slug])" } else { $seen[$slug] = 0; $slug }
    })
}
foreach ($name in $pages.Keys) {
    foreach ($match in [regex]::Matches($contents[$name], '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^[a-z][a-z0-9+.-]*:') { continue }
        $pieces = $target.Split([char[]]'#', 2)
        $path = $pieces[0]
        $file = if ($path) { Join-Path $source $path } else { Join-Path $source $name }
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { $errors.Add("$name links to missing file: $target"); continue }
        if ($path -match '^[^/]+\.md$' -and !$pages.Contains($path)) { $errors.Add("$name links to a page outside the agreed wiki: $path") }
        if ($pieces.Count -eq 2 -and (!$path -or $pages.Contains($path))) {
            $anchorPage = if ($path) { $path } else { $name }
            if ($pieces[1] -cnotin $anchors[$anchorPage]) { $errors.Add("$name links to missing heading: $target") }
        }
    }
}
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
if ($ValidateOnly) { Write-Output 'Validated six wiki source pages, local image/file links and cross-page headings.'; return }
if (!$Destination) { $Destination = Join-Path $repo ('artifacts/wiki/prepared/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')) }
$Destination = [IO.Path]::GetFullPath($Destination)
if ((Test-Path -LiteralPath $Destination) -and @(Get-ChildItem -LiteralPath $Destination -Force).Count) {
    throw 'Preparation requires a new or empty directory. It never synchronises over an existing wiki checkout.'
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($name in $pages.Keys) {
    $body = [regex]::Replace($contents[$name], '\]\(([^)]+)\)', [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $target = $match.Groups[1].Value
        $pieces = $target.Split([char[]]'#', 2)
        $anchor = if ($pieces.Count -eq 2) { '#' + $pieces[1] } else { '' }
        if ($pages.Contains($pieces[0])) { return '](' + $pages[$pieces[0]] + $anchor + ')' }
        # Screenshots live in the repository's images/wiki and are published in the wiki's own images folder.
        if ($pieces[0].StartsWith($imageLink)) { return '](' + $rawWiki + '/images/' + $pieces[0].Substring($imageLink.Length) + ')' }
        if ($pieces[0].StartsWith('../../')) { return '](' + $RepositoryUrl + '/blob/' +
            [Uri]::EscapeDataString($RepositoryRef) + '/' + $pieces[0].Substring(6) + $anchor + ')' }
        return $match.Value
    })
    [IO.File]::WriteAllText((Join-Path $Destination ($pages[$name] + '.md')), $body, [Text.UTF8Encoding]::new($false))
}
$sidebar = @'
[Home](Home)

[About](About)

[Getting Started](Getting-Started)

Documentation

- [Locations](Locations)
- [Planner](Planner)
- [Settings](Settings)
'@
[IO.File]::WriteAllText((Join-Path $Destination '_Sidebar.md'), $sidebar, [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $images) {
    New-Item -ItemType Directory -Path (Join-Path $Destination 'images') -Force | Out-Null
    foreach ($image in Get-ChildItem -LiteralPath $images -Filter '*.png') {
        Copy-Item -LiteralPath $image.FullName -Destination (Join-Path $Destination 'images')
    }
}
Write-Output "Prepared local wiki files: $Destination"
Write-Output 'Nothing was committed, pushed or published.'
