param(
    [string]$Baseline = 'docs/terrain-compute-baseline.json',
    [string]$After = 'docs/terrain-compute-final.json',
    [string]$Output = 'docs/terrain-compute-comparison'
)
$ErrorActionPreference = 'Stop'
$beforeRows = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
$afterRows = Get-Content -LiteralPath $After -Raw | ConvertFrom-Json
function Median($rows, $field) {
    $ordered = @($rows | Sort-Object -Property $field)
    return $ordered[[int][math]::Floor($ordered.Count / 2)].$field
}
$comparison = foreach ($group in ($beforeRows | Group-Object scenario,operation,workers)) {
    $first = $group.Group[0]
    $current = @($afterRows | Where-Object {
        $_.scenario -eq $first.scenario -and $_.operation -eq $first.operation -and $_.workers -eq $first.workers
    })
    if ($current.Count -ne $group.Count) { throw "Missing matching measurements: $($group.Name)" }
    $beforeMs = Median $group.Group 'milliseconds'
    $afterMs = Median $current 'milliseconds'
    $beforeBytes = Median $group.Group 'allocated'
    $afterBytes = Median $current 'allocated'
    [pscustomobject]@{
        Scenario = $first.scenario; Operation = $first.operation; Workers = $first.workers
        BeforeMs = [math]::Round($beforeMs, 4); AfterMs = [math]::Round($afterMs, 4)
        DeltaMs = [math]::Round($afterMs - $beforeMs, 4)
        TimePercent = [math]::Round(100 * ($afterMs / $beforeMs - 1), 2)
        BeforeBytes = $beforeBytes; AfterBytes = $afterBytes; DeltaBytes = $afterBytes - $beforeBytes
        AllocationPercent = [math]::Round(100 * ($afterBytes / $beforeBytes - 1), 2)
        ClassifiedBefore = Median $group.Group 'classifications'; ClassifiedAfter = Median $current 'classifications'
        TouchesBefore = Median $group.Group 'touches'; TouchesAfter = Median $current 'touches'
        LoadsBefore = Median $group.Group 'tileLoads'; LoadsAfter = Median $current 'tileLoads'
        DecodedHitsBefore = Median $group.Group 'decodedHits'; DecodedHitsAfter = Median $current 'decodedHits'
        PositiveAfter = Median $current 'positive'; NegativeAfter = Median $current 'negative'
        ProfileCache = $first.profileCache
    }
}
$comparison | Export-Csv -LiteralPath ($Output + '.csv') -NoTypeInformation
$lines = @('# Terrain compute comparison', '',
    'Independent medians of three measured repetitions following one warm-up. Negative deltas are improvements. Tiny warm-hit percentages reflect timer/GC-counter noise; raw measurements include every repetition. These are offline PNG workloads, not surveyed terrain or live network results.', '',
    '| Scenario | Operation | Workers | Before ms | After ms | Delta ms | Time % | Before bytes | After bytes | Delta bytes | Allocation % |',
    '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($row in $comparison) {
    $lines += "| $($row.Scenario) | $($row.Operation) | $($row.Workers) | $($row.BeforeMs) | $($row.AfterMs) | $($row.DeltaMs) | $($row.TimePercent) | $($row.BeforeBytes) | $($row.AfterBytes) | $($row.DeltaBytes) | $($row.AllocationPercent) |"
}
$lines += @('', '| Scenario | Operation | Workers | Classified before/after | Cache touches before/after | Loads before/after | Decoded hits before/after | Positive/negative after | Profile cache |',
    '|---|---|---:|---:|---:|---:|---:|---:|---|')
foreach ($row in $comparison) {
    $lines += "| $($row.Scenario) | $($row.Operation) | $($row.Workers) | $($row.ClassifiedBefore)/$($row.ClassifiedAfter) | $($row.TouchesBefore)/$($row.TouchesAfter) | $($row.LoadsBefore)/$($row.LoadsAfter) | $($row.DecodedHitsBefore)/$($row.DecodedHitsAfter) | $($row.PositiveAfter)/$($row.NegativeAfter) | $($row.ProfileCache) |"
}
$lines | Set-Content -LiteralPath ($Output + '.md')
