# Bluetooth Fleet Monitor

Sistem local de monitorizare și diagnostic Bluetooth pentru o flotă de 170+ dispozitive
(Lovense, controllere LED, tastaturi/mouse-uri Bluetooth, receivere USB Logitech).

- **Windows 11** — centru de comandă: server local, baza de date SQLite, dashboard, detecția defectelor.
- **Android** — scanner portabil și instrument de inspecție, sincronizează cu Windows prin Wi-Fi local.

Totul rulează local. Fără cloud, reclame, telemetrie sau abonamente.

## Fișiere

| Cale | Ce conține |
|---|---|
| `de-facut.txt` | Specificația completă (sursa de adevăr). |
| `README.md` | Acest fișier. |
| `dist/` | **Tot proiectul** — un singur folder, portabil între calculatoare. |
| `dist/Documentation/SPECIFICATION.txt` | Copie a specificației, ca `dist/` să fie complet de sine stătător. |
| `dist/TASKS.md` | **Lista de taskuri** pe faze, cu bifă pentru ce e gata. |
| `dist/PROJECT_PLAN.md` | Planul pe faze și starea mediului de dezvoltare. |
| `dist/Scripts/Setup-DevEnvironment.ps1` | Instalează uneltele pe un calculator nou. |
| `dist/Windows/` | Soluția .NET 10 (WPF + EF Core + SQLite). |
| `dist/Android/` | Aplicația Kotlin / Jetpack Compose (Faza 2). |
| `dist/Builds/` | Executabile și APK-uri generate. |

## Mutarea pe alt calculator

1. Copiază folderul `dist/` (poți șterge înainte folderele `bin/` și `obj/` — se regenerează).
2. Pe calculatorul nou rulează o singură dată:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\dist\Scripts\Setup-DevEnvironment.ps1
   ```
   Instalează .NET 10 SDK, JDK 17 și Android SDK 35 (~8–10 GB). Necesită internet doar acum.
3. Deschide un terminal nou și compilează:
   ```powershell
   cd dist\Windows
   dotnet build
   dotnet test
   ```

## Cerințe hardware

- Adaptor Bluetooth 4.0+ cu suport BLE și **driverul producătorului** instalat.
- Pentru adaptoarele cu chip Realtek (TP-Link UB500, ASUS USB-BT500) pe **Windows 10**,
  driverul generic Microsoft (`bth.inf`, „Generic Bluetooth Adapter”) pornește scanarea BLE,
  dar nu primește nicio reclamă. Instalează driverul de pe site-ul producătorului.
  După instalare, Device Manager arată în continuare „Generic Bluetooth Adapter” — e normal,
  driverul Realtek se instalează ca filtru (`rtkfilter.inf`). Verificat 2026-11-20 cu TP-Link:
  0 reclame înainte de driver, 91 dispozitive / 949 reclame în 15 s după.
  Windows 11 include de obicei driverul corect.
- Telefon Android 10+ cu Bluetooth LE (pentru aplicația mobilă).

## Stare curentă

| Fază | Stare |
|---|---|
| 0 — Inspecție mediu, unelte instalate | ✅ Gata |
| 1 — Aplicația Windows de bază | 🔨 Aproape gata: se compilează, 67 teste trec, UI verificat headless; scannerul BLE neverificat cu radio real (lipsește adaptorul pe acest PC) |
| 2 — Scanner Android | ⏳ |
| 3 — Sincronizare Windows ↔ Android, pairing QR | ⏳ |
| 4 — Detecția defectelor, Worst Offenders | ⏳ |
| 5 — Teste active de conexiune (doar pe dispozitive selectate) | ⏳ |
| 6 — Release: build portabil, APK semnat, manual | ⏳ |

Taskuri detaliate: `dist/TASKS.md`. Stare tehnică: `dist/IMPLEMENTATION_STATUS.md` (actualizat la finalul fiecărei faze).

## Reguli de siguranță

- Nu se trimit comenzi proprietare către dispozitive Lovense și nu se preia controlul lor.
- Testele active de conexiune rulează doar pe un dispozitiv ales explicit, după avertizare.
- Lipsa unui dispozitiv dintr-o scanare **nu** este raportată ca deconectare confirmată.
- Datele simulate apar doar în modul Demo, marcate vizibil.
