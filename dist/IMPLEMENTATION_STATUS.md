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
| No-adapter path | Scanner reports `AdapterUnavailable` and the Dashboard warns | Observed earlier on this PC while no adapter was present |
| Real BLE scan | Passive scan -> identity -> SQLite | `--scan-test`: 1,067 real advertisements, 56 devices, 0 lost (TP-Link UB500 V3.6 driver) |

## Implemented but NOT yet verified on real hardware

- `WindowsBleScanner`: Passive scanning and "deaf scanner" detection are now verified on real hardware (see radio tests below). Still unverified: Active mode, auto-restart after unplug/replug, Bluetooth off/on, 30+ minute stability.
- The live data path into the **GUI** (scanner -> pipeline -> database is verified by `--scan-test`; the Live Monitor screen showing real devices has not been looked at by a person yet).
- Continuous scanning for 30+ minutes, UI responsiveness under real advertisement rates (hundreds per second).
- Windows 10. Code targets `net10.0-windows10.0.19041.0` with minimum OS 10.0.17763.0 (guarded call for the address type, which needs build 19041). Only Windows 11 (this PC) has been run.

### Radio tests, 2026-11-20 (Windows 11 build 22631, TP-Link UB500, VID 2357 PID 0604)

1. **Before the vendor driver:** `--scan-test --seconds 30` found the adapter, started the watcher, then correctly reported `RunningNoAdvertisements` with the driver hint: **0 advertisements**. Cause: generic Microsoft `bth.inf` driver (BTHUSB only, no filter). This verified the "deaf scanner" detection on real hardware.
2. **After installing TP-Link UB500 V3.6 (driver 1.9.1051.3016, stack BTHUSB + RtkBtFilter):** `--scan-test --seconds 30` in Passive mode: **PASS**. 1,067 advertisements received and 1,067 stored, 0 queue overflow, 0 write errors, 56 distinct devices created (10 with a name, 1 `LVS-` Lovense). Address kinds seen: Public, RandomStatic, RandomResolvablePrivate and RandomNonResolvablePrivate. Worst observed-to-processed delay 0.72 s.
   - Identity heuristics in the wild: 3 devices absorbed more than one rotating address through a unique-fingerprint match (confidence 0.7). These are exactly the matches to review manually; the heuristic can be wrong when only one of two similar products is known.
   - Still untested: 30+ minute run, unplug/replug recovery, Bluetooth off/on, Active mode, Windows 10.
## Android (Phase 2) - built, partly verified on a real phone

Phone used: Samsung Galaxy A52s 5G (SM-A528B), Android 14 (API 34), debug APK over ADB, 2026-11-20.

