# Known Limitations

## Bluetooth platform limits

- **Windows scans BLE only.** `BluetoothLEAdvertisementWatcher` hears BLE advertisements. It cannot passively scan classic Bluetooth (headsets, speakers) and cannot see proprietary 2.4 GHz receivers such as a Logitech Unifying/Bolt dongle. Those need separate categories and, for USB receivers, the Phase 6 USB monitor (which can only see the dongle arriving or leaving, never whether a keyboard lost its link).
- **Adapter and driver dependent.** On Windows 10, Realtek-based USB adapters (TP-Link UB500, ASUS USB-BT500) using the generic Microsoft driver start the scanner but receive **zero** advertisements until the manufacturer driver is installed. The app detects "running but hearing nothing" and says so; it cannot fix it. Windows 11 usually ships a working driver.
- **Passive vs Active.** Passive (default) sends nothing and misses names that are only sent in scan responses. Active recovers more names but adds radio traffic and is an explicit opt-in.
- **One adapter.** Version 1 uses the Windows default adapter. Independent multi-adapter scanning is not claimed until proven on hardware.
- **RSSI is not distance.** It depends on antenna, orientation, body, and transmit power. The app labels it Excellent/Good/Fair/Weak/Very weak and never converts it to metres.
- **Not every device advertises continuously.** Some stop advertising when connected to a phone, which looks identical to "not there" from a scanner. This is why absence is never reported as a disconnection.

## Identity limits

- **Random private addresses rotate.** A device that rotates its address and has no manufacturer data or service UUIDs cannot be tracked automatically; it will appear as a new device each time. Manual merge exists for this.
- **Identical devices.** 170 units of the same model share a fingerprint. When a rotating-address advertisement could belong to more than one of them the app creates a new device instead of guessing. Devices with stable addresses are not affected.
- **A unique-fingerprint match is a heuristic** (confidence 0.7). Two different products that share a vendor prefix and no other distinguishing data could be matched wrongly if only one of them is known. Matches are recorded with their confidence.
- Windows builds older than 10 2004 (19041) do not report the address type; those advertisements are treated as "unknown kind" and never trusted as a stable identity.

## Application limits

- **Presence status is a window rule**, not a diagnosis. "Missing" means "not heard within the presence window while the scanner was demonstrably able to hear". Fault detection (Phase 4) will replace guesswork with expected-interval analysis.
- **Health score and fault ranking do not exist yet.** Nothing in the UI invents them.
- **iOS** cannot be built on Windows (needs a Mac and Xcode) and installs only via Apple signing (free Apple ID re-signs every 7 days; paid account for TestFlight/ad hoc). iOS never exposes MAC addresses and restricts background scanning. Deferred.
- The local database is **not encrypted** yet (optional encryption is a later phase). Bluetooth addresses and manufacturer data are stored locally and exports can be anonymised in Phase 6.
- Data lives next to the exe (`data/`). Moving the exe without the folder starts a fresh database.

## Verification limits (as of Phase 1)

- No real radio has been exercised by this scanner implementation on the development PC (no adapter present). See `IMPLEMENTATION_STATUS.md`.
- Windows 10 not run. Android and iPhone not applicable yet.
