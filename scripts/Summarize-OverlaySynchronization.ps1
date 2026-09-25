param([string]$Path = "$PSScriptRoot/../docs/benchmarks/overlay-sync-native.json")
$data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
function Percentile($values, $fraction) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0 }
    return $sorted[[Math]::Min($sorted.Count - 1, [Math]::Ceiling($sorted.Count * $fraction) - 1)]
}
$rows = foreach ($group in ($data.runs | Group-Object polling,stage)) {
    $runs = @($group.Group)
    $samples = @($runs | ForEach-Object { $_.samples })
    $frames = @($samples | Where-Object Kind -eq composition)
    $renders = @($samples | Where-Object Kind -eq overlay)
    $invalidations = @($samples | Where-Object Kind -eq invalidate)
    [pscustomobject]@{
        Mode = $(if ($runs[0].polling) { 'Polling' } else { 'Events' })
        Stage = $runs[0].stage
        Compositions = $frames.Count
        MeanErrorDip = [Math]::Round(($frames.PositionErrorDip | Measure-Object -Average).Average, 3)
        P95ErrorDip = [Math]::Round((Percentile $frames.PositionErrorDip .95), 3)
        MaxErrorDip = [Math]::Round((Percentile $frames.PositionErrorDip 1), 3)
        P95InvalidationMs = [Math]::Round((Percentile $invalidations.LatestChangeMs .95), 3)
        P95OldestPendingRenderMs = [Math]::Round((Percentile $renders.OldestPendingMs .95), 3)
        Invalidations = $invalidations.Count
        AllocatedBytes = ($runs.allocatedBytes | Measure-Object -Sum).Sum
    }
}
$rows | Export-Csv -LiteralPath ([IO.Path]::ChangeExtension($Path, 'csv')) -NoTypeInformation
$rows | Format-Table -AutoSize
