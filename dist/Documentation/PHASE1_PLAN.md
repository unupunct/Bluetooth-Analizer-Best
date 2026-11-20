# Phase 1 - Core Windows Application Implementation Plan

> Execution: native (one implementer, then a whole-branch review). Steps use checkboxes.

**Goal:** A Windows 10/11 WPF app that passively scans BLE, stores observations in SQLite, identifies devices without trusting MAC alone, and shows Dashboard, Live Monitor, Inventory, Device History and Event Log.

**Architecture:** Layers point inward (Desktop/Bluetooth/Database -> Application -> Domain). `IBleScanner` (Application) hides `BluetoothLEAdvertisementWatcher`. Scanner -> bounded `Channel` -> batched writer -> SQLite. Pure logic (identity, health of the pipeline) lives in Application and is unit-tested without hardware.

**Tech Stack:** .NET 10, WPF + CommunityToolkit.Mvvm, EF Core 10.0.x SQLite, Serilog, LiveCharts2, xUnit.

**Spec:** `dist/Documentation/SPECIFICATION.txt` (sections 6-9, 12, 23, 26, 27). Task list: `dist/TASKS.md`.

## Global Constraints

- Windows target `net10.0-windows10.0.19041.0`, min OS `10.0.17763.0` (Win10 1809 + Win11); other projects `net10.0`.
- EF Core / Microsoft.Extensions packages pinned `10.0.x` (11.x fails NU1202).
- Scanning is **Passive by default**; Active only by explicit setting. No connect/pair/GATT in Phase 1.
- Absence from a scan is never a confirmed disconnection. Periods without scanner coverage are not device failures.
- Observations keep three UTC timestamps: observed, uploaded, processed.
- Never silently drop observations: queue overflow is recorded as an `ApplicationEvent`.
- Identity never MAC-only; never auto-merge on equal names.
- No simulated data in production views (tests only).
- Paths relative; runtime data under `dist/Windows/Desktop/data` (config `appsettings.json`), logs under `Logs`.
- Commits as `unupunct <65507390+unupunct@users.noreply.github.com>`.

## Review Focus

- Scanner running but **zero advertisements** (Win10 generic driver): UI must say so, not "no devices nearby".
- Same device, rotating random address: must not create a new device every address change.
- Two different devices advertising the same name (e.g. `LVS-Lush`): must stay separate.
- Duplicate observation id uploaded twice: stored once.
- Queue full during an advert burst: overflow counted and recorded, nothing silent.

## File Structure

| Project | Files |
|---|---|
| Domain | `Entities/*.cs` (Device, DeviceIdentifier, Scanner, Observation, PresenceEvent, ConnectionTest, InspectionSession, FaultReport, UsbEvent, ApplicationEvent), `Enums.cs` |
| Application | `Scanning/IBleScanner.cs`, `Scanning/BleAdvertisement.cs`, `Identity/IdentityResolver.cs`, `Pipeline/ObservationPipeline.cs`, `Abstractions/IFleetStore.cs` |
| Database | `FleetDbContext.cs`, `Migrations/*`, `EfFleetStore.cs`, `DatabaseMaintenance.cs` (backup + integrity) |
| Bluetooth | `WindowsBleScanner.cs` |
| Desktop | `App.xaml(.cs)` host/DI, `appsettings.json`, `Shell/`, `Views/*`, `ViewModels/*`, `Theme.xaml` |
| Tests | one test file per unit above |

---

### Task 1: Domain model and database

**Files:** Create `Domain/Entities/*.cs`, `Domain/Enums.cs`, `Database/FleetDbContext.cs`, `Database/Migrations/*`, `Database/DatabaseMaintenance.cs`; Test `Tests/DatabaseTests.cs`.

**Produces:** entities exactly per spec section 23 (`Device`, `DeviceIdentifier`, `Scanner`, `Observation`, ...); `FleetDbContext(DbContextOptions<FleetDbContext>)` with `DbSet` per entity; `DatabaseMaintenance.BackupAsync(string dbPath) -> Task<string>`, `CheckIntegrityAsync(DbContext) -> Task<bool>`.

- [ ] Write failing tests: `Migrates_empty_database`, `Observation_unique_on_id_is_enforced`, `Indexes_exist_for_device_scanner_timestamp`, `Integrity_check_passes`, `Backup_creates_copy`.
- [ ] Run `dotnet test --filter DatabaseTests`; expect FAIL (types missing).
- [ ] Implement entities, `FleetDbContext` with indexes (DeviceId, ScannerId, TimestampUtc, EventType, LocationLabel, Category), initial migration `InitialCreate`.
- [ ] Run tests; expect PASS. Commit.

### Task 2: Identity resolver

