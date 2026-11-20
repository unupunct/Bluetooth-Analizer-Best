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

## 3. Hardware validation checklist (a person must tick these; none are ticked)

- [ ] `--scan-test` passes (exit 0) with a real adapter on Windows 11
- [ ] Same on Windows 10 (with the manufacturer driver installed)
- [ ] Without the driver on Windows 10: app shows "running but hearing nothing" with the driver hint
- [ ] Unplug the adapter while scanning: state becomes Interrupted/AdapterUnavailable, a coverage gap is recorded; replug: scanning resumes by itself
- [ ] Turn Bluetooth off then on in Windows settings: same behaviour
- [ ] Lovense (`LVS-*`) devices appear and keep one identity across 30 minutes
- [ ] A device with a rotating address keeps one identity (or is reported as new, never silently merged wrongly)
- [ ] 30+ minutes continuous scan: UI stays responsive, no queue overflow events at normal load, memory stable
- [ ] Kill the app mid-scan, restart: database opens, no corruption, backup exists after a migration
- [ ] Add a second device with the same advertised name: it stays a separate inventory row

## 4. Later phases

Android scanner tests (Phase 2), API authentication / idempotency / offline recovery tests (Phase 3), fault-detection tests with simulated data clearly marked (Phase 4), active-test safety tests (Phase 5), 18-point acceptance checklist from the specification (Phase 6).
