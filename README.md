# MSFS 2024 AI Tuner

Windows-App, die dein System analysiert, konkrete Optimierungsvorschläge für den **Microsoft
Flight Simulator 2024** macht — und sie auf Wunsch direkt anwendet. Dazu kommt die komplette
Verwaltung von Szenerien, Add-ons und GSX-Profilen sowie ein messgestützter Tuning-Workflow.

Alles in einer portablen EXE, ohne Installation, mit automatischen Backups vor jeder Änderung.

---

## Was die App kann

**Analyse & Empfehlungen**
- Erfasst CPU (inkl. AMD-X3D-CCD- und Intel-P/E-Kern-Topologie), RAM, GPU mit VRAM und
  Resizable BAR, Monitore, Laufwerke sowie die leistungsrelevanten Windows-Einstellungen.
- Deklarative Regel-Engine (`rules.json`) leitet daraus Empfehlungen ab — jede mit Ist-Wert,
  Zielwert, Begründung, Detail-Erklärung und, wo möglich, einem **Anwenden**-Button
  (Registry, Energieplan, `UserCfg.opt`, NVIDIA-Treiberprofil).
- Regelwerke lassen sich exportieren und importieren, ohne die App neu zu bauen.

**Einstellungs-Editoren**
- **MSFS:** Alle Grafik- und VR-Einstellungen aus der `UserCfg.opt` direkt editierbar — mit
  FPS-Impact- und CPU/GPU-Last-Angabe, Erklärungen pro Wert und Sperren für abhängige
  Optionen (z. B. VSync bei aktivem DLSS).
- **Windows:** Energieplan, HAGS, MPO, Spielemodus, GameDVR & Co.
- **NVIDIA:** Treiberprofil-Einstellungen des MSFS-Profils lesen und ändern (VSync inkl.
  Bruchteil-Modi, Latenzarmer Modus, vorgerenderte Bilder, Power-Modus, Resizable BAR,
  DLSS-Overrides).

**Szenerien & Add-ons**
- **Bibliotheken:** Add-ons außerhalb des Community-Ordners lagern und per NTFS-Junction
  aktivieren/deaktivieren — ohne Adminrechte. Erkennt bestehende Strukturen automatisch,
  inklusive verwalteter Manager-Ordner (Aerosoft One, FSDT Addon Manager), die bewusst
  unangetastet bleiben.
- **Installer:** Add-ons aus ZIP, 7z, RAR oder Ordner installieren — auch per Drag & Drop,
  auch mehrere Pakete gleichzeitig, mit Zielwahl (Community, Community2024 oder Bibliothek).
- **GSX:** Erkennt vorhandene Profile pro Airport und installiert neue — inklusive Auswahl
  der VDGS-Variante (GSX, Aerosoft, nool), wenn ein Archiv mehrere Fassungen enthält.
  Aircraft-Configs (`virtuali\Airplanes`) werden ebenfalls unterstützt.
- **Konflikt-Analyse:** Exakte Airport-ICAOs aus den BGL-Binärdaten, Dubletten zwischen
  `Community` und `Community2024`, überdeckte Official-Airports, defekte Links.
- **Content.xml:** Paket-Aktivierung des Sims durchsuchen und schalten, ohne den Sim zu starten.
- **Autostart (`EXE.xml`):** Zeigt, welche Programme mit dem Sim starten, erkennt tote
  Einträge und deaktiviert sie auf Wunsch.
- **Layout-Check:** Findet Dateien, die der Sim wegen fehlender `layout.json`-Einträge
  ignoriert (der Klassiker bei manuell installierten Liveries) und repariert sie —
  Hersteller-Konfiguratoren werden erkannt und in Ruhe gelassen.

**Messen & Tunen**
- **Benchmark:** Frametime-Messung über Intel PresentMon mit Ø-FPS und 1 % Low, A/B-Vergleich
  gegen eine Baseline. Über SimConnect wird pro Messung Flugzeug, Position und Flugphase
  festgehalten — die App warnt, wenn zwei Messungen nicht vergleichbar sind.
- **Kern-Monitor:** Live-Auslastung pro CPU-Kern, gruppiert nach CCD bzw. P-/E-Kernen, plus
  gezieltes Pinning des Sim-Prozesses auf die schnellen Kerne.
- **Flug-Modus:** Pausiert ausgewählte Hintergrunddienste (Windows Update, Indexer, Telemetrie …),
  solange der Sim läuft, und stellt sie danach automatisch wieder her — auch nach einem
  App-Neustart.
