# Bluetooth Fleet Monitor — Taskuri

Legendă: `[x]` gata și verificat · `[~]` implementat, dar încă neverificat complet (vezi nota) · `[ ]` de făcut · 🔧 necesită test pe hardware real

Un task e bifat doar după ce compilează, are teste unde se poate și a fost verificat. Un ecran care doar există nu înseamnă funcționalitate gata.

---

## Faza 0 — Pregătire mediu

- [x] Citire specificație (`de-facut.txt`)
- [x] Inspecție calculator: .NET, Java, Android SDK, Visual Studio, adaptor Bluetooth
- [x] Instalare .NET 10 SDK (10.0.404)
- [x] Instalare JDK 17 + setare `JAVA_HOME`
- [x] Instalare Android SDK 35 (cmdline-tools, platform-tools, build-tools) + setare `ANDROID_HOME`
- [x] Folder unic portabil `dist/`
- [x] Script instalare unelte pe alt calculator (`Scripts/Setup-DevEnvironment.ps1`)
- [x] `README.md` lângă `de-facut.txt`
- [x] `PROJECT_PLAN.md`
- [x] Driver TP-Link (Realtek) instalat de utilizator
- [x] 🔧 Test scanare BLE reală pe Windows — Passive: 85 dispozitive, 981 reclame / 20 s, `LVS-Z14` și `LVS-Lush` văzute, fără conectare

## Faza 1 — Aplicația Windows de bază

### Documentație
- [x] `TECHNICAL_ARCHITECTURE.md`
- [x] `IMPLEMENTATION_STATUS.md`
- [x] `TEST_PLAN.md`
- [x] `KNOWN_LIMITATIONS.md`

### Structură
- [x] Soluție .NET 10: Domain, Application, Database, Diagnostics, Bluetooth, UsbMonitoring, Desktop (WPF), Tests
- [x] Pachete NuGet (EF Core 10.0.15 SQLite, CommunityToolkit.Mvvm, Serilog, LiveCharts2, Hosting)
- [x] Scheletul compilează fără erori
- [x] Configurare JSON (`appsettings.json`), căi relative, date în `data/` lângă exe

### Model de date și bază de date
- [x] Entități Domain: Device, DeviceIdentifier, Scanner, Observation, PresenceEvent, ConnectionTest, InspectionSession, FaultReport, UsbEvent, ApplicationEvent
- [x] `DbContext` EF Core + SQLite, indexuri (DeviceId, ScannerId, Timestamp, EventType, Location, Category)
- [x] Migrație inițială
- [x] Backup automat al bazei înainte de migrații (backup testat; declanșatorul din start-up nu are test unitar)
- [x] Verificare integritate SQLite la pornire (calea de eșec netestată)
- [x] Teste: migrație pe bază goală, scriere/citire

### Identificarea dispozitivelor
- [x] Amprentă dispozitiv: manufacturer data, service UUIDs, nume, adresă, tip adresă
- [x] Asociere adresă ↔ dispozitiv cu nivel de încredere
- [x] Gestionare adrese aleatorii (random / resolvable private)
- [x] Fără unire automată doar după nume identic
- [~] Unire / separare manuală cu jurnal de audit (unire: serviciu + UI; separare: doar serviciu, fără UI)
- [x] Teste: schimbare adresă, nume duplicat, observații duplicate

### Scanner BLE Windows
- [x] Interfață `IBleScanner` (permite alte scannere mai târziu)
- [x] Implementare cu `BluetoothLEAdvertisementWatcher`, **Passive implicit** (verificat real: 1.067 reclame, 56 dispozitive, 0 pierdute)
- [~] Mod Active doar ca opțiune explicită în Settings (opțiunea există; efectul real neverificat)
- [~] Stare reală adaptor: lipsă / oprit / pornit / eroare / întrerupt („lipsă” și „pornit, dar surd” verificate real; restul doar în teste unitare)
- [~] Detectare scoatere adaptor și repornire automată a scanării (implementat, netestat pe hardware)
- [x] Înregistrare întreruperi scanner (acoperire scanner pentru diagnostic)
- [x] Coadă limitată de observații (Channel) + scriere în loturi în bază
- [x] Nicio observație pierdută fără înregistrare (overflow → ApplicationEvent)
- [x] 🔧 Verificare pe hardware: dispozitive reale apar în bază (`--scan-test`, TP-Link UB500 V3.6)

### Interfață WPF (temă întunecată, MVVM)
- [x] Fereastră principală + navigare (cele 14 secțiuni, inactive marcate cu faza)
- [~] Dashboard: total, detectate acum, lipsă, instabile, stare scanner, ultima actualizare, feed evenimente live
- [~] Live Monitor: tabel live, căutare, filtre, sortare RSSI / last seen, pauză / reluare
- [~] Device Inventory: listă sortabilă, redenumire, cameră, workstation, note, categorie
- [~] Import / export CSV (serviciu testat; dialogurile de fișier netestate)
- [~] Device History: grafic RSSI, perioade (15 min … 30 zile), evenimente
- [~] Event Log: evenimente aplicație și scanner, filtrare
- [~] Actualizări UI fără blocare (baza de date nu rulează pe firul UI; reactivitate sub încărcare reală nemăsurată)

### Logging și finalizare fază
- [x] Serilog în `data/Logs`, crash logging
- [x] Toate testele automate trec (67/67)
- [ ] 🔧 Test pe hardware: scanare continuă 30+ min, UI responsiv, `LVS-*` apar
- [x] Actualizare `IMPLEMENTATION_STATUS.md`

