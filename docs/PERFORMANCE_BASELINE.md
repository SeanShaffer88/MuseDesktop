# MuseDesktop performance baseline

This is a repeatable baseline protocol, not a claim that measurements have been collected. Record results from a real Windows machine; do not compare measurements from different machines as if they were equivalent.

## Test setup

Record the MuseDesktop version/commit, Windows version, CPU, RAM, architecture, WebView2 Runtime version, display scale, and whether the run is cold (first launch after reboot) or warm. Use the same profile and network conditions for comparisons. Keep the app idle for two minutes before measuring steady-state memory.

## Measurements

| Scenario | Measurement | Result | Machine / run notes |
|---|---|---|---|
| Cold launch | Start to interactive Muse page | Not measured | |
| Warm launch | Start to interactive Muse page | Not measured | |
| Idle after two minutes | Private bytes / working set | Not measured | |
| Open Settings | Click to interactive window | Not measured | |
| Open Downloads with a typical history | Click to interactive window | Not measured | |
| Filter a 1,000-entry download history | Keystroke to visible results | Not measured | |
| Open Diagnostics | Click to first complete report | Not measured | |
| Profile-size scan | Elapsed time and files scanned | Not measured | |

## Static bottleneck review (unmeasured)

This is a source inspection, not a profile; ordering below is a list of measurement priorities, not a claim that the first item is the measured slowest path.

1. **WebView2 process startup and first navigation** — likely dominant to perceived launch because the app must create the runtime and load Muse over the network. Separate app startup milestones from WebView2/runtime/network time with WPR/WPA before changing startup behavior.
2. **Profile-folder size walk** — O(files + directories), potentially the largest local disk scan. The current code already runs UI-triggered scans on a worker and caches read-only summaries for 15 seconds; cache/quota/cleanup correctness depends on fresh measurements, so do not extend that cache without data.
3. **Downloads history open/search** — JSON history is capped at 1,000 rows; query/filter/sort is linearithmic and UI updates are debounced/virtualized. Profile disk read/deserialization separately from sorting and WPF item-container/render work before optimizing.
4. **Palette/history fuzzy search** — repeated scoring allocates temporary strings, but the browser history is capped at 50 entries per window and the palette corpus is bounded. Measure allocations and keystroke latency with representative open-window/provider counts before changing scoring semantics.
5. **Idle dispatcher work** — productivity timers do cheap state updates each second and schedule work less frequently; inspect WPR CPU wakeups first. Do not replace timers with a more complex scheduler without evidence.

No benchmark or profiler is installed in the current coding environment, and it cannot execute this Windows WPF/WebView2 application. The codebase's existing performance changes (background folder scans, short-lived size cache, debounced/virtualized lists, event-driven download refresh, and asynchronous hash/support-bundle work) are visible in source, but no before/after measurements exist here. Any claimed impact must wait for the procedure below on the same Windows device.

## Procedure

1. Use a Release build and note whether WebView2 is already warm.
2. For launch timing, capture at least five cold and five warm runs; report median and slowest result, not only the best run.
3. Use the built-in Diagnostics startup timings and memory facts where available. Use Windows Performance Recorder/Analyzer or an equivalent profiler for CPU and disk attribution; do not infer a bottleneck from wall-clock time alone.
4. Populate representative test data (including the maximum supported download history) in a disposable profile. Do not use real conversation contents in a public report.
5. Attach results to a change or release note when a performance optimization is claimed. Treat a regression as actionable when it is repeatable on the same machine and exceeds the previous median by 10% or 100 ms, whichever is larger.

## Interpretation

No target values are recorded yet. First establish a baseline on the maintainer's Windows machine, then set explicit budgets from observed user-impacting behavior. This document must not be marked complete until a real-device run is recorded.
