#requires -Version 5.1
# Run with powershell.exe -NoProfile -File scripts/tests/Test-PowerShell51.ps1.
# Exercises the real 5.1 runtime; no native screenshots or release packages are generated.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
    throw 'Run this regression check with Windows PowerShell 5.1 (powershell.exe).'
}
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('noctaxis-ps51-' + [Guid]::NewGuid().ToString('N'))
$previewProcess = $null
$previewRoot = $null
function Assert($Condition, [string] $Message) { if (!$Condition) { throw $Message } }
function Write-Utf8([string] $Path, [string] $Text) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Read-SharedText([string] $Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Remove-TestDirectory([string] $Path, [string] $AllowedRoot) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($AllowedRoot)
    if ($full -ne $root -and !$full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the test directory.'
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
try {
    Push-Location $repo
    try { $scripts = @(git ls-files --cached --others --exclude-standard '*.ps1' '*.psm1' '*.psd1') }
    finally { Pop-Location }
    Assert ($LASTEXITCODE -eq 0) 'Cannot enumerate PowerShell source files.'
    foreach ($relative in $scripts) {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $relative), [ref]$tokens, [ref]$errors)
        Assert (!$errors) ("Parse failed: $relative " + ($errors | Out-String))
        Assert ($ast.ScriptRequirements.RequiredPSVersion -le [version]'5.1') "Requires newer PowerShell: $relative"
    }
    Write-Output "Parsed $($scripts.Count) PowerShell files in $($PSVersionTable.PSVersion)."
    & (Join-Path $repo 'scripts/prepare-wiki.ps1') -ValidateOnly
    & (Join-Path $repo 'scripts/generate-wiki-screenshots.ps1') -ValidateOnly
    $prepared = Join-Path $fixture 'prepared'
    & (Join-Path $repo 'scripts/prepare-wiki.ps1') -Destination $prepared -RepositoryRef 'feature/wiki'
    $wikiPages = @('Home','About','Getting-Started','Locations','Planner','Settings')
    Assert ((@(Get-ChildItem $prepared -Filter '*.md' | ForEach-Object BaseName | Sort-Object) -join ',') -eq
        (@($wikiPages + '_Sidebar' | Sort-Object) -join ',')) 'Wiki preparation page inventory changed.'
    $preparedText = (Get-ChildItem $prepared -Filter '*.md' | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    Assert ($preparedText.Contains('/blob/feature%2Fwiki/')) 'Repository ref URL encoding failed.'
    $sidebar = [IO.File]::ReadAllText((Join-Path $prepared '_Sidebar.md'))
    $navigation = @([regex]::Matches($sidebar, '\]\(([^)]+)\)') | ForEach-Object { $_.Groups[1].Value })
    Assert (($navigation -join ',') -eq ($wikiPages -join ',')) 'Wiki navigation pages or order changed.'

    # Launch the actual server with 5.1, avoiding browser UI and using an allocated port.
    $out = Join-Path $fixture 'preview.out'; $err = Join-Path $fixture 'preview.err'
    $previewProcess = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList @(
        '-NoProfile', '-File', ('"' + (Join-Path $repo 'scripts/preview-wiki.ps1') + '"'), '-NoBrowser', '-Port', '0'
    ) -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $log = ''
    do {
        if (Test-Path $out) { $log = (Read-SharedText $out) }
        if ($log -match 'Noctaxis wiki preview: (http://127\.0\.0\.1:\d+)/Home') { $origin = $Matches[1]; break }
        if ($previewProcess.HasExited) { throw ('Preview exited: ' + (Read-SharedText $err)) }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($origin) 'Preview startup timed out.'
    $log = (Read-SharedText $out)
    Assert ($log -match 'Temporary files: ([^\r\n]+)') 'Preview did not report its temporary path.'
    $previewRoot = $Matches[1]
    $pages = @{}
    foreach ($name in $wikiPages) {
        $response = Invoke-WebRequest "$origin/$name" -UseBasicParsing
        Assert ($response.StatusCode -eq 200) "Preview failed: $name"
        Assert ($response.Content.Contains('aria-current="page"')) "Missing navigation: $name"
        $pages[$name] = $response.Content
    }
    $links = 0
    foreach ($html in $pages.Values) {
        foreach ($match in [regex]::Matches($html, 'href="/([^"#]+)(?:#([^"]+))?"')) {
            $page = $match.Groups[1].Value
            Assert ($pages.ContainsKey($page)) "Bad local page link: $page"
            if ($match.Groups[2].Success) {
                Assert ($pages[$page].Contains('id="' + $match.Groups[2].Value + '"')) "Missing heading: $($match.Value)"
            }
            $links++
        }
    }
    foreach ($image in Get-ChildItem (Join-Path $repo 'images/wiki') -Filter '*.png') {
        $download = Join-Path $fixture $image.Name
        Invoke-WebRequest ("$origin/images/" + $image.Name) -UseBasicParsing -OutFile $download
        Assert ((Get-FileHash $download).Hash -eq (Get-FileHash $image.FullName).Hash) "Image changed: $($image.Name)"
    }
    $head = Invoke-WebRequest "$origin/Home?check=1" -Method Head -UseBasicParsing
    Assert ($head.StatusCode -eq 200) 'HEAD/query handling failed.'
    $null = Invoke-WebRequest "$origin/__stop" -Method Post -Headers @{ Origin = $origin } -UseBasicParsing
    Assert ($previewProcess.WaitForExit(10000)) 'Preview stop did not exit.'
    Assert (!(Test-Path -LiteralPath $previewRoot)) 'Preview did not clean its temporary files.'
    Write-Output "Preview: six pages, $links page/heading links, image bytes, HEAD/query and stop/cleanup passed."

    # An isolated fake host tests .NET Framework argument/env APIs and both image promotion paths.
    # Spaces and Unicode in the root catch quoting and text encoding regressions.
    $captureRoot = Join-Path $fixture ('capture space ' + [char]0x00e9)
    $captureScript = Join-Path $captureRoot 'scripts/generate-wiki-screenshots.ps1'
    Write-Utf8 $captureScript ([IO.File]::ReadAllText((Join-Path $repo 'scripts/generate-wiki-screenshots.ps1')))
    $manifest = @([ordered]@{ id = 'fixture'; pages = @('page.md'); alt = ('Caf' + [char]0x00e9); caption = 'Fixture caption' })
    Write-Utf8 (Join-Path $captureRoot 'docs/wiki-screenshots.json') (ConvertTo-Json -InputObject $manifest)
    $pagePath = Join-Path $captureRoot 'docs/user-guide/page.md'
    Write-Utf8 $pagePath '<!-- wiki-image:fixture -->'
    $hostPath = Join-Path $captureRoot 'artifacts/wiki/build/bin/Noctaxis.WikiCapture/debug/Noctaxis.WikiCapture.exe'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $hostPath)) | Out-Null
    $hostSource = @"
using System;
using System.IO;
public static class CaptureFixture {
    public static int Main(string[] args) {
        if (args.Length != 4 || !Directory.Exists(args[0]) || !Directory.Exists(args[1]) ||
            args[2] != "fixture" || args[3] != "15" || Environment.GetEnvironmentVariable("NOCTAXIS_DIAGNOSTICS") != "0") return 22;
        if (Environment.GetEnvironmentVariable("NOCTAXIS_PS51_FIXTURE_FAIL") == "1") return 23;
        File.WriteAllBytes(Path.Combine(args[0], "fixture.png"), new byte[1024]);
        File.WriteAllText(Path.Combine(args[0], "capture-report.json"), "{\"screenshots\":[{\"id\":\"fixture\"}]}");
        Console.WriteLine("fixture stdout");
        Console.Error.WriteLine("fixture stderr");
        return 0;
    }
}
"@
    Add-Type -TypeDefinition $hostSource -OutputAssembly $hostPath -OutputType ConsoleApplication
    & $captureScript -NoBuild -Only fixture -TimeoutSeconds 15
    $imagePath = Join-Path $captureRoot 'images/wiki/fixture.png'
    Assert ((Get-Item $imagePath).Length -eq 1024) 'New image promotion failed.'
    $run = @(Get-ChildItem (Join-Path $captureRoot 'artifacts/wiki/runs') -Directory)[0].FullName
    Assert ([IO.File]::ReadAllText((Join-Path $run 'stdout.log')).Contains('fixture stdout')) 'Host stdout was not captured.'
    Assert ([IO.File]::ReadAllText((Join-Path $run 'stderr.log')).Contains('fixture stderr')) 'Host stderr was not captured.'
    Assert ([IO.File]::ReadAllText($pagePath).Contains($manifest[0].alt)) 'UTF-8 manifest text was corrupted.'
    [IO.File]::WriteAllBytes($imagePath, [byte[]](1,2,3))
    & $captureScript -NoBuild -Only fixture -TimeoutSeconds 15
    Assert ((Get-Item $imagePath).Length -eq 1024) 'Existing image replacement failed.'
    Assert ([regex]::Matches([IO.File]::ReadAllText($pagePath), '!\[').Count -eq 1) 'Image block duplicated.'
    $beforePage = (Get-FileHash $pagePath).Hash; $beforeImage = (Get-FileHash $imagePath).Hash
    $previousFailureFlag = $env:NOCTAXIS_PS51_FIXTURE_FAIL
    try {
        $env:NOCTAXIS_PS51_FIXTURE_FAIL = '1'
        $failed = $false
        try { & $captureScript -NoBuild -Only fixture -TimeoutSeconds 15 } catch { $failed = $_.Exception.Message -like 'Native capture failed.*' }
        Assert $failed 'Host failure was not propagated.'
    }
    finally { $env:NOCTAXIS_PS51_FIXTURE_FAIL = $previousFailureFlag }
    Assert ((Get-FileHash $pagePath).Hash -eq $beforePage -and (Get-FileHash $imagePath).Hash -eq $beforeImage) 'Failed capture changed published fixture files.'
    Write-Output 'Capture fixture: quoted Unicode paths, environment, logs, new/replaced images and failure preservation passed.'

    # Refreshing the PNG must not replace reviewed captions or require unrelated page markers.
    Write-Utf8 $pagePath ([IO.File]::ReadAllText($pagePath).Replace('Fixture caption', 'Reviewed custom caption'))
    $reviewedHash = (Get-FileHash $pagePath).Hash
    & $captureScript -NoBuild -Only fixture -TimeoutSeconds 15
    Assert ((Get-FileHash $pagePath).Hash -eq $reviewedHash) 'Regeneration replaced reviewed image text.'
    $manifest += [ordered]@{ id = 'unrelated'; pages = @('other.md'); alt = 'Unrelated'; caption = 'Unrelated' }
    Write-Utf8 (Join-Path $captureRoot 'docs/wiki-screenshots.json') (ConvertTo-Json -InputObject $manifest)
    Write-Utf8 (Join-Path $captureRoot 'docs/user-guide/other.md') 'Unrelated page without an insertion marker.'
    & $captureScript -NoBuild -Only fixture -TimeoutSeconds 15
    Assert ((Get-FileHash $pagePath).Hash -eq $reviewedHash) 'Selected regeneration changed reviewed image text.'
    Write-Output 'Capture fixture: reviewed captions and selected-marker validation passed.'
    Write-Utf8 $pagePath "# Reviewed layout`n`n| Existing image |`n| --- |`n| ![Reviewed image](../../images/wiki/fixture.png) |"
    $reviewedHash = (Get-FileHash $pagePath).Hash
    & $captureScript -NoBuild -Only fixture -TimeoutSeconds 15
    Assert ((Get-FileHash $pagePath).Hash -eq $reviewedHash) 'Regeneration changed an existing image layout without a per-image marker.'
    $manifest += [ordered]@{ id = 'asset-only'; pages = @(); alt = 'Asset for later editorial placement'; caption = 'No insertion' }
    Write-Utf8 (Join-Path $captureRoot 'docs/wiki-screenshots.json') (ConvertTo-Json -InputObject $manifest)
    & $captureScript -Only asset-only -ValidateOnly
    Write-Output 'Capture fixture: reviewed grouped layouts and asset-only registration passed.'
}
finally {
    if ($previewProcess -and !$previewProcess.HasExited) { $previewProcess.Kill(); $previewProcess.WaitForExit() }
    if ($previewRoot -and (Test-Path -LiteralPath $previewRoot)) {
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        Assert ($previewRoot.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $previewRoot) -like 'noctaxis-wiki-preview-*') 'Unexpected preview cleanup path.'
        Remove-TestDirectory $previewRoot $previewRoot
    }
    Remove-TestDirectory $fixture $fixture
}