## Faza 2 — Aplicația Android

- [x] Proiect Kotlin + Compose + Hilt + Room, Gradle wrapper în `dist/Android` (wrapper fixat pe checksum verificat)
- [x] Permisiuni: Android 10–11 (locație) și 12+ (Nearby Devices) (verificat pe Galaxy A52s / Android 14; Android 10–11 netestat)
- [x] Scanner BLE `BluetoothLeScanner` (doar ascultare, fără conectare) (verificat pe telefon: 267 dispozitive, 0 pierdute)
- [~] Colectare: RSSI, nume, adresă, manufacturer data, UUIDs, flags, TX power, payload (RSSI/nume/adresă/manufacturer/UUID/payload verificate pe telefon; flags și TX power citite dar încă nesalvate)
- [x] Bază locală Room (verificată pe telefon: 172-267 dispozitive, statistici consistente cu rândurile)
- [~] Mod A — scanare continuă cu foreground service și notificare (service + notificare verificate; scanarea cu ecranul stins se oprea, acum trece pe lista dispozitivelor cunoscute - de retestat)
- [~] Mod B — scanare în fundal (filtre, PendingIntent), raportare întreruperi (netestat pe telefon; filtrare doar pe nume exacte)
- [~] Mod C — inspecție mobilă: alegere locație, note, salvare sesiune (pornirea sesiunii verificată pe telefon; încheierea/salvarea nevăzută)
- [~] Ecrane: Scanner, Nearby Devices, Signal Radar, Device Inspector, Signal History, Inspection Sessions, Sync Status, Settings (toate 8 compilează; nerulate pe telefon)
- [~] Avertizare optimizare baterie (netestat pe telefon)
- [~] Gestionare: Bluetooth oprit, permisiune revocată, repornire telefon, proces oprit (proces oprit verificat pe telefon; restul netestat)
- [x] Teste unitare (28 de teste de logică pură: amprentă compatibilă cu Windows, agregare, prezență, sănătate scanner, RSSI)
- [x] APK debug în `dist/Builds/Android` (FleetMonitor-0.2.0-debug.apk, 24,8 MB; permisiuni minime, fără INTERNET, fără SDK-uri de reclame/analitice)
- [~] 🔧 Test pe telefon real (Galaxy A52s / Android 14: parțial, vezi IMPLEMENTATION_STATUS.md)

## Faza 3 — Legătura Windows ↔ Android

- [ ] Server ASP.NET Core local în aplicația Windows, port configurabil, legat doar de rețeaua locală
- [ ] Endpoint-uri `/api/v1/...` (health, pair, scanners, observations/batch, inspection-sessions, devices, sync/status, events)
- [ ] Swagger în modul development
- [ ] Pairing cu cod QR + token unic per telefon
- [ ] Revocare token, pairing manual, mai multe telefoane
- [ ] Validare cereri, rate limiting, erori structurate
- [ ] Upload în loturi, idempotent (fără dubluri), confirmare de la server
- [ ] Coadă offline pe Android (WorkManager), reîncercări
- [ ] Ora observației păstrată separat de ora uploadului și a procesării
- [ ] Opțiune HTTPS cu certificat local
- [ ] Teste: autentificare, sincronizare, recuperare offline
- [ ] 🔧 Test real: telefon offline → reconectare → date recuperate

## Faza 4 — Detecția defectelor

- [ ] Algoritm A: goluri de prezență (ține cont de acoperirea scannerului)
- [ ] Algoritm B: stabilitate RSSI (medie, deviație, degradări bruște)
- [ ] Algoritm C: corelare între mai multe dispozitive (cameră, scanner, aceeași perioadă)
- [ ] Algoritm D: repetabilitate din testele de conexiune
- [ ] Algoritm E: recomandări bazate pe dovezi
- [ ] Scor de sănătate 0–100, ponderi și praguri configurabile
- [ ] Afișare „de ce s-a schimbat scorul” + încredere separată
- [ ] „Insufficient data” când nu sunt destule observații
- [ ] Separare cauză: dispozitiv / mediu / scanner / aplicație / necunoscut
- [ ] Ecran Worst Offenders cu filtre
- [ ] Ecran Signal Analysis
- [ ] Notificări configurabile
- [ ] Teste cu date simulate (marcate clar ca simulate)
- [ ] 🔧 Validare pe flota reală

## Faza 5 — Teste active de conexiune

- [ ] Doar pe un dispozitiv selectat manual
- [ ] Avertizare înainte de test (poate interfera cu aplicația care controlează dispozitivul)
- [ ] Fără deconectarea conexiunilor existente, fără comenzi Lovense, fără pairing automat
- [ ] Configurare: încercări, timeout, pauză, cooldown, descoperire servicii GATT
- [ ] Înregistrare rezultate: durată, eroare platformă, status GATT
- [ ] Statistici: reușite, eșecuri, timeout %, timp mediu / min / max
- [ ] 🔧 Test pe dispozitiv non-Lovense mai întâi

## Faza 6 — Versiune finală

- [ ] Monitor USB (receivere Logitech, adaptoare BT, huburi)
- [ ] Reports + export anonimizat
- [ ] Settings complet
- [ ] Backup și restore din aplicație
- [ ] Retenție date configurabilă
- [ ] Optimizare performanță (500+ dispozitive)
- [ ] Build portabil Windows x64 în `dist/Builds/Windows`
- [ ] APK release semnat în `dist/Builds/Android`
- [ ] Manual utilizator + instrucțiuni instalare
- [ ] 🔧 Checklist acceptanță hardware (cele 18 criterii din specificație)
