#requires -Version 5.1
<#
.SYNOPSIS
Capture the running Avalonia application in a fresh, isolated demonstration profile.
.EXAMPLE
./scripts/generate-wiki-screenshots.ps1 -GettingStarted
Regenerate the four Getting Started illustrations, retaining its existing Planner overview.
.EXAMPLE
./scripts/generate-wiki-screenshots.ps1 -Only celestial-objects,viability-favourable,viability-compromised
Regenerate only the new captures; reuse equipment-settings.png unchanged.
.NOTES
Requires an unlocked Windows desktop and network access for real map/terrain tiles.
Use -Only planner-map-context-menu to capture the real map menu at the Eryri demo.
This asset has no automatic page placement; Planner editorial text is not touched.
Planner overview uses main light pollution only (Inferno, 60%) and PiP terrain relief
only (100%, 73.2421875 m/px), retaining the Eryri/M31 workspace. A complete real Lorenz
2025 atlas is cached under artifacts/wiki/atlas-cache, or reused from the existing
isolated artifacts/lorenz-real-install dataset. First download may take several minutes.
Only the overview receives a separate copy; other scenes retain their atlas-free setup.
Comparison scenes use M31 at 53.0056,-3.9519 on 2026-10-09, Europe/London,
36x24 mm / 6000x4000 px, 70 mm, Follow with camera. Favourable: 23:00 / 5% cloud;
compromised: 19:00 / 95% cloud. Weather is explicitly simulated in capture tooling;
astronomy, terrain, framing and all scoring use production implementations. No atlas
is installed for comparison scenes, so that factor stays unavailable rather than fabricated.
Reports include calculated scores, warnings, coverage and inputs. Comparison quality
gates fail closed if the real scorer no longer produces the intended contrast.
Without -Only or -GettingStarted, regenerate all manifest entries. Build once and use
-NoBuild to repeat. -ValidateOnly checks definitions without launching the application.
All selected captures must succeed before images or Markdown are replaced. Diagnostics
remain under artifacts/wiki/runs. Images use wiki-compatible publication mapping.
#>
[CmdletBinding()]
param(
    [string[]] $Only = @(),
    [switch] $GettingStarted,
    [ValidateRange(15, 600)] [int] $TimeoutSeconds = 120,
    [switch] $NoBuild,
    [switch] $ValidateOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repo 'docs/wiki-screenshots.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$ids = @($manifest.id)
if ($GettingStarted) {
    if ($Only.Count -ne 0) { throw 'Choose -GettingStarted or -Only, not both.' }
    $Only = @('equipment-settings', 'celestial-objects', 'viability-favourable', 'viability-compromised')
}
if ($Only.Count -eq 0) { $Only = $ids }
foreach ($id in $Only) { if ($id -cnotin $ids) { throw "Unknown screenshot '$id'. Choose: $($ids -join ', ')" } }
if (@($Only | Select-Object -Unique).Count -ne $Only.Count) { throw 'Screenshot IDs must be unique.' }
$guide = Join-Path $repo 'docs/user-guide'
# Public screenshots live in the repository's media folder, which awoo.ltd's project gallery also reads.
# Guide pages (docs/user-guide/*.md) link them by this relative path.
$images = Join-Path $repo 'images/wiki'
$imageLink = '../../images/wiki/'
foreach ($shot in $manifest | Where-Object { $_.id -cin $Only }) {
    foreach ($page in $shot.pages) {
        $body = Get-Content -LiteralPath (Join-Path $guide $page) -Raw -Encoding UTF8
        # Reviewed layouts may group several existing images under one marker (e.g. a comparison table).
        # Those PNG links need no insertion; regeneration must leave their Markdown untouched.
        if (!$body.Contains("<!-- wiki-image:$($shot.id) -->") -and
            !$body.Contains("($imageLink$($shot.id).png)")) { throw "Missing screenshot marker or existing image link: $page / $($shot.id)" }
    }
}
if ($ValidateOnly) { Write-Output "Validated $($Only.Count) selected screenshot definitions and Markdown placements."; return }
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Native screenshot generation currently requires Windows and an unlocked graphical desktop.' }
$build = Join-Path $repo 'artifacts/wiki/build'
if (!$NoBuild) {
    & dotnet build (Join-Path $repo 'tools/Noctaxis.WikiCapture/Noctaxis.WikiCapture.csproj') --artifacts-path $build --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Wiki capture host build failed.' }
}
$exe = Join-Path $build 'bin/Noctaxis.WikiCapture/debug/Noctaxis.WikiCapture.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Capture host not found: $exe" }
# Each run starts from known demonstration settings. Retain failed runs for diagnosis.
$run = Join-Path $repo ("artifacts/wiki/runs/" + [Guid]::NewGuid().ToString('N'))
$pending = Join-Path $run 'pending'
$demo = Join-Path $run 'demo-data'
New-Item -ItemType Directory -Path $pending, $demo -Force | Out-Null
# The child is a native desktop app. Its host controls views without global mouse/keyboard input.
# Logs, state and all download caches belong to this run, not the real Noctaxis profile.
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $repo
# .NET Framework has Arguments rather than ArgumentList. Quote using Windows argv rules,
# including embedded quotes and trailing backslashes (paths may contain spaces).
$arguments = @($pending, $demo, ($Only -join ','), [string]$TimeoutSeconds)
$start.Arguments = ($arguments | ForEach-Object {
    '"' + ([regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"'
}) -join ' '
$start.EnvironmentVariables['NOCTAXIS_DIAGNOSTICS'] = '0'
$atlasAllowance = 0
if ($Only -ccontains 'planner-overview') {
    $atlasCache = Join-Path $repo 'artifacts/wiki/atlas-cache'
    $existingAtlas = Join-Path $repo 'artifacts/lorenz-real-install'
    $pointer = 'EnvironmentalData/LightPollution/Lorenz/2025/current'
    if (!(Test-Path -LiteralPath (Join-Path $atlasCache $pointer)) -and
        (Test-Path -LiteralPath (Join-Path $existingAtlas $pointer))) { $atlasCache = $existingAtlas }
    $start.EnvironmentVariables['NOCTAXIS_WIKI_ATLAS_CACHE'] = $atlasCache
    # Bounded first-download allowance, separate from map/render readiness timeouts.
    $atlasAllowance = 660
}
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$deadline = [DateTime]::UtcNow.AddSeconds(($TimeoutSeconds + 90) * ($Only.Count + 1) + $atlasAllowance)
while (!$process.WaitForExit(250)) {
    if ([DateTime]::UtcNow -gt $deadline) {
        # taskkill preserves whole-process-tree termination on Windows PowerShell 5.1.
        & "$env:WINDIR\System32\taskkill.exe" /PID $process.Id /T /F | Out-Null
        if (!$process.HasExited) { $process.Kill() }
        throw "Screenshot host timed out. Existing images are unchanged. Diagnostics: $run"
    }
}
[IO.File]::WriteAllText((Join-Path $run 'stdout.log'), $stdout.GetAwaiter().GetResult())
[IO.File]::WriteAllText((Join-Path $run 'stderr.log'), $stderr.GetAwaiter().GetResult())
if ($process.ExitCode -ne 0) {
    $errorFile = Join-Path $pending 'capture-error.txt'
    if (Test-Path -LiteralPath $errorFile) { Write-Warning (Get-Content -LiteralPath $errorFile -Raw -Encoding UTF8) }
    throw "Native capture failed. Exit code: $($process.ExitCode). Existing images and pages are unchanged. Diagnostics: $run"
}
$reportPath = Join-Path $pending 'capture-report.json'
if (!(Test-Path -LiteralPath $reportPath)) { throw "Capture report missing. Existing images are unchanged: $run" }
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($id in $Only) {
    $candidate = Join-Path $pending "$id.png"
    if (!(Test-Path -LiteralPath $candidate) -or (Get-Item -LiteralPath $candidate).Length -lt 1000) { throw "Missing or invalid capture: $id" }
    if ($id -cnotin @($report.screenshots.id)) { throw "Capture provenance missing: $id" }
}
# Promote only after the entire selected set has succeeded. Replacement is atomic per image.
New-Item -ItemType Directory -Path $images -Force | Out-Null
foreach ($id in $Only) {
    $temporary = Join-Path $images "$id.png.tmp"
    Copy-Item -LiteralPath (Join-Path $pending "$id.png") -Destination $temporary
    $destination = Join-Path $images "$id.png"
    if ([IO.File]::Exists($destination)) { [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $destination) }
}
foreach ($shot in $manifest | Where-Object { $_.id -cin $Only }) {
    foreach ($page in $shot.pages) {
        $path = Join-Path $guide $page
        $body = [IO.File]::ReadAllText($path)
        $id = $shot.id
        $block = "<!-- wiki-image:$id -->" + "`n`n" + "![$($shot.alt)]($imageLink$id.png)" +
            "`n`n" + "*$($shot.caption)*" + "`n<!-- /wiki-image:$id -->"
        $pattern = '(?s)<!-- wiki-image:' + [regex]::Escape($id) + ' -->(?:.*?<!-- /wiki-image:' + [regex]::Escape($id) + ' -->)?'
        $updated = [regex]::Replace($body, $pattern, [Text.RegularExpressions.MatchEvaluator]{
            param($m)
            # Regenerating an existing asset must preserve manually reviewed alt text, captions,
            # layout and line endings. Manifest text is only the fallback for a new image block.
            if ($m.Value.Contains("($imageLink$id.png)")) { $m.Value } else { $block }
        })
        if ($updated -cne $body) { [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false)) }
    }
}
Copy-Item -LiteralPath $reportPath -Destination (Join-Path $run 'capture-report.json')
Write-Output "Captured $($Only.Count) images; Markdown placements preserved or inserted where configured. Inspect before committing. Report: $run/capture-report.json"
