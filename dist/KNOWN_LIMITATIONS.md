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

## Verification limits

- The Windows scanner has been verified with a real radio for Passive scanning only (30 s test). Active mode, unplug/replug recovery, Bluetooth off/on and long runs are unverified. See `IMPLEMENTATION_STATUS.md`.
- Windows 10 not run. Android has been run on one phone only (Galaxy A52s, Android 14), and not every feature. iPhone not applicable yet.

## Android limits

- **Android hides address type.** The phone cannot tell a public address from a random one, so every distinct address is its own device on the phone. It never merges devices by fingerprint. The server reconciles in Phase 3; rotating-address devices will show as several phone-side devices until then.
- **`neverForLocation`.** The app declares it never derives location from scans, so Android 12+ does not require location permission, but Android may then filter out beacon-style advertisements (iBeacon/Eddystone). Fleet devices that advertise normally are unaffected.
- **Android 10-11 need the Location setting switched on** for any scan result to be delivered, even though no location is recorded.
- **Background mode** uses an OS-managed filtered scan. Android only keeps it running with the screen off if it is filtered, and `ScanFilter` supports exact device names, not prefixes. Android may still stop it (battery saver, vendor task killers). With no names set, Background mode filters on the addresses of the devices already known, so brand-new devices are not discovered in this mode. A process Android kills mid-window loses that unfinished window (up to the sampling window). Interruptions are recorded when the app can detect them; if the OS silently stops delivering results the status falls to "running, hearing nothing" instead of claiming coverage.
- **Screen off pauses open-ended scans.** Measured on a Galaxy A52s (Android 14): with the screen off Android suspended the unfiltered scan, even inside a foreground service. The app therefore narrows to a scan filtered to the already-known device addresses while the screen is off. Consequences: devices never seen before are not discovered until the screen is on, and a large watch list may be handled by Android in software, which costs more battery. Keep the screen on (or use Inspection mode while walking) when you need discovery.
- **Foreground service** (Continuous/Inspection) keeps the process alive, but not an unfiltered scan (see above). Android 15 restricts starting such services from the background, so a scan stopped by a phone restart is recorded as a gap and must be restarted by hand.
- **Sampling, not every advertisement.** Each device is stored as one window per interval with last/min/max/average RSSI and a count. Setting the interval to 0 stores everything but fills storage quickly (170 devices can produce hundreds of thousands of rows an hour).
- **No upload yet.** Until Phase 3 nothing leaves the phone and nothing is deleted automatically.
- **Release builds** are currently signed with the debug key (local install only). A real signing key arrives in Phase 6.
- **Partly verified on one phone** (Galaxy A52s, Android 14). Other models and Android versions are untested; Samsung and other vendors add their own background-process killers. See `IMPLEMENTATION_STATUS.md`.