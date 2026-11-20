# Implementation Status

Last updated: 2026-11-20, end of Phase 1 work. Build: `dotnet build` in `dist/Windows` is clean (0 warnings, 0 errors). Tests: 67 passed, 0 failed.

## Completed and verified

| Area | What exists | How it was verified |
|---|---|---|
| Database | All 10 spec tables, indexes, `InitialCreate` migration, UTC timestamp handling, online backup, integrity check | 13 automated tests |
| Device identity | Stable-address match, rotating-address memory, fingerprint match only when unique, never name-only | 10 automated tests |
| Observation pipeline | Bounded queue, batched transactional writes, retries, overflow and write-failure recorded as events, graceful flush on stop | 6 automated tests |
| Store | Idempotent batches (duplicate id stored once), stale uploads cannot overwrite current state, three separate timestamps | 8 automated tests |
| Device admin | Edit details, CSV export/import (formula-safe), manual merge and split with audit events, bucketed history queries | 11 automated tests |
| Scanner health | Pure state machine: running / running-but-deaf (driver hint) / interrupted / adapter unavailable, coverage gaps; address-kind classifier | 12 automated tests |
| Presence status | Detected / Missing / Uncertain, coverage-aware | 7 automated tests |
| Windows UI | Dark shell, 14-section navigation, Dashboard, Live Monitor, Inventory, History (chart), Scanner, Event Log, Settings | `--verify-ui`: all 14 pages render with 0 binding errors, chart produces series (TEST data) |
| App lifecycle | Start-up (backup-before-migrate, migrate, integrity check), crash logging, graceful shutdown | Ran the real app: started, responded, exited with code 0, scanner event recorded in the database |
| No-adapter path | Scanner reports `AdapterUnavailable` and the Dashboard warns | Observed on this PC, which has no Bluetooth adapter present |

## Implemented but NOT yet verified on real hardware

- `WindowsBleScanner` (Passive default, Active opt-in, auto-restart after interruption, radio on/off handling). It compiles and its state logic is unit-tested, but this development PC has **no Bluetooth adapter present** (the ASUS USB-BT500 is registered but unplugged; the Bluetooth service is stopped), so no real advertisement has been received by this code. The earlier session recorded a successful 85-device passive probe on a TP-Link adapter with the Realtek driver, but that was a throw-away probe, not this scanner.
- The live data path: scanner -> pipeline -> database -> Live Monitor. Each stage is tested separately; the whole chain has not run with a radio.
- Continuous scanning for 30+ minutes, UI responsiveness under real advertisement rates (hundreds per second).
- Windows 10. Code targets `net10.0-windows10.0.19041.0` with minimum OS 10.0.17763.0 (guarded call for the address type, which needs build 19041). Only Windows 11 (this PC) has been run.

## Not built yet (by design)

Fault detection engine, health score, Worst Offenders, signal analysis (Phase 4); Android app and LAN API with QR pairing (Phases 2-3); active connection tests (Phase 5); USB monitor, reports, in-app restore, release packaging (Phase 6); iOS (deferred; needs a Mac). Their navigation entries are marked with their phase and show "Not implemented yet". The Health column shows "-" instead of a number.

## Known gaps inside Phase 1

- Split has a service and tests but **no UI** (merge has a UI).
- Inventory category is free text (no preset list).
- The Dashboard "events" feed shows scanner and application events only. Presence events (device went missing / came back) are Phase 4, because a naive window rule would produce false alarms.
- Live Monitor has no persisted column layout.

## Build errors / outstanding bugs

None known. Bugs found and fixed during Phase 1: ComboBox showing type names, filters leaking between pages (shared collection view), `CollectionViewSource` NRE in DataGrid, async-dispose crash on exit, EF SQL logged at Info level, Dashboard property hiding the `ScannerState` enum.
