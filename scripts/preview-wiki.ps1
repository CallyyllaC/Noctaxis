#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(0, 65535)] [int] $Port = 8765,
    [switch] $NoBrowser,
    [string] $RepositoryUrl = 'https://github.com/CallyyllaC/Noctaxis',
    [string] $RepositoryRef = 'main'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$prepare = Join-Path $PSScriptRoot 'prepare-wiki.ps1'
$images = Join-Path $repo 'images/wiki'
if ($RepositoryUrl -notmatch '^https://github\.com/([^/]+)/([^/]+)/?$') { throw 'RepositoryUrl must identify a GitHub repository.' }
$rawImages = "https://raw.githubusercontent.com/wiki/$($Matches[1])/$($Matches[2])/images/"
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('noctaxis-wiki-preview-' + [Guid]::NewGuid().ToString('N'))))
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$rendererProject = Join-Path $repo 'tools/Noctaxis.WikiMarkdown/Noctaxis.WikiMarkdown.csproj'
$rendererBuild = Join-Path $repo 'artifacts/wiki/markdown-build'
$renderer = Join-Path $rendererBuild 'bin/Noctaxis.WikiMarkdown/release/Noctaxis.WikiMarkdown.dll'

function Remove-PreviewDirectory([string] $Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -ne $temporaryRoot -and !$resolved.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a directory outside this preview run.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
function New-PreparedPreview {
    $destination = Join-Path $temporaryRoot ([Guid]::NewGuid().ToString('N'))
    & $prepare -Destination $destination -RepositoryUrl $RepositoryUrl -RepositoryRef $RepositoryRef | Out-Null
    & dotnet $renderer $destination
    if ($LASTEXITCODE -ne 0) { throw 'Wiki Markdown rendering failed.' }
    return $destination
}
function Convert-PreviewMarkdown([string] $Path, [string[]] $PageNames) {
    $html = [IO.File]::ReadAllText([IO.Path]::ChangeExtension($Path, 'html')).Replace($rawImages, '/images/')
    # The publication mapping stays authoritative; only local page and image URLs change.
    foreach ($name in $PageNames) {
        $html = [regex]::Replace($html, ('href="' + [regex]::Escape($name) + '(#[^"]*)?"'), ('href="/' + $name + '$1"'))
    }
    return $html
}
$style = @"
:root{color-scheme:light dark;--bg:#fff;--fg:#1f2328;--muted:#59636e;--border:#d1d9e0;--soft:#f6f8fa;--link:#0969da}
@media(prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#f0f6fc;--muted:#9198a1;--border:#3d444d;--soft:#151b23;--link:#4493f8}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:16px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}a{color:var(--link);text-decoration:none}a:hover{text-decoration:underline}
.topbar{border-bottom:1px solid var(--border);background:var(--soft);padding:16px 24px;display:flex;align-items:center;gap:14px}.topbar form{margin:0}.topbar button{font:inherit;font-size:12px;color:var(--fg);background:var(--bg);border:1px solid var(--border);border-radius:6px;padding:5px 10px;cursor:pointer}.topbar button:hover{background:var(--soft)}.topbar strong{font-size:20px}.topbar small{margin-left:auto;color:var(--muted)}
.layout{max-width:1320px;margin:auto;display:grid;grid-template-columns:minmax(0,1fr) 250px;gap:40px;padding:28px 32px}.page-name{font-size:28px;font-weight:400;border-bottom:1px solid var(--border);padding-bottom:12px;margin-bottom:24px}
.markdown-body{overflow-wrap:break-word}.markdown-body>*:first-child{margin-top:0}.markdown-body h1,.markdown-body h2{padding-bottom:.3em;border-bottom:1px solid var(--border)}.markdown-body h1{font-size:2em}.markdown-body h2{font-size:1.5em}.markdown-body h3{font-size:1.25em}.markdown-body h4{font-size:1em}.markdown-body h1,.markdown-body h2,.markdown-body h3,.markdown-body h4,.markdown-body h5,.markdown-body h6{position:relative;font-weight:600;line-height:1.25;margin-top:24px;margin-bottom:16px;scroll-margin-top:20px}
.markdown-body p,.markdown-body ul,.markdown-body ol,.markdown-body pre,.markdown-body table,.markdown-body blockquote{margin-top:0;margin-bottom:16px}.markdown-body li+li{margin-top:.25em}.markdown-body li>p{margin-top:16px}.markdown-body ul,.markdown-body ol{padding-left:2em}.markdown-body img{max-width:100%;height:auto;vertical-align:middle}.markdown-body table{border-collapse:collapse;display:block;width:max-content;max-width:100%;overflow:auto}.markdown-body th,.markdown-body td{padding:6px 13px;border:1px solid var(--border)}.markdown-body tr:nth-child(2n){background:var(--soft)}.markdown-body blockquote{padding:0 1em;color:var(--muted);border-left:.25em solid var(--border)}
.markdown-body code,.markdown-body pre{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:85%}.markdown-body code{padding:.2em .4em;background:var(--soft);border-radius:6px}.markdown-body pre{padding:16px;overflow:auto;background:var(--soft);border-radius:6px;line-height:1.45}.markdown-body pre code{padding:0;background:none;font-size:100%}.markdown-body hr{height:1px;border:0;background:var(--border);margin:24px 0}.heading-anchor{position:absolute;left:-20px;width:20px;opacity:0;font-size:16px;line-height:inherit}.markdown-body :is(h1,h2,h3,h4,h5,h6):hover .heading-anchor,.heading-anchor:focus{opacity:1}
.sidebar{font-size:14px}.sidebar h2{font-size:16px;margin-top:0}.wiki-navigation{border:1px solid var(--border);border-radius:6px;padding:16px}.wiki-navigation p{margin:0 0 12px}.wiki-navigation p:last-child{margin-bottom:0}.wiki-navigation ul{margin:0 0 12px;padding-left:20px}.wiki-navigation li{margin-bottom:8px}.wiki-navigation [aria-current=page]{font-weight:600;text-decoration:underline}.toc{margin-top:24px}.toc ul{list-style:none;padding:0}.toc li{margin:7px 0}.toc .depth-3{padding-left:14px}.notice{color:var(--muted);font-size:12px;border-top:1px solid var(--border);padding-top:12px;margin-top:24px}
@media(max-width:900px){.layout{grid-template-columns:minmax(0,1fr);padding:20px 24px;gap:24px}.sidebar{grid-row:1}.toc{display:none}.topbar{flex-wrap:wrap}.topbar small{margin-left:0}.page-name{font-size:24px}}
"@
function New-PreviewPage([string] $Name) {
    $prepared = New-PreparedPreview
    try {
        $names = @(Get-ChildItem -LiteralPath $prepared -Filter '*.md' | Where-Object Name -ne '_Sidebar.md' | ForEach-Object BaseName)
        if ($Name -cnotin $names) { return $null }
        $article = Convert-PreviewMarkdown (Join-Path $prepared ($Name + '.md')) $names
        $navigation = Convert-PreviewMarkdown (Join-Path $prepared '_Sidebar.md') $names
        $navigation = $navigation.Replace(('href="/' + $Name + '"'), ('aria-current="page" href="/' + $Name + '"'))
        $toc = foreach ($heading in [regex]::Matches($article, '<h([23]) id="([^"]+)">(.*?)</h\1>', 'Singleline')) {
            $label = [Net.WebUtility]::HtmlEncode([Net.WebUtility]::HtmlDecode(($heading.Groups[3].Value -replace '<[^>]+>', '')))
            '<li class="depth-' + $heading.Groups[1].Value + '"><a href="#' + $heading.Groups[2].Value + '">' + $label + '</a></li>'
        }
        $article = [regex]::Replace($article, '<h([1-6]) id="([^"]+)">', '<h$1 id="$2"><a class="heading-anchor" href="#$2" aria-label="Link to this heading">#</a>')
        $title = [Net.WebUtility]::HtmlEncode($Name.Replace('-', ' '))
        return @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>$title &middot; Noctaxis Wiki preview</title><style>$style</style></head>
<body><header class="topbar"><strong><a href="/Home">Noctaxis</a></strong><span>Wiki</span><form action="/__stop" method="post"><button type="submit">Stop preview</button></form><small>Local preview &middot; Refresh for Markdown edits &middot; Ctrl+C in the terminal to stop</small></header>
<div class="layout"><main><div class="page-name">$title</div><article class="markdown-body">$article</article></main><aside class="sidebar"><nav class="wiki-navigation" aria-label="Wiki pages"><h2>Pages</h2>$navigation</nav><nav class="toc" aria-label="On this page"><h2>On this page</h2><ul>$($toc -join '')</ul></nav><p class="notice">Markdig Markdown rendering and approximate GitHub styling. Repository documents and third-party links keep their publication URLs.</p></aside></div></body></html>
"@
    }
    finally { Remove-PreviewDirectory $prepared }
}
function Send-PreviewResponse($Stream, [int] $Status, [string] $ContentType, [byte[]] $Body, [bool] $HeadOnly = $false) {
    $reason = switch ($Status) { 200 {'OK'} 204 {'No Content'} 404 {'Not Found'} 403 {'Forbidden'} 405 {'Method Not Allowed'} default {'Internal Server Error'} }
    $headers = "HTTP/1.1 $Status $reason`r`nContent-Type: $ContentType`r`nContent-Length: $($Body.Length)`r`nCache-Control: no-store`r`nX-Content-Type-Options: nosniff`r`nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`r`nConnection: close`r`n`r`n"
    $bytes = [Text.Encoding]::ASCII.GetBytes($headers)
    $Stream.Write($bytes, 0, $bytes.Length)
    if (!$HeadOnly) { $Stream.Write($Body, 0, $Body.Length) }
}
try {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Wiki preview requires the repository .NET SDK. Install it and rerun this script.' }
    & dotnet build $rendererProject -c Release --artifacts-path $rendererBuild --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'Wiki Markdown renderer build failed.' }
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    # Check the publication before opening a browser; each page request prepares it again.
    $initial = New-PreparedPreview
    Remove-PreviewDirectory $initial
    $listener.Start()
    $url = "http://127.0.0.1:$($listener.LocalEndpoint.Port)/Home"
    Write-Host "Noctaxis wiki preview: $url"
    Write-Host 'Refresh a page to pick up Markdown edits. Press Ctrl+C to stop and remove temporary files.'
    Write-Host "Temporary files: $temporaryRoot"
    if (!$NoBrowser) { Start-Process $url }
    while ($true) {
        if (!$listener.Pending()) { Start-Sleep -Milliseconds 100; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 2000
            $stream.WriteTimeout = 5000
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
            try {
                $request = $reader.ReadLine()
                if (!$request -or $request.Length -gt 8192) { continue }
                $origin = ''
                $headerSize = 0
                while ($line = $reader.ReadLine()) {
                    if ($line -match '^Origin:\s*(.+)$') { $origin = $Matches[1] }
                    $headerSize += $line.Length
                    if ($headerSize -gt 16384) { throw 'Request headers exceed the preview limit.' }
                }
            }
            finally { $reader.Dispose() }
            if ($request -notmatch '^(GET|HEAD|POST) (/\S*) HTTP/1\.[01]$') {
                Send-PreviewResponse $stream 405 'text/plain; charset=utf-8' ([Text.Encoding]::UTF8.GetBytes('Use GET or HEAD for pages; POST is reserved for the stop control.'))
                continue
            }
            $method = $Matches[1]
            $path = [Uri]::UnescapeDataString($Matches[2].Split([char[]]'?', 2)[0])
            if ($method -eq 'POST') {
                if ($path -eq '/__stop' -and $origin -ceq ("http://127.0.0.1:" + $listener.LocalEndpoint.Port)) {
                    Send-PreviewResponse $stream 200 'text/html; charset=utf-8' ([Text.Encoding]::UTF8.GetBytes('<h1>Wiki preview stopped</h1><p>Temporary files have been removed. You can close this tab.</p>'))
                    break
                }
                Send-PreviewResponse $stream 403 'text/plain; charset=utf-8' ([Text.Encoding]::UTF8.GetBytes('Stop requires the preview page on this origin.'))
                continue
            }
            $status = 200
            $type = 'text/html; charset=utf-8'
            if ($path -eq '/favicon.ico') { $status = 204; $body = [byte[]]::new(0) }
            elseif ($path -cmatch '^/images/([A-Za-z0-9_-]+\.png)$' -and (Test-Path -LiteralPath (Join-Path $images $Matches[1]) -PathType Leaf)) {
                $type = 'image/png'
                $body = [IO.File]::ReadAllBytes((Join-Path $images $Matches[1]))
            }
            else {
                $name = if ($path -eq '/') { 'Home' } else { $path.TrimStart('/') }
                try {
                    $html = New-PreviewPage $name
                    if ($null -eq $html) { $status = 404; $html = '<h1>Wiki page not found</h1><p><a href="/Home">Return to Home</a></p>' }
                }
                catch {
                    $status = 500
                    $html = '<h1>Wiki preparation failed</h1><p>Correct the Markdown and refresh.</p><pre>' + [Net.WebUtility]::HtmlEncode($_.Exception.Message) + '</pre>'
                    Write-Warning $_.Exception.Message
                }
                $body = [Text.Encoding]::UTF8.GetBytes($html)
            }
            Send-PreviewResponse $stream $status $type $body ($method -eq 'HEAD')
        }
        catch { Write-Verbose "Preview request closed or failed: $($_.Exception.Message)" }
        finally { $client.Dispose() }
    }
}
finally {
    $listener.Stop()
    Remove-PreviewDirectory $temporaryRoot
    Write-Host 'Wiki preview stopped; temporary files removed.'
}
