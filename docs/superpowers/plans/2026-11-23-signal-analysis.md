# Signal Analysis (device deep-dive) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the "Signal Analysis" placeholder into a per-device deep-dive: identity details, the Fault Detector verdict, and signal/interval/silence/recovery analysis, reachable with an "Analyse" button.

**Architecture:** A pure `SignalAnalyzer` (Application/Diagnostics) computes everything from pre-aggregated input; `SignalQueries` (Database) produces that input with SQL aggregation; a pure `DeviceProfileBuilder` turns stored facts into the "what is this device" panel; `SignalAnalysisViewModel` + a DataTemplate in `Views.xaml` present it. The verdict is `FaultAnalyzer`'s output for the same device and window.

**Tech Stack:** .NET 10, WPF + CommunityToolkit.Mvvm, EF Core 10 SQLite (raw SQL for aggregation), LiveChartsCore.SkiaSharpView.WPF, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-23-signal-analysis-design.md`

## Global Constraints

- Windows app only; no new database tables or migrations; no phone-side change.
- The app makes no network call for lookup: the "Look up online" button only calls `Process.Start` with `UseShellExecute` on a DuckDuckGo search URL (`https://duckduckgo.com/?q=`).
- The search text is `"<maker> <advertised name> bluetooth"`; the advertised name is left out when it is empty or the generated `Device xxxxxxxx`; the Bluetooth address, room and workstation are never included.
- RSSI is never presented as a distance. Descriptions: Excellent >= -55, Good >= -67, Fair >= -78, Weak >= -88, Very weak below (same cut-offs as the dashboard).
- Silences are cut to scanner coverage (state "Running") and to the time the device existed; silences shared by many devices are flagged as environmental, never charged to the device (reuse `FaultAnalyzer` rules).
- Recovery: a drop is a bucket average >= 10 dB below the device's own baseline (median of the preceding 60 minutes of bucket averages, at least 3 buckets); recovered when back within 3 dB; unrecovered drops are reported as unrecovered.
- Distribution bins are 5 dB wide. Advertisement-interval bins: <1, 1-2, 2-5, 5-10, 10-30, 30-120, >120 seconds.
- Bucket size: 60 s for windows up to 36 h, 900 s otherwise. Windows offered: 1 hour, 24 hours, 7 days, 30 days.
- Interval statistics are computed per source (PC radio, each phone) and the page says which scanner it describes.
- Demo screenshots use fictional data labelled DEMO DATA. Dark theme, LiveCharts controls need an explicit `Background`.
- Namespace `BluetoothFleetMonitor.Application` collides with `System.Windows.Application`: write `System.Windows.Application` in Desktop code.
- Backslashes in strings written through shell/Python heredocs get collapsed by the tooling: use forward slashes in paths inside files, or edit files with the Edit tool.

## Review Focus

- Device heard only once or twice in the window: the page shows "Not enough data" (no division by zero, no NaN charts).
- Window with no observations at all (device retired): empty-state message, not an exception.
- Scanner picker set to a scanner that never heard the device: empty-state, not the other scanner's numbers.
- Device name is the generated `Device xxxxxxxx` or has characters like `&`, `#`, quotes or non-ASCII: the lookup URL is correctly encoded and never contains the address.
- Very long window (30 days, 1 Hz): the page loads in a few seconds, because aggregation is done in SQL.

---

### Task 1: SignalAnalyzer (pure engine)

**Files:**
- Create: `dist/Windows/Application/Diagnostics/SignalAnalyzer.cs`
- Test: `dist/Windows/Tests/SignalAnalyzerTests.cs`