**Files:** Create `Application/Identity/IdentityResolver.cs`, `Application/Scanning/BleAdvertisement.cs`; Test `Tests/IdentityResolverTests.cs`.

**Produces:** `record BleAdvertisement(DateTime ObservedUtc, ulong Address, AddressKind AddressKind, short Rssi, string? Name, IReadOnlyList<ManufacturerBlock> Manufacturer, IReadOnlyList<Guid> ServiceUuids, byte[] Payload)`; `IdentityResolver.Resolve(BleAdvertisement ad, IReadOnlyList<KnownDevice> known) -> IdentityDecision(Guid? DeviceId, string Fingerprint, double Confidence, string Reason)`.

- [ ] Failing tests: `Public_address_matches_known_device`; `Rotating_random_address_matches_by_fingerprint_when_unique`; `Same_name_alone_never_matches`; `Two_devices_same_name_stay_separate`; `Ambiguous_fingerprint_creates_new_device`; `Unknown_returns_new_decision`.
- [ ] Implement: exact address match for Public/RandomStatic -> confidence 1.0; for resolvable/non-resolvable private: fingerprint = company id + manufacturer payload stable prefix + sorted service UUIDs, match only if it contains manufacturer data or UUIDs AND matches exactly one known device -> 0.7; name is a tiebreaker, never the key.
- [ ] Tests pass. Commit.

### Task 3: Observation pipeline

**Files:** Create `Application/Pipeline/ObservationPipeline.cs`, `Application/Abstractions/IFleetStore.cs`, `Database/EfFleetStore.cs`; Test `Tests/PipelineTests.cs`.

**Produces:** `IFleetStore.SaveBatchAsync(IReadOnlyList<ProcessedObservation>, CancellationToken)`, `IFleetStore.RecordEventAsync(ApplicationEvent)`; `ObservationPipeline(IFleetStore, IdentityResolver, capacity, batchSize, flushInterval)` with `bool TryEnqueue(BleAdvertisement)`, `long Overflowed`, `Task RunAsync(CancellationToken)`.

- [ ] Failing tests: `Batches_are_written_in_one_transaction`, `Overflow_is_counted_and_recorded_as_event`, `Duplicate_observation_id_stored_once`, `Original_timestamp_preserved_and_processed_time_set`, `Stops_gracefully_and_flushes`.
- [ ] Implement bounded `Channel` (DropWrite + counter), batching writer, event on overflow (rate-limited). Tests pass. Commit.

### Task 4: Windows BLE scanner

**Files:** Create `Bluetooth/WindowsBleScanner.cs`; Test `Tests/ScannerHealthTests.cs` (pure state logic only).

**Produces:** `IBleScanner { ScannerState State; event Action<BleAdvertisement> Advertisement; event Action<ScannerStatus> StatusChanged; Task StartAsync(ScanMode mode); Task StopAsync(); }`, `ScanMode { Passive, Active }`, `ScannerStatus(State, Message, LastAdvertisementUtc)`.

- [ ] Failing tests for `ScannerHealth`: running with no advert for N seconds => `RunningNoAdvertisements` (driver hint); stopped by system => `Interrupted` recorded; recovery => `Running`.
- [ ] Implement watcher (Passive default, `AllowExtendedAdvertisements` off), map args to `BleAdvertisement`, restart on `Stopped` abort, adapter-absent state. Hardware check: run, see real devices in DB. Commit.

### Task 5: WPF shell and screens

**Files:** Create `Desktop/Theme.xaml`, `Desktop/Shell/MainWindow.xaml`, `Desktop/Views/{Dashboard,LiveMonitor,Inventory,History,EventLog}View.xaml`, matching `ViewModels/*`, `Desktop/appsettings.json`.

**Consumes:** `IBleScanner`, `ObservationPipeline`, `FleetDbContext`.

- [ ] Dark theme, left navigation of the 14 sections (unbuilt ones marked "in progress"), Dashboard (total, detected now, missing, scanner state, last update, event feed), Live Monitor (search, filters, sort, pause), Inventory (rename, room, workstation, notes, category, CSV import/export), History (RSSI chart, period selector), Event Log.
- [ ] All DB access off the UI thread; collections updated via dispatcher in batches.
- [ ] Headless `--verify-ui` self-test (offscreen render, no screen capture). Commit.

### Task 6: Logging, docs, verification

- [ ] Serilog to `Logs/`, crash handler, appsettings.json.
- [ ] Write `TECHNICAL_ARCHITECTURE.md`, `IMPLEMENTATION_STATUS.md`, `TEST_PLAN.md`, `KNOWN_LIMITATIONS.md`; tick `TASKS.md` only for verified items.
- [ ] `dotnet build` + `dotnet test` green; hardware run recorded honestly. Commit and push.
