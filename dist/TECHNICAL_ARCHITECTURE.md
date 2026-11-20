# Technical Architecture (Windows, Phase 1)

## Layers

```
Desktop (WPF, MVVM)  ->  Bluetooth, Database, Diagnostics*, UsbMonitoring*   ->  Application  ->  Domain
                                                                                  (*empty so far)
```

| Project | Target | Responsibility |
|---|---|---|
| Domain | net10.0 | Entities matching spec section 23, enums |
| Application | net10.0 | Interfaces and pure logic: `IBleScanner`, `ScannerHealth`, `PresenceStatus`, `IdentityResolver`, `ObservationPipeline`, `IFleetStore`, CSV |
| Database | net10.0 | EF Core + SQLite: `FleetDbContext`, migrations, `EfFleetStore`, `DeviceAdminService`, `HistoryQueries`, backup / integrity |
| Bluetooth | net10.0-windows10.0.19041.0 | `WindowsBleScanner` over `BluetoothLEAdvertisementWatcher` |
| Desktop | net10.0-windows10.0.19041.0 | WPF app, DI host, view-models, theme, headless self-tests |
| Tests | net10.0-windows10.0.19041.0 | xUnit |

Logic that decides anything important (identity, presence, scanner health) is in Application with no UI or hardware dependency, so it is unit-tested.

## Data flow

```
BluetoothLEAdvertisementWatcher thread
   -> WindowsBleScanner.Advertisement           (never blocks)
   -> ObservationPipeline.TryEnqueue            (bounded Channel; full => counted + recorded as ApplicationEvent)
   -> pipeline loop: IdentityResolver per advertisement
   -> EfFleetStore.SaveBatchAsync               (one transaction; idempotent on observation id)
   -> BatchStored event -> UI dispatcher -> LiveRegistry.ApplyBatch   (one UI hop per batch)
```

The UI reads from `LiveRegistry` (in memory, loaded once at start-up). History and Event Log read SQLite directly. The UI thread never writes to the database.

## Key decisions

- **Passive by default.** The scanner only listens. Active is a persisted opt-in. No code path connects, pairs or sends GATT commands in Phase 1.
- **Three timestamps** on every observation: observed (never rewritten), uploaded (null for local), processed. "Current" state (`LastSeen`, `LastRssi`) only moves forward, so a late upload of old data cannot make a device look freshly seen.
- **Identity.** Stable address (public / random static) -> exact match. Rotating addresses -> remembered rotating address, else a fingerprint (company id + first 4 bytes of each manufacturer block + sorted service UUIDs) that must match exactly one device. A name is never a key. Ambiguity creates a new device. Manual merge/split are audited as `ApplicationEvent`s that list every identifier moved.
- **Presence is coverage-aware.** `PresenceStatus` returns Uncertain when the scanner was stopped, deaf, or had a recorded coverage gap between the last sighting and now. "Missing" never means "disconnected".
- **Scanner honesty.** `ScannerHealth` distinguishes Running from RunningNoAdvertisements (the Windows 10 generic-driver failure) and keeps coverage gaps for the diagnostic engine to exclude.
- **Nothing is dropped silently.** Overflow and permanent write failures are recorded as events; after a failed write the identity index is rebuilt from the database so no observation points at an unsaved device.
- **Portable data.** Everything is under `data/` next to the exe (`fleet.db`, `settings.json`, `Logs/`, `backups/`).
- **History is downsampled in SQL** (bucketed average/min/max); gaps over 3 buckets are drawn as breaks, not joined.

## Headless self-tests

`--verify-ui` (offscreen render of all pages from labelled TEST data, binding-error count) and `--scan-test` (real scanner into a throw-away database). See `TEST_PLAN.md`.

## Planned (not built)

Diagnostics project (presence-gap, RSSI stability, correlation, health score), ASP.NET Core LAN API hosted in-process with QR pairing, Android app, USB monitor, reports. See `PROJECT_PLAN.md` and `TASKS.md`.
