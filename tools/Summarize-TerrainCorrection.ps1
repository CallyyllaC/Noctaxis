$ErrorActionPreference = 'Stop'
$artifactRoot = Join-Path $PSScriptRoot '../artifacts/viewport-investigation'
$before = Get-Content -LiteralPath (Join-Path $artifactRoot 'native-kit-gpu2.json') -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath (Join-Path $artifactRoot 'native-correction.json') -Raw | ConvertFrom-Json
function Measure-NativeStage($capture, $stage) {
    $samples = @($capture.samples | Where-Object stage -eq $stage | ForEach-Object sample)
    $times = @($samples.TotalMs | Sort-Object)
    $mid = [int][Math]::Floor($times.Count / 2)
    $median = if ($times.Count % 2) { $times[$mid] } else { ($times[$mid - 1] + $times[$mid]) / 2 }
    [pscustomobject]@{
        samples = $times.Count
        meanMs = ($times | Measure-Object -Average).Average
        medianMs = $median
        worstMs = ($times | Measure-Object -Maximum).Maximum
        toneMs = ($samples.TonePreparationMs | Measure-Object -Average).Average
        gpuCompletionMs = ($samples.GpuCompletionMs | Measure-Object -Average).Average
        shaderMs = ($samples.ShaderMs | Measure-Object -Average).Average
        managedBytes = ($samples.ManagedBytes | Measure-Object -Average).Average
    }
}
$stages = foreach ($stage in @('bearing', 'pan', 'zoom', 'pitch', 'static')) {
    $a = Measure-NativeStage $before $stage
    $b = Measure-NativeStage $after $stage
    [pscustomobject]@{ stage = $stage; before = $a; after = $b; deltaMs = $b.meanMs - $a.meanMs }
}
$oldArtifacts = Get-Content -LiteralPath (Join-Path $artifactRoot 'artifacts-final.json') -Raw | ConvertFrom-Json
$artifacts = foreach ($old in $oldArtifacts.records) {
    $new = Get-Content -LiteralPath (Join-Path $artifactRoot "correction/metrics-$($old.scale)x.json") -Raw | ConvertFrom-Json
    [pscustomobject]@{ scale=$old.scale; beforeOverlap=$old.overlapPixels; beforeGaps=$old.gapPixels;
        semanticOverlap=$old.polarOverlap; beforeInvalid=$old.invalidPaths; after=$new }
}
$result = [pscustomobject]@{ profileBearings=$after.profileBearings; fieldOfView=$after.FieldOfView;
    gpuValidatedBeforeTiming=$after.gpuValidatedBeforeTiming; gpuPreflight=$after.gpuPreflight;
    stages=$stages; preparation=$after.preparation; artifacts=$artifacts; gpuCapture=$after.gpuCapture }
$result | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $artifactRoot 'correction-comparison.json')
$stages | ForEach-Object { [pscustomobject]@{
    Stage=$_.stage; BeforeMean=$_.before.meanMs; AfterMean=$_.after.meanMs;
    BeforeMedian=$_.before.medianMs; AfterMedian=$_.after.medianMs;
    BeforeWorst=$_.before.worstMs; AfterWorst=$_.after.worstMs; Delta=$_.deltaMs
} } | Format-Table -AutoSize