| Area | State | Verification |
|---|---|---|
| Project | Kotlin 2.0.21, Compose (Material 3), Hilt, Room 2.6.1, AGP 8.7.3, Gradle 8.10.2 wrapper (pinned checksum), minSdk 29, targetSdk 35 | `gradlew assembleDebug` succeeds |
| Domain logic | Windows-compatible device fingerprint, sampling-window aggregator, presence status, scanner health + coverage gaps, RSSI labels/trend/stability | 28 JVM unit tests; the fingerprint test pins values produced by the C# code |
| Install + permissions | Install over ADB, "Nearby devices" and notification prompts | **Verified on the phone**: both permissions granted through the real prompt (flag USER_SET), no INTERNET permission |
| Real scanning (screen on) | `BluetoothLeScanner` -> queue -> sampling windows -> Room | **Verified on the phone**: first run 13,505 advertisements heard, 742 windows stored, 165 devices, 0 dropped, 0 lost; later run 267 devices. Database summary: manufacturer data, service UUIDs and raw payload stored, 12-13 manufacturers, `LVS-` devices seen, per-device statistics consistent with the rows |
| Foreground service | Continuous/Inspection modes | **Verified on the phone**: `isForeground=true`, type connectedDevice, notification with a Stop action |
| Process death while scanning | Gap + event + honest status | **Verified on the phone**: after killing the app mid-scan the next launch recorded a coverage gap (14:26:24 to 14:27:21), a warning event, and showed "Interrupted" with a banner instead of claiming coverage |
| Inspection session | Start at a location, tag observations | **Verified on the phone** (start): session "Studio 01" created, 1,601 observation windows tagged with its id, live summary showed heard / known-but-not-heard / unstable / weak counts. End-and-save and the saved-sessions list were not seen working |
| Screen-off scanning | **Found a real problem, fixed, fix VERIFIED on the phone (retest 14:41-14:42)** | The Bluetooth stack's own dump showed the scan as `NonFilter` with `Suspended SCR_OFF`: Android paused the open-ended scan when the screen turned off (3 of 13,263 results delivered while off), even from a foreground service. Fix: when the screen turns off the app switches to a scan filtered to the known device addresses and says so ("Screen off: watching N known devices only"); it widens again when the screen turns on. Retest: screen off at 14:41:08, windows kept arriving at 72-81 per 10 s from about 75 devices (before the fix: none); the Bluetooth stack showed a second scan client registered as `Filter`; the app logged "scan narrowed to 337 known devices". On wake it logged "Screen on: scanning all nearby devices again" and scanned everything again. About 75 devices heard with the screen off versus about 95 with it on |
| Other screens | Nearby, Radar, Inspector, Signal History, Sync, Settings, More | **Seen working on the phone**: Radar ("165 devices heard recently"), Inspector (flags and 5-minute vs earlier comparison), Signal History (picker, periods, summary), More with the event log, Sessions (saved session "Studio 01, 2 min, 194 devices, 2,494 windows" proves end-and-save works). Nearby list and Settings not read; Sync screen not read (a performance problem on the Sessions screen was found and fixed: it drew 70+ cards every second; lists are now capped at 10) |
| "Unstable" flag | Was too sensitive: 43 of 167 devices flagged | Now ignores signals near the noise floor (average below -88 dBm is "weak", not "unstable"); covered by a new unit test; not re-checked on the phone |
| APK | `dist/Builds/Android/FleetMonitor-0.2.1-debug.apk` (latest build is installed on the phone) | `aapt2`: BLUETOOTH_SCAN (neverForLocation), foreground-service, notifications, legacy BT/location capped at API 30. **No INTERNET permission.** No ad/analytics/telemetry libraries found |

Design notes: a phone hears hundreds of advertisements per second, so each device gets one stored window per sampling interval (defaults: 10 s continuous, 2 s inspection, 30 s background) holding last/min/max/average RSSI and the number of advertisements heard. Every advertisement is counted; nothing is silently discarded. Rows that were never acknowledged by the server (Phase 3) are never deleted. Android does not reveal whether an address is public or random, so every distinct address is its own device on the phone and the Windows server reconciles identities in Phase 3.

Background mode (2026-11-23, Galaxy A52s): **verified.** With the chip selected the Scanner screen reads "Background: watching 512 known devices" and the Bluetooth stack shows a `LowPower | Filter` scan for the app. Windows were stored (about 60 per minute). Killing the process with `am kill` made Android start a new one to deliver results and windows kept arriving (154 -> 187 -> 212) with no new gap or overflow event. **Two bugs were found by that test and fixed:** (1) after Android restarted the process the app marked the scan as running but never started the loop that stores results, so almost nothing was stored (82 -> 84 windows in 45 s); (2) after an app update the OS scan is cancelled while the app would have kept claiming "Running"; the app now re-registers the OS scan every time it restarts in Background mode. Not verified: Background with the screen off, and for long periods. A process killed mid-window loses that unfinished window (up to the sampling window, 30 s by default).

Schema migration v1 -> v2 (2026-11-23, real phone data): the phone's database (511 devices, 8,060 windows, 1 session) was backed up, then the v2 build installed over it. Result: nothing lost (8,085 windows afterwards, the extra ones written before the app stopped), new columns `txPower` and `advertiseFlags` present, integrity ok. TX power and advertising flags are now stored (59 Background windows: 12 had TX power, 48 had flags).

The false "QueueOverflow: 1 dropped" event logged on every stop is fixed in code (an advertisement arriving after the queue was closed is no longer counted as dropped); not re-observed on the phone yet.

Known gaps: Bluetooth off/on recovery, permission revocation while scanning, Background mode with the screen off, long runs, and the battery cost are untested. The phone's clock was about 5 s behind the PC when compared, and observation times come from the phone clock, so Phase 3 sync must tolerate or detect clock skew.
## Radar, identification and following a signal (Android, 2026-11-23)

