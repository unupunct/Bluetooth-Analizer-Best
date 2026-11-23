# Signal Analysis: the device deep-dive (design)

Status: proposed, waiting for approval. Size: a project (one new Windows screen, one pure analysis engine, small additions elsewhere).

## Intent and why

The user runs 170+ Bluetooth devices and found the Fault Detector's "Worst Offenders" list. When a device there looks flaky, the next question is "what exactly is this thing, and why is it flaky?" They want to answer it inside the app: find out more about the device, and see a proper analysis of its behaviour. Today the Signal Analysis navigation entry is a placeholder and Device History shows only a basic chart.

## Goals

1. **Signal Analysis becomes a per-device deep-dive** (spec section 10): signal over time with moving average, minimum/maximum/average/median, volatility, signal distribution, time between advertisements, missing-observation intervals, signal recovery time, and the optional Excellent..Very weak interpretation.
2. **A "what is this device" panel** built from everything the database knows, offline.
3. **A verdict header** reusing the Fault Detector's score, confidence, findings and recommendation for the same device and window, so the page answers "why is it flaky" without a second screen.
4. **"Look up online" button** that opens the user's browser with a web search built from maker and name. The app itself makes no network call.
5. Reachable in one click from the Fault Detector, the Device Inventory and the Live Monitor ("Analyse").

## Non-goals

- No new database tables, no migrations: everything is computed from existing observations.
- No internet access from the app (the only network use stays phone sync). No scraping, no online database.
- No phone-side screen in this version.
- No fault verdicts of its own: the Fault Detector stays the single source of health scores.
- Signal Survey and Reports are separate, later pieces.

## Anti-goals (would be a failure even if it "works")

- Showing RSSI as a distance or a universal quality threshold (spec forbids it; labels are optional and configurable and are shown as a description of this device's own signal).
- Calling a device faulty from too little data, or charging it for time the scanner was not listening or for outages shared with many other devices (reuse the Fault Detector's coverage and correlation rules).
- Sending the Bluetooth address, or anything that identifies the user's fleet, to any website.

## Constraints

- WPF/.NET 10, MVVM, LiveCharts2 (charts need an explicit dark `Background`); build on `FaultAnalyzer`, `FaultQueries`, `HistoryQueries`, `SelectionService`.
- A device at 1 Hz for 7 days is about 600k rows: aggregation happens in SQL, never by loading raw rows into memory for long windows.
- Observations come from two sources: the PC's radio (every advertisement) and paired phones (sampling windows). Interval statistics are only meaningful per source, so the analysis can be filtered by scanner and says which source it is describing.
- Local-only rules from `CLAUDE.md` apply: no fabricated data (demo data is labelled), absence is never "disconnected".

## Approaches considered

1. **Signal Analysis as the deep-dive screen (chosen by the user).** One screen for identity, verdict and charts; Device History stays the raw timeline.
2. Extend Device History. Less new code, but the page gets crowded and the spec lists Signal Analysis separately.
3. Separate details page and analysis page. Cleaner on paper, but two screens for one question.

## Design

**Navigation.** `SelectionService.OpenAnalysis(deviceId)` plus an `OpenAnalysisRequested` event; `MainViewModel` navigates to the "analysis" page (currently a placeholder). "Analyse" buttons are added to the Fault Detector detail panel, the Inventory selected-device panel and the Live Monitor.

**Engine (pure, in `Application/Diagnostics/SignalAnalyzer.cs`).** Input: bucketed RSSI for the device (average, min, max, count per bucket), an interval histogram, silences (already cut to scanner coverage), and a window. Output, all unit-testable:
- series with moving average (window = N buckets, default about 5% of the span);
- stats: min, max, mean, median, standard deviation; volatility = standard deviation of bucket-to-bucket change;
- distribution: 5 dB bins and the share of time per description (Excellent >= -55, Good, Fair, Weak, Very weak: the same configurable cut-offs the dashboard uses);
- advertisement intervals: median, 95th percentile, maximum, histogram (<1, 1-2, 2-5, 5-10, 10-30, 30-120, >120 s);
- silences: start, length, signal level just before and just after, and flags for "shared with other devices" (from the Fault Detector's correlation);
- signal recovery: after each drop of 10 dB or more below the device's own baseline (median of the preceding hour), the time until it is back within 3 dB; unrecovered drops are reported as such.

**Queries (in `Database`).** `SignalQueries.LoadAsync(deviceId, window, scannerId?)`: bucket query (like `HistoryQueries.GetRssiHistoryAsync`, bucket size chosen from the window: 1 min for 1 h/24 h, 15 min for 7 d/30 d), interval histogram via `LAG` in SQL, silences via the same gap query the Fault Detector uses, plus the device row, identifiers, latest manufacturer/service data and per-scanner observation counts.

**What-is-this panel (offline).** Name (custom/advertised), category, room, workstation; maker (Bluetooth SIG company id, `CompanyIds`); services announced with names for the common 16-bit UUIDs (battery, HID, device information, Eddystone, SmartTag and similar) and the raw UUID otherwise; manufacturer data prefix; address type and whether the identity is by stable address or fingerprint; first/last seen, observation counts per source (PC radio, each phone), connection-test history from phones. Unknown things are shown as unknown, never guessed.

**Look up online.** A button that opens the default browser at a DuckDuckGo search for `"<maker> <advertised name> bluetooth"` (name omitted when it is only the app's generated "Device xxxxxxxx"). A one-line note beside the button says what is sent. The address, room and workstation are never included. Implemented with `Process.Start(UseShellExecute)`; no `HttpClient` anywhere.

**Verdict header.** Runs `FaultAnalyzer` for just this device over the same window, with the same coverage, so the score matches the Worst Offenders row (a shared helper in `FaultQueries` avoids a second implementation).

**Screen layout.** Header (name, verdict band, score, confidence, Look-up and Open-history buttons, window picker 1 h / 24 h / 7 d / 30 d, scanner picker). Left column: the what-is-this panel and the findings/recommendation. Right: signal chart with moving average and silence shading; histogram of signal strength; interval histogram; table of silences and recoveries.

**Demo and verification.** `--demo-shots` renders the page for a fictional device ("DEMO DATA"); a headless `--verify-ui` step opens it; unit tests for `SignalAnalyzer` (moving average, volatility, bins, intervals, recovery incl. unrecovered drops, insufficient data) and one SQL-backed test like `FaultQueriesTests`.

## Left to the builder

Exact chart styling, bucket-size and moving-average defaults, the table of well-known 16-bit service names (a small curated list is enough), and wording of explanations (plain, honest, no distance claims).

## Calls I made (the user has not been asked)

- Search engine for the browser button: DuckDuckGo.
- Recovery thresholds (10 dB drop, back within 3 dB) and 5 dB bins are starting points, adjustable later like the Fault Detector's numbers.
- Interval statistics are shown per source rather than blended.
