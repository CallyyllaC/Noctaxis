param([Parameter(Mandatory=$true)][string]$Path)
$pinRun = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
$pinRun.results | ForEach-Object {
    $pinCalls = $_.after.pin.calls - $_.before.pin.calls
    $projectionCalls = $_.after.projection.calls - $_.before.projection.calls
    [pscustomobject]@{
        Repeat = $_.repeat
        Mode = $_.mode
        Scenario = $_.scenario
        OverlayRenders = $_.after.overlay.calls - $_.before.overlay.calls
        PinDraws = $pinCalls
        PinMicrosecondsPerCall = [math]::Round(1000 * ($_.after.pin.totalMs - $_.before.pin.totalMs) / [math]::Max(1, $pinCalls), 3)
        ProjectionMicrosecondsPerCall = [math]::Round(1000 * ($_.after.projection.totalMs - $_.before.projection.totalMs) / [math]::Max(1, $projectionCalls), 3)
        PinBytes = $_.after.pin.allocatedBytes - $_.before.pin.allocatedBytes
        Measures = $_.after.measures - $_.before.measures
        Arranges = $_.after.arranges - $_.before.arranges
        MapRefreshRequests = $_.mapRefreshRequests
        ProcessCpuMs = $_.processCpuMs
        ProcessAllocatedBytes = $_.processAllocatedBytes
        Gen0 = $_.collections[0]
        Gen1 = $_.collections[1]
        Gen2 = $_.collections[2]
        UiGapP95Ms = $_.uiGapP95Ms
        UiGapMaxMs = $_.uiGapMaxMs
    }
}