- **Tuning-Assistent:** Führt durch Baseline, Experiment, Messung und Entscheidung; jedes
  Experiment ist ein Profil und jederzeit zurücknehmbar.
- **Profile & Backups:** Sichern Windows-Werte, Energieplan, die komplette `UserCfg.opt` und
  die NVIDIA-Profileinstellungen.

Damit deckt die App den Funktionsumfang mehrerer Einzeltools ab (Addon Linker, Layout
Generator, Cache-Cleaner, DLSS-Swapper) — in einer Oberfläche und mit gemeinsamer Datenbasis.

---

## Download & Start

1. Unter [Releases](../../releases) das ZIP herunterladen und entpacken.
2. `AiTuner.App.exe` starten. **`SimConnect.dll` muss im selben Ordner liegen** — sie wird
   für die Sim-Anbindung gebraucht.
3. Es ist keine Installation und kein .NET-Runtime nötig (self-contained).

Adminrechte fordert die App nur punktuell an (HKLM-Einstellungen, Benchmark-Aufzeichnung,
Flug-Modus) — jeweils mit UAC-Abfrage für genau diesen einen Vorgang.

### Systemvoraussetzungen

- Windows 10/11, 64 Bit
- MSFS 2024 (Microsoft Store oder Steam); MSFS 2020 wird bei der Analyse mit erkannt
- NVIDIA-GPU für die Treiberprofil-Funktionen (alles andere läuft auch ohne)

---

## Sicherheit

- Vor der ersten Änderung einer Sitzung wird automatisch ein Backup angelegt.
- Änderungen an `UserCfg.opt`, `EXE.xml`, `Content.xml` und `layout.json` erfolgen
  strukturerhaltend; von jeder Datei wird vorher eine Sicherung abgelegt.
- Das Deaktivieren von Add-ons entfernt nur Verknüpfungen, niemals Inhalte.
- Der Flug-Modus verändert keine Startarten, sondern stoppt Dienste nur temporär.
- Schreibzugriffe auf Sim-Dateien sind gesperrt, solange der Simulator läuft.

Gespeicherte Daten liegen unter `%APPDATA%\Msfs2024AiTuner\` (Profile, Backups, Konfiguration).

---

## Bekannte Grenzen

- **AMD- und Intel-Grafikkarten** werden erkannt, aber nicht getunt — die Treiberprofil-
  Funktionen setzen NVIDIA (NVAPI) voraus.
- **Intel-Hybrid-CPUs** (P-/E-Kerne) werden über die Windows-API erkannt und mit eigenen
  Regeln bedacht; getestet wurde bisher nur auf AMD-Hardware.
- Die ICAO-Erkennung liest exakte Werte aus BGL-Dateien; für Pakete ohne Airport-BGL greift
  eine Namens-Heuristik, die in Einzelfällen danebenliegen kann.
- Einige NVIDIA-Wertecodes sind in der Oberfläche als „unbestätigt" markiert, weil sie nicht
  gegen einen realen Treiber verifiziert werden konnten.

---

## Aus dem Quellcode bauen

Benötigt das .NET 8 SDK.

```powershell
git clone <repo-url>
cd msfs2024_ai_tuner
dotnet build Msfs2024AiTuner.sln -c Release

# Portable EXE erzeugen
dotnet publish src\AiTuner.App\AiTuner.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

### Projektstruktur

| Projekt | Inhalt |
| --- | --- |
| `AiTuner.Core` | Analyse, Regel-Engine, Einstellungs-Katalog, Apply-Logik, Bibliotheken, Benchmark, SimConnect |
| `AiTuner.App` | WPF-Oberfläche (WPF-UI, MVVM) |
| `AiTuner.Cli` | Diagnose-Werkzeuge (`--json`, `--icao`, `--layoutscan`, `--topology`, `--simconnect`, …) |

Regelwerk und Einstellungs-Katalog liegen als JSON in `src/AiTuner.Core/Rules/` bzw.
`src/AiTuner.Core/Settings/`. Beide sind eingebettet, lassen sich aber durch gleichnamige
Dateien neben der EXE oder unter `%APPDATA%\Msfs2024AiTuner\` überschreiben.

---

## Lizenz

MIT — siehe [LICENSE](LICENSE). Kein offizielles Microsoft- oder Asobo-Produkt; alle
Produkt- und Herstellernamen gehören ihren jeweiligen Eigentümern.
