"""Summarize recorded native viewport probes; never recompute Mapsui tile levels."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / "artifacts/lp-viewport"
VIEWPORTS = ["1920x1080", "2560x1440", "3440x1440", "3840x2160"]


def load(folder, viewport):
    return json.loads((BASE / folder / f"{viewport}.json").read_text(encoding="utf-8-sig"))


def table(headers, rows):
    return "\n".join(["| " + " | ".join(headers) + " |", "|" + "---|" * len(headers)] +
                     ["| " + " | ".join(map(str, row)) + " |" for row in rows])


def levels(value):
    return ", ".join(f"z{k}:{v}" for k, v in value.items()) or "none"


out = ["# Light Pollution viewport measurements", "",
       "Generated from `artifacts/lp-viewport/baseline-final` and `after-native`. All times are milliseconds. "
       "Working sets are the lists recorded inside the native fetch strategy, including cached tiles; "
       "dispatch counts are actual source requests. Eviction, repeat and numerical counters in the tables below "
       "are stage deltas. Native relevance includes all current required fallback levels. "
       "Native eviction totals include tiles published and evicted within the same four-job batch.", ""]

summary = []
for viewport in VIEWPORTS:
    before, after = load("baseline-final", viewport), load("after-native", viewport)
    steps = before["steps"]
    summary.append([viewport, steps[0]["fetchWorkingSet"],
                    max(s["fetchWorkingSet"] for s in steps if s["name"].startswith("pan")),
                    max(s["fetchWorkingSet"] for s in steps if s["name"].startswith("zoom")),
                    f'{steps[-1]["repeated"]} → {after["steps"][-1]["repeated"]}',
                    f'{steps[-1]["nativeRelevantEvictions"]} → {after["steps"][-1]["nativeRelevantEvictions"]}',
                    f'{sum(s["elapsedMs"] for s in steps)/1000:.2f} → {sum(s["elapsedMs"] for s in after["steps"])/1000:.2f}'])
out += ["## Path overview", "", table(["Viewport", "Initial set", "Pan peak", "Zoom peak", "Repeated XYZ before → after", "Required native evictions before → after", "Path fetch seconds before → after"], summary), ""]

for folder, label in [("baseline-final", "Before"), ("after-native", "After")]:
    for viewport in VIEWPORTS:
        data = load(folder, viewport)
        out += [f"## {label}: {viewport}", ""]
        work, caches, numerical, timing = [], [], [], []
        prev = {}
        for s in data["steps"]:
            detail = json.loads((BASE / folder / f'{viewport}-{s["name"]}-detail.json').read_text())
            delta = lambda key: s[key] - prev.get(key, 0)
            work.append([s["name"], s["level"], levels(s["levels"]), s["currentLevel"], s["fallback"], s["fetchWorkingSet"], levels(detail["actualFetchLevels"]), s["jobsTotal"], detail["notScheduledByNativePlanner"]])
            caches.append([s["name"], s["nativeCount"], delta("nativeEvictions"), delta("nativeVisibleEvictions"), delta("nativeRelevantEvictions"), s["renderedCache"], delta("renderedEvictions"), delta("renderedVisibleEvictions"), delta("repeated"), s["currentLevelMissing"], s["missing"]])
            numerical.append([s["name"], delta("renderedHits"), delta("renderedMisses"), delta("numericalHits"), delta("numericalMisses"), delta("numericalEvictions"), s["decoded"], s["peak"] if s["jobsTotal"] else 0])
            timing.append([s["name"], f'{s["elapsedMs"]:.2f}', f'{s["waitMs"]["sum"]:.2f}', f'{s["waitMs"]["mean"]:.2f}', f'{s["generationMs"]["mean"]:.2f}', f'{s["pngMs"]["mean"]:.2f}', f'{s["publicationMs"]["mean"]:.2f}', f'{s["publicationMs"]["p95"]:.2f}', f'{s["afterSourceMs"]["p95"]:.3f}'])
            prev = s
        out += [table(["Stage", "Selected", "Required levels:counts", "Current", "Fallback", "Total set", "Dispatched levels:counts", "Dispatched", "Unscheduled"], work), "",
                table(["Stage", "Native count", "Native evictions", "Current-level evictions", "All required evictions", "Raster count", "Raster evictions", "Current raster evictions", "Repeated XYZ", "Missing detail", "Uncovered centres"], caches), "",
                table(["Stage", "Raster hits", "Raster misses/generations", "Numerical hits", "Numerical misses/decompressions", "Numerical evictions", "Numerical occupancy", "Peak raster in flight"], numerical), "",
                table(["Stage", "Fetch wall ms", "Read wait sum", "Read wait mean", "Generation mean", "PNG mean", "Raster-start→publication mean", "Raster-start→publication p95", "Source→publication p95"], timing), "",
                f'Errors: {data["Failures"]}; empty results: {data["EmptyResults"]}; rejected palette revisions: {data["RejectedRevisions"]}.', ""]

out += ["## Retained-memory estimates after the fix", "",
        "Encoded bytes are measured from the two cache contents and deduplicated by byte-array reference. "
        "Numerical bytes include loaded source grids and the overview. Decoded native image storage is a conservative "
        "256×256×4 bytes per retained feature, not a measured GPU allocation. Estimates exclude object overhead, "
        "framebuffers, temporary encoding buffers and other application/map-renderer caches.", ""]
memory = []
for viewport in VIEWPORTS:
    details = [json.loads(p.read_text())["memory"] for p in (BASE / "after-native").glob(f"{viewport}-*-detail.json")]
    mib = lambda key: max(d[key] for d in details) / 1048576
    total = max(d["distinctEncodedBytes"] + d["numericalBytes"] + d["conservativeNativeDecodedBytes"] for d in details) / 1048576
    memory.append([viewport, max(d["nativeCapacity"] for d in details), f'{mib("sourceBytes"):.2f}', f'{mib("distinctEncodedBytes"):.2f}', f'{mib("numericalBytes"):.2f}', f'{mib("conservativeNativeDecodedBytes"):.2f}', f'{total:.2f}'])
out += [table(["Viewport", "Capacity high-water", "Raster PNG MiB", "Distinct PNG MiB", "Numerical MiB", "Potential native RGBA MiB", "Combined estimate MiB"], memory), ""]
(ROOT / "docs/light-pollution-viewport-measurements.md").write_text("\n".join(out), encoding="utf-8")
