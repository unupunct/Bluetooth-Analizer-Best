# Test Plan

Run everything from `dist/Windows`:

```powershell
dotnet build
dotnet test
```

## 1. Automated tests (xUnit, no hardware, 67 tests)

| File | Covers |
|---|---|
| `DatabaseTests` | migration on empty DB, all 10 tables, required indexes, duplicate observation id rejected, UTC round-trip, integrity check, backup |
| `IdentityResolverTests` | stable address match, unknown stable address = new device, fingerprint match only when unique, name alone never matches, same-name devices stay separate, ambiguity creates a new device, repeated rotating address, Unknown address kind untrusted, fingerprint ignores volatile bytes/UUID order |
| `PipelineTests` | batch size limit, overflow counted + recorded, original timestamp preserved + processed time separate, one new device per address within a batch, flush on cancel, write retry and permanent-failure event |
| `StoreTests` | device + identifiers created, duplicate id once (across and within batches), stale upload cannot overwrite current state, RSSI min/max/avg, processed vs observed time, known-device loading, events and scanner rows |
| `DeviceAdminTests` | CSV round-trip and formula neutralising, edit details, CSV import by Id / Address / missing columns, merge (stats, identifiers, audit), self-merge refused, split, history buckets, longest silent gap |
| `ScannerHealthTests` | running / deaf-with-driver-hint / recovery, unexpected stop opens a gap, user stop does not, adapter unavailable, address-kind classification |
| `PresenceStatusTests` | Detected, Missing, Uncertain for stopped/deaf/gap/never-heard |

Simulated data appears **only** in tests (and in `--verify-ui`, below) and is always labelled.

## 2. Headless self-tests of the built app

Run the exe produced by `dotnet build` (path: `Desktop/bin/Debug/net10.0-windows10.0.19041.0/BluetoothFleetMonitor.Desktop.exe`):

```powershell
# Renders all 14 pages off-screen from TEST data and counts WPF binding errors. Writes PNGs + report.txt. Exit code 0 = pass.
.\BluetoothFleetMonitor.Desktop.exe --verify-ui --out "C:\temp\verify-ui" --data "C:\temp\verify-data"

# Runs the REAL scanner + pipeline in Passive mode into a throw-away DB and writes a report.
# Exit codes: 0 stored data, 2 heard nothing (see report), 3 heard but stored nothing.
.\BluetoothFleetMonitor.Desktop.exe --scan-test --seconds 20 --out "C:\temp\scan-report.txt" --data "C:\temp\scan-data"
```

Neither touches the real database. `--verify-ui` never captures the screen; it renders WPF visuals into bitmaps.

## 3. Windows hardware validation checklist (ticked only where actually observed)

- [x] `--scan-test` passes (exit 0) with a real adapter on Windows 11 (2026-11-20, TP-Link UB500 V3.6: 1,067 advertisements, 56 devices)
- [ ] Same on Windows 10 (with the manufacturer driver installed)
- [x] Without the vendor driver: app shows "running but hearing nothing" with the driver hint (observed on Windows 11 with the generic driver)
- [ ] Unplug the adapter while scanning: state becomes Interrupted/AdapterUnavailable, a coverage gap is recorded; replug: scanning resumes by itself
- [ ] Turn Bluetooth off then on in Windows settings: same behaviour
- [~] Lovense (`LVS-*`) devices appear (1 seen in a 30 s scan); one-identity-over-30-minutes not yet checked
- [ ] A device with a rotating address keeps one identity (or is reported as new, never silently merged wrongly)
- [ ] 30+ minutes continuous scan: UI stays responsive, no queue overflow events at normal load, memory stable
- [ ] Kill the app mid-scan, restart: database opens, no corruption, backup exists after a migration
- [ ] Add a second device with the same advertised name: it stays a separate inventory row

## 4. Android (Phase 2)

```powershell
cd dist\Android\BluetoothFleetMonitor
.\gradlew.bat :app:testDebugUnitTest      # 28 JVM tests of the pure logic (fingerprint contract, aggregation, presence, health, RSSI)
.\gradlew.bat :app:assembleDebug          # builds app\build\outputs\apk\debug\app-debug.apk
```

Install on a phone (USB debugging on): `adb install -r <apk>`. Needs JDK 17 + Android SDK 35 (`dist\Scripts\Setup-DevEnvironment.ps1`).

Phone checklist (ticked only where observed on a real phone):

- [x] First launch: permission prompt appears, grant works, Scanner tab turns green (Galaxy A52s, Android 14)
- [x] Continuous mode: notification visible, devices stored, and the screen-off scan now continues as a known-devices scan (verified 14:41-14:42; a 10+ minute run is still not done)
- [ ] Turn Bluetooth off during a scan: status Interrupted + a coverage gap; turn it on: scan resumes by itself
- [ ] Revoke the permission while scanning: status explains it, no crash
- [x] Force-stop the app mid-scan, reopen: a gap is recorded and nothing claims coverage for the missing time (Galaxy A52s)
- [~] Inspection: session at "Studio 01" started and tagged 1,601 windows (verified); ending it and the saved-sessions list not yet seen
- [~] Background mode: results keep arriving after the process is killed (verified 2026-11-23, filter = 512 known devices); with the screen off and with a name filter not yet tested
- [ ] The same device on the phone and on Windows has the same fingerprint (compare the Fleet fingerprints once Phase 3 sync exists)
- [ ] Battery: scan for an hour with defaults, note the drain

## 5. Later phases

API authentication / idempotency / offline recovery tests (Phase 3), fault-detection tests with simulated data clearly marked (Phase 4), active-test safety tests (Phase 5), 18-point acceptance checklist from the specification (Phase 6).