What exists: Start scanning goes straight to the Radar. Devices appear on it as soon as they are heard (the first reading gives a provisional distance, firmed up after a few seconds) and are listed directly under it, nearest first, in a stable order. A small (i) button holds the explanation. The ring shows N, E, S and W and turns with the phone (Heading up, with a "facing 93 deg E" readout) or stays fixed (North up). Selecting a device opens a panel at the top of the list: name, category and maker with confidence and the evidence behind it, smoothed signal, distance with error bars, calibration, Follow the signal, Connect and test, Search for this device. The search box filters the list by name, maker, type or address; Watch for it selects a matching device and buzzes when it is first heard; a pinned target is highlighted on the radar and everything else dims. Follow the signal shows warmer / colder / very close, a signal gauge, a direction arrow (once a few steps and a turn give a bearing) and optional vibration that speeds up as the distance falls.

| Piece | Verified how |
|---|---|
| Distance model (log-distance, per-device calibration, TX power or default reference, Kalman smoothing, error bars) | 9 unit tests |
| Identification (GAP appearance, 16-bit services, name patterns, company id, evidence list, agreement raises confidence) and the advertisement parser | 11 unit tests |
| Locating a device from a walk (grid search over position and reference level, error region, mirror ambiguity, confidence) | 6 unit tests with simulated walks; accuracy below |
| Compass heading maths, circular smoothing, step detector, dead reckoning | 8 unit tests |
| Room v2 -> v3 migration | Run on the real phone database: 542 devices, 8,446 windows, 1 session, 2 gaps, 6 events before and after; new columns and the connection-test table present; integrity ok |
| Setup card with "Allow everything" (permissions, then the system battery-exemption dialog) | Used by the phone's owner: the Bluetooth-connect permission was granted and the app whitelisted; the card then reads "Setup complete" |
| Start scanning -> radar, device list under it, info button, search filtering, device panel, Follow the signal gauge and stop, live compass readout | Seen working on the Galaxy A52s (text of the app's own screens) |
| Radar graphics (sweep, glow, rings, compass ring, dots, dimming) | Compiled and ran without crashing; **not visually inspected by me** (Canvas drawing is not readable as text) |

**Accuracy, measured in simulation (3 dB noise, a 17-reading walk, device about 9 m away):** with the reference level calibrated, the median position error was 1.4 m and the reported error radius contained the true position in 87% of 60 walks; every walk labelled "High" confidence was within 0.9 m. With the reference 6 dB wrong (an uncalibrated device) the median error is about 5 m and the radius still contained the truth in 83% of walks. So: calibrate the device. The app reports a likely range (for example "about 3 m, probably 2 to 5 m"), never a single exact figure. Real rooms have more multipath than the simulation, so field error will usually be larger; the error radius is the thing to trust.

NOT verified: Connect and test (code complete, but I did not connect to any of your devices; it needs you to choose a safe one), the direction arrow and bearing on a real walk, vibration pacing, Watch for it firing, heading-up rotation while turning (the compass readout updates, but I cannot rotate the phone), step counting on a real walk, and drift of the path over longer walks.

## Not built yet (by design)

Fault detection engine, health score, Worst Offenders, signal analysis (Phase 4); LAN API with QR pairing and sync (Phase 3); active connection tests (Phase 5); USB monitor, reports, in-app restore, release packaging (Phase 6); iOS (deferred; needs a Mac). Their navigation entries are marked with their phase and show "Not implemented yet". The Health column shows "-" instead of a number.

## Known gaps inside Phase 1

- Split has a service and tests but **no UI** (merge has a UI).
- Inventory category is free text (no preset list).
- The Dashboard "events" feed shows scanner and application events only. Presence events (device went missing / came back) are Phase 4, because a naive window rule would produce false alarms.
- Live Monitor has no persisted column layout.

## Build errors / outstanding bugs

None known. Bugs found and fixed during Phase 1: ComboBox showing type names, filters leaking between pages (shared collection view), `CollectionViewSource` NRE in DataGrid, async-dispose crash on exit, EF SQL logged at Info level, Dashboard property hiding the `ScannerState` enum.