**Interfaces:**
- Consumes: `TimeRange(DateTime StartUtc, DateTime EndUtc)` from `FaultAnalyzer.cs`.
- Produces (namespace `BluetoothFleetMonitor.Application.Diagnostics`):
  - `record SignalBucket(DateTime StartUtc, double Average, int Min, int Max, int Count)`
  - `record SignalInput(TimeRange Window, int BucketSeconds, IReadOnlyList<SignalBucket> Buckets, IReadOnlyDictionary<int,int> IntervalSecondCounts, double MaxIntervalSeconds, IReadOnlyList<TimeRange> Silences)` where `IntervalSecondCounts[s]` = number of gaps between consecutive advertisements whose whole-second length is `s`, with key 600 meaning 600 s or more
  - `record SignalStats(int Min, int Max, double Mean, double Median, double StdDev, double Volatility)`
  - `record DistributionBin(int FromDbm, int ToDbm, int Count)`; `record StrengthShare(string Label, double Fraction)`
  - `record IntervalBin(string Label, int Count)`; `record IntervalStats(double MedianSeconds, double P95Seconds, double MaxSeconds, IReadOnlyList<IntervalBin> Bins)`
  - `record SilenceDetail(TimeRange Range, double? LevelBeforeDbm, double? LevelAfterDbm)`
  - `record RecoveryEvent(DateTime DropStartUtc, double DropDb, TimeSpan? RecoveredAfter)`
  - `sealed class SignalAnalysis { bool Sufficient; string? Note; SignalStats? Stats; IReadOnlyList<double> MovingAverage; IReadOnlyList<DistributionBin> Distribution; IReadOnlyList<StrengthShare> Strength; IntervalStats? Intervals; IReadOnlyList<SilenceDetail> Silences; IReadOnlyList<RecoveryEvent> Recoveries; }`
  - `static class SignalAnalyzer { static SignalAnalysis Analyze(SignalInput input, int movingAverageBuckets = 0); static int DefaultMovingAverage(int bucketCount); static string Describe(double rssi); }`

- [ ] **Step 1: Write failing tests** in `SignalAnalyzerTests.cs` (xUnit, like `FaultAnalyzerTests`; helper builds buckets every 60 s from an array of averages). Tests, with exact assertions:
  - `Fewer_than_three_buckets_is_insufficient_with_a_note`: 2 buckets -> `Sufficient == false`, `Note` contains "Not enough data", `Stats == null`, no exception.
  - `Empty_input_is_insufficient_and_does_not_throw`: 0 buckets, empty interval dictionary.
  - `Stats_are_weighted_and_exact_for_min_max`: buckets avg -60/-70/-80 with Min -62/-72/-85, Max -58/-68/-78, counts 10/10/20 -> `Stats.Min == -85`, `Max == -58`, `Mean == -72.5` (weighted by count), `Median == -70` (weighted median of bucket averages).
  - `Moving_average_has_one_value_per_bucket_and_smooths`: alternating -60/-80 x 20 with window 4 -> every value (after the first 3) is `-70` within 0.01; length equals bucket count.
  - `DefaultMovingAverage_is_about_five_percent_with_a_floor_of_three`: 40 -> 3, 200 -> 10, 1000 -> 50.
  - `Volatility_is_the_standard_deviation_of_bucket_to_bucket_change`: steady series -> 0; alternating +/-10 -> 20 change magnitude -> `Volatility` between 19 and 21 (std of changes +20,-20,...).
  - `Distribution_uses_5_dB_bins_and_strength_shares_sum_to_one`: shares sum to 1.0 within 0.001; bin edges multiples of 5; -50 counted Excellent, -90 counted Very weak.
  - `Describe_matches_the_dashboard_cutoffs`: -55 Excellent, -56 Good, -67 Good, -68 Fair, -78 Fair, -79 Weak, -88 Weak, -89 Very weak.
  - `Interval_median_p95_and_bins_come_from_the_second_histogram`: counts {1:50, 2:30, 5:15, 600:5} -> median 1 s (within one second), p95 at most 5 s, `MaxSeconds` equals the input max, bin "<1" has 0, "1-2" has 80, "2-5" has 0, "5-10" has 15, ">120" has 5. (Define bin membership: `<1` is s=0; `1-2` is s in [1,2); `2-5` is [2,5); `5-10` is [5,10); `10-30`; `30-120`; `>120` is s >= 120... choose half-open [a,b) and keep ">120" for s >= 120; document in the test.)
  - `Silence_details_carry_the_level_before_and_after`: one silence between bucket 10 and 20 with averages -60 before, -72 after -> `LevelBeforeDbm == -60`, `LevelAfterDbm == -72`; silence at the very start of the window has `LevelBeforeDbm == null`.
  - `Recovery_is_measured_from_a_ten_dB_drop_until_within_three_dB_of_baseline`: 70 buckets at -60, then 5 buckets at -75, then back to -61 -> exactly one `RecoveryEvent`, `DropDb` about 15, `RecoveredAfter == 5 minutes`.
  - `A_drop_that_never_recovers_is_reported_as_unrecovered`: -60 x 70 then -80 to the end -> one event with `RecoveredAfter == null`.
  - `Small_wobble_is_not_a_drop`: -60 +/- 4 for 100 buckets -> no recoveries.

- [ ] **Step 2: Run to confirm failure** — `dotnet test Tests/BluetoothFleetMonitor.Tests.csproj --filter FullyQualifiedName~SignalAnalyzer` (from `dist/Windows`). Expected: compile errors / FAIL (types missing).

- [ ] **Step 3: Implement** `SignalAnalyzer.cs` with the exact types above. Approach: mean weighted by `Count`; median = weighted median of bucket averages; standard deviation = population std of bucket averages; volatility = std of successive differences of bucket averages; baseline for recovery = median of bucket averages in the preceding 60 minutes (needs at least 3 buckets, else skip); drop starts when avg <= baseline - 10, ends at first bucket with avg >= baseline - 3, `RecoveredAfter` = that bucket start minus drop start; no overlapping drops (a drop stays open until recovery). Interval median/p95 walk the cumulative second histogram. Silence levels use the nearest bucket before the silence start and after its end.

- [ ] **Step 4: Run tests** — same command. Expected: all `SignalAnalyzer*` tests PASS and the whole suite still passes (`dotnet test Tests/BluetoothFleetMonitor.Tests.csproj`).

- [ ] **Step 5: Commit** — `git add dist/Windows/Application/Diagnostics/SignalAnalyzer.cs dist/Windows/Tests/SignalAnalyzerTests.cs && git commit -m "Signal analysis engine"` (add the Co-Authored-By line from the session reminder).

---

### Task 2: SignalQueries and coverage-aware silences

**Files:**
- Create: `dist/Windows/Database/SignalQueries.cs`
- Modify: `dist/Windows/Application/Diagnostics/FaultAnalyzer.cs` (expose `CutToCoverage`), `dist/Windows/Database/FaultQueries.cs` (make `LoadCoverageAsync(TimeRange window, CancellationToken)` public instance method; add `DiagnoseAsync`)
- Test: `dist/Windows/Tests/SignalQueriesTests.cs`

**Interfaces:**
- Consumes: Task 1 records; `TimeRange`; `FaultQueries`, `AnalyzerSettings`, `DeviceDiagnosis`; `IDbContextFactory<FleetDbContext>` (test Factory pattern from `FaultQueriesTests`).
- Produces:
  - `FaultAnalyzer.CutToCoverage(IReadOnlyList<TimeRange> silences, IReadOnlyList<TimeRange> coverage, TimeRange window, DateTime? firstSeenUtc, int minGapSeconds) -> IReadOnlyList<TimeRange>` (public static; the existing private logic in `Analyze` calls it)
  - `FaultQueries.LoadCoverageAsync(TimeRange window, CancellationToken ct = default) -> Task<IReadOnlyList<TimeRange>>` and `FaultQueries.DiagnoseAsync(Guid deviceId, TimeRange window, AnalyzerSettings s, CancellationToken ct = default) -> Task<DeviceDiagnosis?>` (loads the whole fleet like the Fault Detector, returns that device's diagnosis, null if the device was not heard in the window)
  - `record ScannerChoice(Guid? Id, string Name, long Observations)` (Id null = all scanners)
  - `SignalQueries(IDbContextFactory<FleetDbContext> factory, FaultQueries faults)` with `LoadAsync(Guid deviceId, TimeRange window, Guid? scannerId, CancellationToken ct = default) -> Task<SignalInput>` and `ScannersAsync(Guid deviceId, TimeRange window, CancellationToken ct = default) -> Task<IReadOnlyList<ScannerChoice>>` (first element is "All scanners")

- [ ] **Step 1: Write failing tests** in `SignalQueriesTests.cs` (in-memory SQLite, migrated; seed like `FaultQueriesTests`: one device heard every 30 s for 6 h by scanner A with a 20-minute dropout while a "Running" scanner event covers it, plus scanner events marking a 1-hour scanner outage):
  - `Buckets_are_60_seconds_for_a_24_hour_window_and_900_for_7_days`: `BucketSeconds` 60 / 900; bucket counts match (about 120 per hour of data / 4 per hour).
  - `Interval_histogram_counts_whole_second_gaps`: all gaps 30 s except one 20-minute gap -> `IntervalSecondCounts[30]` equals the number of normal gaps, `IntervalSecondCounts[600]` is 1, `MaxIntervalSeconds` about 1200.
  - `Silences_are_cut_to_scanner_coverage`: the 20-minute dropout inside coverage appears (about 20 min); a silence inside the scanner outage does not.
  - `Scanner_filter_only_counts_that_scanners_observations`: observations from scanner B added; `LoadAsync(..., scannerId: A)` bucket counts exclude B; `ScannersAsync` lists "All scanners" then A and B with correct observation counts.
  - `Unknown_device_or_empty_window_returns_empty_input_not_an_exception`: zero buckets, empty dictionary, no silences.
  - `DiagnoseAsync_matches_the_Fault_Detector_row_for_the_same_device`: result `Health` equals `FaultAnalyzer.Analyze(...)` for that device from `FaultQueries.LoadAsync`.

- [ ] **Step 2: Run to confirm failure** — `dotnet test ... --filter FullyQualifiedName~SignalQueries`. Expected: FAIL (compile).

- [ ] **Step 3: Implement** `CutToCoverage` by moving the existing clip/intersect logic out of `FaultAnalyzer.Analyze` (behaviour must not change; existing `FaultAnalyzerTests` must still pass). Implement `SignalQueries` with raw SQL like `HistoryQueries`/`FaultQueries`: bucket query `GROUP BY CAST(strftime('%s', TimestampUtc) AS INTEGER) / {bucketSeconds}` with `AVG/MIN/MAX/COUNT` and optional `AND ScannerId = {scannerId}`; interval histogram with `LAG` over `TimestampUtc` for that device (and scanner if filtered), `MIN(CAST(gapSeconds AS INTEGER), 600)` grouped; maximum gap via `MAX`; silences from the gap query (> 120 s) plus the edge silences (as `FaultQueries.LoadAsync` does), then `CutToCoverage` with `faults.LoadCoverageAsync(window)` and the device's `FirstSeenUtc`.

- [ ] **Step 4: Run tests** — `dotnet test Tests/BluetoothFleetMonitor.Tests.csproj`. Expected: PASS, including every earlier test.

- [ ] **Step 5: Commit** — files above; message "Signal queries, coverage cut helper, per-device diagnosis".

---

### Task 3: Device profile and online lookup link

**Files:**
- Create: `dist/Windows/Application/Diagnostics/DeviceProfile.cs`
- Modify: `dist/Windows/Database/SignalQueries.cs` (add `LoadProfileAsync`)
- Test: `dist/Windows/Tests/DeviceProfileTests.cs`

**Interfaces:**
- Consumes: `CompanyIds.Describe(ushort)` (Application/Identity), `IdentifierTypes`, `AddressKind`.
- Produces (namespace `BluetoothFleetMonitor.Application.Diagnostics`):
  - `static class ServiceNames { static string Describe(Guid uuid) }` — curated names for 16-bit UUIDs inside the Bluetooth base UUID (180A Device Information, 180F Battery, 1812 Human Interface Device, 1800 Generic Access, 1801 Generic Attribute, 181A Environmental Sensing, 180D Heart Rate, FEAA Eddystone, FD5A Samsung SmartTag, FE2C Google Fast Pair, FD6F Exposure Notification, FEF3 Google, FE9F Google) else the full UUID text
  - `record DeviceProfileInput(string DisplayName, string? AdvertisedName, string Category, string? Room, string? Workstation, string? ManufacturerData, string? ServiceUuids, AddressKind AddressKind, bool HasStableAddress, bool HasFingerprint, DateTime? FirstSeenUtc, DateTime? LastSeenUtc, long ObservationCount, IReadOnlyDictionary<string,long> ObservationsBySource)`
  - `record ProfileLine(string Label, string Value)`; `record DeviceProfile(string Title, string? Maker, IReadOnlyList<ProfileLine> Lines, string? LookupQuery)`
  - `static class DeviceProfileBuilder { static DeviceProfile Build(DeviceProfileInput i); }` and `static class LookupLink { static string? Query(string? maker, string? advertisedName); static string Url(string query); }`
  - `SignalQueries.LoadProfileAsync(Guid deviceId, CancellationToken ct = default) -> Task<DeviceProfileInput?>` (null if no such device)

- [ ] **Step 1: Write failing tests**:
  - `Maker_is_taken_from_the_first_manufacturer_block`: `ManufacturerData` "0059:0102030405" -> `Maker == "Nordic Semiconductor"`; "FFFF:00" -> "Company 0xFFFF"; null -> `Maker == null` and a line "Maker" saying "Unknown".
  - `Services_are_named_when_known_and_raw_when_not`: UUID `0000180f-0000-1000-8000-00805f9b34fb` -> "Battery"; an unknown 128-bit UUID -> its own text.
  - `Address_kind_and_identity_are_explained_without_guessing`: `AddressKind.Unknown` -> value contains "unknown"; Public + HasStableAddress -> contains "stable".
  - `Counts_per_source_are_listed`: sources {"Windows": 100, "Android": 40} -> two lines containing "100" and "40".
  - `LookupQuery_uses_maker_and_name_and_never_the_address`: ("Logitech", "MX Keys") -> `"Logitech MX Keys bluetooth"`; name "Device a1b2c3d4" -> `"Logitech bluetooth"`; both null -> `null`; the query never matches `[0-9A-Fa-f]{12}`.
  - `Url_is_encoded`: query `Acme & Co #1 "x" é bluetooth` -> URL starts with `https://duckduckgo.com/?q=`, contains `%26` and `%23` and `%22`, no raw space.
  - `LoadProfileAsync_reads_the_device_and_its_per_source_counts` (SQLite in-memory like Task 2): device with Windows and Android observations -> counts per source `Windows`/`Android`, `ManufacturerData` of the latest observation, `HasStableAddress` true when a StableAddress identifier exists.

- [ ] **Step 2: Run to confirm failure** — `--filter FullyQualifiedName~DeviceProfile`. Expected: FAIL (compile).

- [ ] **Step 3: Implement** the builder (pure), `LookupLink` (`Uri.EscapeDataString` for the query) and `LoadProfileAsync` (device row, identifiers, newest observation's manufacturer/service data, `GROUP BY Source` counts). Unknown things read "Unknown", nothing is guessed.

- [ ] **Step 4: Run tests** — whole suite. Expected: PASS.

- [ ] **Step 5: Commit** — "Device profile and online lookup link".

---

### Task 4: Page, navigation and entry buttons

**Files:**
- Create: `dist/Windows/Desktop/ViewModels/SignalAnalysisViewModel.cs`
- Modify: `dist/Windows/Desktop/Services/SupportServices.cs` (`SelectionService`), `dist/Windows/Desktop/ViewModels/MainViewModel.cs`, `dist/Windows/Desktop/App.xaml.cs` (DI), `dist/Windows/Desktop/Views.xaml` (new DataTemplate + "Analyse" buttons in the Fault Detector, Inventory and Live Monitor templates), `dist/Windows/Desktop/ViewModels/FaultsViewModel.cs` / `InventoryViewModel.cs` / `LiveMonitorViewModel.cs` (command `AnalyseCommand`)
- Test: `dist/Windows/Tests/SelectionServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1-3 (`SignalAnalyzer.Analyze`, `SignalQueries`, `DeviceProfileBuilder`, `LookupLink`, `FaultQueries.DiagnoseAsync`), `FaultsViewModel.Settings`, `SelectionService.OpenHistory` pattern.
- Produces:
  - `SelectionService.OpenAnalysis(Guid id)` and event `OpenAnalysisRequested : Action<Guid>`; `MainViewModel` handles it with `Navigate("analysis")` and replaces the `Todo("analysis", ...)` entry with `Add("analysis", "Signal Analysis", analysis)`
  - `SignalAnalysisViewModel(SignalQueries, FaultQueries, FaultsViewModel faults, SelectionService)` with `Task ShowAsync(Guid deviceId)`; bindable: `Title`, `Verdict`, `VerdictBrush`, `Recommendation`, `Findings`, `ProfileLines`, `Windows` (1 hour/24 hours/7 days/30 days), `Window`, `Scanners`, `SelectedScanner`, `Stats` text lines, `SignalSeries` (signal + moving average), `DistributionSeries`, `IntervalSeries`, `Silences` rows, `Recoveries` rows, `Message` (empty-state/insufficient-data text), commands `LookUpOnlineCommand`, `OpenHistoryCommand`, `RefreshCommand`
  - `LookUpOnlineCommand` calls `Process.Start(new ProcessStartInfo(url){UseShellExecute=true})` only with `LookupLink.Url(query)`; no `HttpClient` anywhere in Desktop

- [ ] **Step 1: Write the failing test** `OpenAnalysis_raises_the_event_with_the_device_id_and_remembers_the_selection` (SelectionService is plain: assert the event argument and `SelectedDeviceId`).

- [ ] **Step 2: Run** `--filter FullyQualifiedName~SelectionService`. Expected: FAIL (member missing).

- [ ] **Step 3: Implement** `SelectionService.OpenAnalysis`; the view-model (loads in `Task.Run`, results applied on the UI thread; refreshes when `Window` or `SelectedScanner` changes; empty states per Review Focus; chart axes/background styled like `DashboardViewModel`); the XAML layout from the spec (header with verdict, window and scanner pickers and buttons; left column profile + findings + recommendation; right column signal chart with moving average, signal distribution, interval histogram, silences/recoveries tables, each chart with `Background="{StaticResource Panel}"`); DI registrations (`SignalQueries`, `SignalAnalysisViewModel`); "Analyse" buttons that call `SelectionService.OpenAnalysis` with the selected device.

- [ ] **Step 4: Build and verify** — `dotnet build Desktop/BluetoothFleetMonitor.Desktop.csproj` (stop any running `BluetoothFleetMonitor` process first, it locks DLLs); `dotnet test Tests/BluetoothFleetMonitor.Tests.csproj`. Expected: build succeeded, all tests pass.

- [ ] **Step 5: Commit** — "Signal Analysis page and Analyse buttons".

---

### Task 5: Demo screenshot, headless check, docs, release v0.4.0

**Files:**
- Modify: `dist/Windows/Desktop/Headless.cs` (demo page + verify step), `dist/Windows/Directory.Build.props` and Android `build.gradle.kts`/`Screens.kt` About text (version 0.4.0, versionCode 6), `README.md`, `dist/IMPLEMENTATION_STATUS.md`, `dist/TASKS.md`
- Create: `docs/screenshots/windows-signal-analysis.png`

**Interfaces:**
- Consumes: `SignalAnalysisViewModel.ShowAsync`, `SignalAnalyzer`, `DeviceProfileBuilder` (the demo feeds fictional `SignalInput`/`DeviceProfileInput`; add `SignalAnalysisViewModel.LoadDemo(SignalAnalysis, DeviceProfile, DeviceDiagnosis)` that sets `IsDemo` and the DEMO DATA label, like `FaultsViewModel.LoadAnalysis`)
- Produces: demo page `("analysis", "windows-signal-analysis.png", 1100)` in `DemoShotsAsync`; `--verify-ui` opens the analysis page without exceptions.

- [ ] **Step 1: Add the demo** — fictional device with a 24-hour series including one 18-minute silence and one unrecovered-then-recovered drop; `LoadDemo`; page added to `DemoShotsAsync` (never uses real data; address-like text must not appear).

- [ ] **Step 2: Run the demo** — `Desktop.exe --demo-shots --out <dir>` (needs `--out`); read `windows-signal-analysis.png`; confirm the DEMO DATA label, dark charts (no white bitmaps), readable text, no real addresses. Copy to `docs/screenshots/`.

- [ ] **Step 3: Verify headless** — copy the published exe to a temp folder, run `--verify-ui`; expected exit code 0.

- [ ] **Step 4: Docs and version** — README feature table and screenshot, honest status (page verified on demo data and unit tests; not yet on weeks of real scans), IMPLEMENTATION_STATUS section, TASKS checkboxes; bump to 0.4.0.

- [ ] **Step 5: Build, release, install** — run all Windows and Android tests; publish the exe (`-p:EnableCompressionInSingleFile=true -p:DebugType=none`), build the installer (`MSYS_NO_PATHCONV=1 ISCC.exe /DAppVersion=0.4.0 dist/Installer/BluetoothFleetMonitor.iss`), assemble the APK, scan the staged diff for emails/addresses/local paths, commit, push, `gh release create v0.4.0 --prerelease` with Setup.exe, exe, APK and `SHA256SUMS.txt`, then install the new Setup.exe on the PC (per user, `/VERYSILENT /CURRENTUSER /TASKS=desktopicon`) and `adb install -r` the APK on the phone.

---

## Self-review

- Spec coverage: signal chart + moving average, min/max/mean/median, volatility, distribution, intervals, silences, recovery, strength descriptions (Task 1, shown in Task 4); queries and per-scanner filter (Task 2); what-is-this panel, offline details and online button (Task 3); verdict header and navigation/entry buttons (Tasks 2 and 4); demo, verification, docs (Task 5). Non-goals respected: no migrations, no phone change, no network calls from the app.
- Types: `TimeRange`, `SignalBucket`, `SignalInput`, `SignalAnalysis`, `DeviceProfile`, `DeviceProfileInput`, `ScannerChoice` are defined once (Tasks 1-3) and used with the same names in Task 4.
- Proportion: the plan is about twice the spec's length because it lists test assertions; no function bodies are written out.
