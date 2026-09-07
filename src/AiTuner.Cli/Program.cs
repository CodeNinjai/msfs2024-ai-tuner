using System.Text;
using System.Text.Json;
using AiTuner.Core;
using AiTuner.Core.Apply;

// Elevated relaunch für HKLM-Registry-Schreibvorgänge (HAGS/MPO).
if (ElevatedCommands.TryHandle(args, out var elevatedExitCode))
    return elevatedExitCode;

Console.OutputEncoding = Encoding.UTF8;

// Diagnose: prüft, ob der UserCfg-Parser die Datei byte-identisch regeneriert (Write-back-Sicherheit).
if (args.Contains("--roundtrip-check"))
{
    foreach (var installation in AiTuner.Core.Msfs.MsfsLocator.DetectInstallations().Where(i => i.UserCfgExists))
    {
        var original = File.ReadAllText(installation.UserCfgPath);
        var regenerated = AiTuner.Core.Msfs.UserCfgDocument.Parse(original).ToText();
        Console.WriteLine($"{installation.EditionName}: Original {original.Length} Zeichen, regeneriert {regenerated.Length}, identisch: {original == regenerated}");
        if (original != regenerated)
        {
            var min = Math.Min(original.Length, regenerated.Length);
            var index = 0;
            while (index < min && original[index] == regenerated[index])
                index++;
            var origSnip = original.Substring(Math.Max(0, index - 20), Math.Min(40, original.Length - Math.Max(0, index - 20)));
            var regenSnip = regenerated.Substring(Math.Max(0, index - 20), Math.Min(40, regenerated.Length - Math.Max(0, index - 20)));
            Console.WriteLine($"  Erste Abweichung bei Index {index}:");
            Console.WriteLine($"  Original:    {origSnip.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}");
            Console.WriteLine($"  Regeneriert: {regenSnip.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}");
        }
    }
    return 0;
}

var result = SystemAnalyzer.Run();

if (args.Contains("--json"))
{
    var dto = new
    {
        timestamp = result.Timestamp,
        hardware = result.Hardware,
        msfs = result.MsfsInstallations.Select(i => new
        {
            edition = i.EditionName,
            userCfgPath = i.UserCfgPath,
            exists = i.UserCfgExists,
            settings = i.LoadUserCfg()?.Flatten(),
        }),
        windows = result.WindowsSettings.Items,
        nvidia = new
        {
            result.Nvidia.Available,
            result.Nvidia.MsfsProfileName,
            global = result.Nvidia.GlobalSettings,
            msfsProfile = result.Nvidia.MsfsSettings,
        },
        dlss = result.DlssRuntimes,
        addons = result.Addons.Addons,
        addonIssues = result.Addons.Issues,
        caches = result.Caches,
        recommendations = result.Recommendations,
        warnings = result.Hardware.Warnings
            .Concat(result.WindowsSettings.Warnings)
            .Concat(result.Nvidia.Warnings),
    };
    Console.WriteLine(JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// Diagnose: Settings-Katalog auflisten (inkl. automatischer VR-Klone).
if (args.Contains("--catalog"))
{
    var catalog = AiTuner.Core.Settings.SettingsCatalog.All;
    foreach (var group in catalog.GroupBy(d => d.Area))
        Console.WriteLine($"{group.Key}: {group.Count()} Einträge");
    Console.WriteLine($"Gesamt: {catalog.Count}");
    foreach (var definition in catalog)
        Console.WriteLine($"  [{definition.Area}] {definition.Match}  →  {definition.Name}");
    return 0;
}

// Diagnose: LinkTarget-Auflösung für Junctions prüfen.
var linkTargetIndex = Array.IndexOf(args, "--linktarget");
if (linkTargetIndex >= 0 && linkTargetIndex + 1 < args.Length)
{
    var info = new DirectoryInfo(args[linkTargetIndex + 1]);
    Console.WriteLine($"Exists: {info.Exists} · ReparsePoint: {(info.Attributes & FileAttributes.ReparsePoint) != 0}");
    Console.WriteLine($"LinkTarget: {info.LinkTarget ?? "(null)"}");
    Console.WriteLine($"ResolveLinkTarget: {info.ResolveLinkTarget(true)?.FullName ?? "(null)"}");
    return 0;
}

// Diagnose: Bibliotheks-Migration/-Auflösung auf Testordnern durchspielen.
var scan2024Index = Array.IndexOf(args, "--scan2024");
if (scan2024Index >= 0 && scan2024Index + 1 < args.Length)
{
    var fakeInstallation = new AiTuner.Core.Msfs.MsfsInstallation
    {
        Edition = AiTuner.Core.Msfs.MsfsEdition.MicrosoftStore,
        UserCfgPath = args[scan2024Index + 1],
    };
    var scanResult = AiTuner.Core.Libraries.LibraryService.Scan(fakeInstallation);
    Console.WriteLine($"CommunityPath:     {scanResult.CommunityPath}");
    Console.WriteLine($"Community2024Path: {scanResult.Community2024Path ?? "(fehlt)"}");
    foreach (var c in scanResult.CommunityItems)
        Console.WriteLine($"  [{c.Location}] {c.FolderName} · {c.Title} · Link={c.IsLink} Aktiv={c.IsActive} Broken={c.IsBroken}");
    foreach (var l in scanResult.LibraryItems)
        Console.WriteLine($"  [Lib {l.Location}] {l.FolderName} · Aktiv={l.IsActive} via={l.ActiveIn}");
    foreach (var d in scanResult.Duplicates)
        Console.WriteLine($"  DUP: {d}");
    return 0;
}

if (args.Contains("--icao"))
{
    var installs = AiTuner.Core.Msfs.MsfsLocator.DetectInstallations();
    var scanned = AiTuner.Core.Msfs.AddonScanner.Scan(installs);
    Console.WriteLine($"Add-ons: {scanned.Addons.Count} ({scanned.Addons.Count(a => a.DeactivatedInContent)} in Content.xml deaktiviert) · Airports: {scanned.AirportCount}");
    Console.WriteLine($"IcaoConflicts: {scanned.IcaoConflicts.Count}");
    foreach (var conflict in scanned.IcaoConflicts)
        Console.WriteLine($"  {conflict.Icao}{(conflict.InvolvesOfficial ? " [+Official]" : "")}: {string.Join(" | ", conflict.Packages)}");
    Console.WriteLine($"Issues: {scanned.Issues.Count}");
    foreach (var issue in scanned.Issues)
        Console.WriteLine($"  {issue}");
    return 0;
}

var contentTestIndex = Array.IndexOf(args, "--contenttest");
if (contentTestIndex >= 0 && contentTestIndex + 1 < args.Length)
{
    var fakeInst = new AiTuner.Core.Msfs.MsfsInstallation
    {
        Edition = AiTuner.Core.Msfs.MsfsEdition.MicrosoftStore,
        UserCfgPath = Path.Combine(args[contentTestIndex + 1], "UserCfg.opt"),
    };
    var packages = AiTuner.Core.Msfs.ContentXmlService.Load(fakeInst);
    Console.WriteLine($"{packages.Count} Pakete · Aktiv={packages.Count(p => p.IsActive)} · UserDisabled={packages.Count(p => p.IsUserDisabled)} · SystemDisabled={packages.Count(p => p.IsSystemDisabled)}");
    var target = packages.First(p => p.IsCommunity && p.IsActive);
    Console.WriteLine($"Testziel: {target.RawName} [{target.SourceLabel}]");
    var (ok1, m1) = AiTuner.Core.Msfs.ContentXmlService.SetActive(fakeInst, target.RawName, activate: false);
    Console.WriteLine($"Deaktivieren: ok={ok1} — {m1}");
    var after = AiTuner.Core.Msfs.ContentXmlService.Load(fakeInst).First(p => p.RawName == target.RawName);
    Console.WriteLine($"Zustand jetzt: {after.ActiveState}");
    var (ok2, _) = AiTuner.Core.Msfs.ContentXmlService.SetActive(fakeInst, target.RawName, activate: true);
    Console.WriteLine($"Reaktivieren: ok={ok2}");
    var sysBlocked = packages.FirstOrDefault(p => p.IsSystemDisabled);
    if (sysBlocked is not null)
    {
        var (ok3, m3) = AiTuner.Core.Msfs.ContentXmlService.SetActive(fakeInst, sysBlocked.RawName, activate: true);
        Console.WriteLine($"SystemDisabled-Sperre: ok={ok3} (false erwartet) — {m3}");
    }
    return 0;
}

var exeTestIndex = Array.IndexOf(args, "--exetest");
if (exeTestIndex >= 0 && exeTestIndex + 1 < args.Length)
{
    var fakeInstall = new AiTuner.Core.Msfs.MsfsInstallation
    {
        Edition = AiTuner.Core.Msfs.MsfsEdition.MicrosoftStore,
        UserCfgPath = Path.Combine(args[exeTestIndex + 1], "UserCfg.opt"),
    };
    void PrintEntries()
    {
        foreach (var e in AiTuner.Core.Msfs.ExeXmlService.Load(fakeInstall))
            Console.WriteLine($"  {e.Name} · Disabled={e.Disabled} · PathExists={e.PathExists}");
    }
    Console.WriteLine("Vorher:"); PrintEntries();
    var (ok1, msg1) = AiTuner.Core.Msfs.ExeXmlService.SetDisabled(fakeInstall, "Skytrails Manager", true);
    Console.WriteLine($"SetDisabled(true): ok={ok1} — {msg1}");
    var reloaded = AiTuner.Core.Msfs.ExeXmlService.Load(fakeInstall);
    Console.WriteLine($"Skytrails jetzt Disabled={reloaded.First(e => e.Name.StartsWith("Skytrails")).Disabled}");
    var (ok2, msg2) = AiTuner.Core.Msfs.ExeXmlService.SetDisabled(fakeInstall, "Skytrails Manager", false);
    Console.WriteLine($"SetDisabled(false): ok={ok2}");
    Console.WriteLine("Nachher:"); PrintEntries();
    return 0;
}

var bglIndex = Array.IndexOf(args, "--bgl");
if (bglIndex >= 0 && bglIndex + 1 < args.Length)
{
    foreach (var root in args[(bglIndex + 1)..])
    {
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.bgl", SearchOption.AllDirectories).ToList()
            : new List<string> { root };
        foreach (var file in files)
        {
            var icaos = AiTuner.Core.Msfs.BglParser.ReadAirportIcaos(file);
            if (icaos.Count > 0)
                Console.WriteLine($"{Path.GetFileName(file)}: {string.Join(", ", icaos)}");
        }
    }
    return 0;
}

if (args.Contains("--layouttest"))
{
    var root = Path.Combine(Path.GetTempPath(), "aituner-layouttest");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    Directory.CreateDirectory(Path.Combine(root, "sub"));
    File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"title\":\"LayoutTest\"}");
    File.WriteAllBytes(Path.Combine(root, "a.bgl"), new byte[10]);
    File.WriteAllBytes(Path.Combine(root, "sub", "b.dds"), new byte[20]);
    var (genOk, genMsg) = AiTuner.Core.Msfs.LayoutService.Regenerate(root);
    Console.WriteLine($"Regenerate: ok={genOk} — {genMsg}");
    Console.WriteLine($"Check sauber: {AiTuner.Core.Msfs.LayoutService.Check(root).IsClean} (true erwartet)");
    // Manipulieren: Datei dazu, Größe ändern, Datei löschen
    File.WriteAllBytes(Path.Combine(root, "livery.dds"), new byte[5]);
    File.WriteAllBytes(Path.Combine(root, "a.bgl"), new byte[99]);
    File.Delete(Path.Combine(root, "sub", "b.dds"));
    var check = AiTuner.Core.Msfs.LayoutService.Check(root);
    Console.WriteLine($"Nach Manipulation: unlisted={check.UnlistedFiles.Count} mismatch={check.SizeMismatches.Count} missing={check.MissingFiles.Count} (je 1 erwartet) — {check.Summary}");
    AiTuner.Core.Msfs.LayoutService.Regenerate(root);
    Console.WriteLine($"Nach Reparatur sauber: {AiTuner.Core.Msfs.LayoutService.Check(root).IsClean} (true erwartet)");
    Directory.Delete(root, true);
    return 0;
}

if (args.Contains("--layoutscan"))
{
    var installs2 = AiTuner.Core.Msfs.MsfsLocator.DetectInstallations();
    var install2 = installs2.FirstOrDefault(i => i.UserCfgExists);
    if (install2 is null) { Console.WriteLine("Keine Installation."); return 1; }
    var scanResult2 = AiTuner.Core.Libraries.LibraryService.Scan(install2);
    var dirs = scanResult2.CommunityItems.Where(c => !c.IsBroken).Select(c => c.Path)
        .Concat(scanResult2.LibraryItems.Select(l => l.Path))
        .Select(p => { try { return System.IO.Path.TrimEndingDirectorySeparator(new DirectoryInfo(p).LinkTarget ?? p); } catch { return p; } })
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var dirty = 0;
    foreach (var dir in dirs)
    {
        var layoutResult = AiTuner.Core.Msfs.LayoutService.Check(dir);
        if (!layoutResult.IsClean)
        {
            dirty++;
            Console.WriteLine($"⚠ {Path.GetFileName(dir)}: {layoutResult.Summary}");
            foreach (var f in layoutResult.UnlistedFiles.Take(3)) Console.WriteLine($"    nicht gelistet: {f}");
            foreach (var f in layoutResult.SizeMismatches.Take(3)) Console.WriteLine($"    Größe falsch:  {f}");
            foreach (var f in layoutResult.MissingFiles.Take(3)) Console.WriteLine($"    fehlt:         {f}");
        }
    }
    Console.WriteLine($"— {dirs.Count} Pakete geprüft, {dirty} mit Layout-Problemen.");
    return 0;
}

if (args.Contains("--simconnect"))
{
    using var sim = new AiTuner.Core.Sim.SimConnectService();
    if (!sim.TryConnect())
    {
        Console.WriteLine("SimConnect: Sim nicht erreichbar (läuft MSFS?).");
        return 0;
    }
    Console.WriteLine("SimConnect: verbunden — frage Position ab …");
    var snapshot = sim.Poll(3000);
    Console.WriteLine(snapshot is null
        ? "Keine Antwort (Sim evtl. noch im Menü)."
        : $"{snapshot.AircraftTitle} · {snapshot.PositionText} · {snapshot.PhaseText}");
    return 0;
}

if (args.Contains("--topology"))
{
    var topology = AiTuner.Core.Hardware.CpuTopology.Detect();
    if (topology is null)
    {
        Console.WriteLine("Topologie-Abfrage fehlgeschlagen (GetSystemCpuSetInformation).");
    }
    else
    {
        Console.WriteLine($"Hybrid: {topology.IsHybrid}");
        Console.WriteLine($"P-Kerne: {topology.PCoreCount} ({topology.PLogicalCount} logisch, Maske 0x{topology.PCoreMask:X})");
        Console.WriteLine($"E-Kerne: {topology.ECoreCount}");
        Console.WriteLine("EfficiencyClass je logischem Prozessor: " + string.Join(" ", topology.EfficiencyClassByLogical));
    }
    return 0;
}

var gsxPrepIndex = Array.IndexOf(args, "--gsxprep");
if (gsxPrepIndex >= 0 && gsxPrepIndex + 1 < args.Length)
{
    foreach (var source in args[(gsxPrepIndex + 1)..])
    {
        Console.WriteLine($"=== {Path.GetFileName(source)} ===");
        var preparation = AiTuner.Core.Libraries.AddonInstaller.Prepare(source);
        if (preparation.Error is not null)
            Console.WriteLine($"  Fehler: {preparation.Error}");
        Console.WriteLine($"  MSFS-Pakete: {preparation.Candidates.Count}");
        foreach (var gsxCandidate in preparation.GsxProfiles)
        {
            Console.WriteLine($"  GSX {gsxCandidate.Icao} — {gsxCandidate.Variants.Count} Variante(n):");
            foreach (var variant in gsxCandidate.Variants)
                Console.WriteLine($"    [{variant.Vdgs}] {variant.Label}  ({variant.Files.Count} Datei(en): {string.Join(", ", variant.Files.Select(Path.GetFileName))})");
        }
        foreach (var aircraft in preparation.GsxAircraftConfigs)
            Console.WriteLine($"  Aircraft-Config: {aircraft.AircraftFolder} ({aircraft.FileCount} cfg-Datei(en))");
        AiTuner.Core.Libraries.AddonInstaller.Cleanup(preparation);
    }
    return 0;
}

if (args.Contains("--installtest"))
{
    var root = Path.Combine(Path.GetTempPath(), "aituner-installtest");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    var community = Path.Combine(root, "Community");
    var library = Path.Combine(root, "Lib");
    Directory.CreateDirectory(community);
    Directory.CreateDirectory(library);

    // Quelle 1: ZIP mit Überordner + manifest.json, dazu ein zweites Paket im selben ZIP
    var stage = Path.Combine(root, "stage");
    Directory.CreateDirectory(Path.Combine(stage, "dev-airport-eddx", "scenery"));
    File.WriteAllText(Path.Combine(stage, "dev-airport-eddx", "manifest.json"),
        "{\"title\": \"EDDX Testfield\", \"package_version\": \"1.2.3\"}");
    Directory.CreateDirectory(Path.Combine(stage, "extras", "dev-lights"));
    File.WriteAllText(Path.Combine(stage, "extras", "dev-lights", "manifest.json"),
        "{\"title\": \"Dev Lights\", \"package_version\": \"0.9\"}");
    var zip = Path.Combine(root, "twopack.zip");
    System.IO.Compression.ZipFile.CreateFromDirectory(stage, zip);

    var prep = AiTuner.Core.Libraries.AddonInstaller.Prepare(zip);
    Console.WriteLine($"ZIP: {prep.Candidates.Count} Kandidat(en), Fehler={prep.Error ?? "-"}");
    foreach (var c in prep.Candidates)
        Console.WriteLine($"  {c.FolderName} · {c.Title} v{c.Version}");

    foreach (var c in prep.Candidates)
    {
        var target = c.FolderName == "dev-airport-eddx" ? library : null;
        var (ok, msg) = AiTuner.Core.Libraries.AddonInstaller.Install(
            c, community, target, activate: true, copyInsteadOfMove: !prep.SourceIsUserFolder ? false : true, overwrite: false);
        Console.WriteLine($"  Install ok={ok}: {msg}");
    }
    AiTuner.Core.Libraries.AddonInstaller.Cleanup(prep);

    var link = new DirectoryInfo(Path.Combine(community, "dev-airport-eddx"));
    Console.WriteLine($"Junction da: {(link.Attributes & FileAttributes.ReparsePoint) != 0}, Ziel-Manifest: {File.Exists(Path.Combine(library, "dev-airport-eddx", "manifest.json"))}");
    Console.WriteLine($"Community fest: {File.Exists(Path.Combine(community, "dev-lights", "manifest.json"))}");

    // Quelle 2: Nutzer-Ordner (muss KOPIERT werden, Quelle bleibt bestehen) + EXISTS-Konflikt
    var folderSource = Path.Combine(stage, "extras", "dev-lights");
    var prep2 = AiTuner.Core.Libraries.AddonInstaller.Prepare(folderSource);
    Console.WriteLine($"Ordner: {prep2.Candidates.Count} Kandidat(en), userFolder={prep2.SourceIsUserFolder}");
    var (ok2, msg2) = AiTuner.Core.Libraries.AddonInstaller.Install(
        prep2.Candidates[0], community, null, true, copyInsteadOfMove: true, overwrite: false);
    Console.WriteLine($"  Konflikt erwartet: ok={ok2}, msg={msg2}");
    var (ok3, msg3) = AiTuner.Core.Libraries.AddonInstaller.Install(
        prep2.Candidates[0], community, null, true, copyInsteadOfMove: true, overwrite: true);
    Console.WriteLine($"  Overwrite: ok={ok3}: {msg3}");
    Console.WriteLine($"  Quelle existiert noch (Kopie!): {Directory.Exists(folderSource)}");

    Directory.Delete(Path.Combine(community, "dev-airport-eddx"), recursive: false); // Junction zuerst
    Directory.Delete(root, true);
    Console.WriteLine("Installtest fertig.");
    return 0;
}

var libtestIndex = Array.IndexOf(args, "--libtest");
if (libtestIndex >= 0 && libtestIndex + 3 < args.Length)
{
    var community = args[libtestIndex + 1];
    var sourceLib = args[libtestIndex + 2];
    var targetLib = args[libtestIndex + 3];
    var (ok1, log1) = AiTuner.Core.Libraries.LibraryService.MigrateLibrary(community, sourceLib, targetLib);
    Console.WriteLine($"Migrate ok={ok1}");
    log1.ForEach(Console.WriteLine);
    var (ok2, log2) = AiTuner.Core.Libraries.LibraryService.DissolveToCommunity(community, targetLib);
    Console.WriteLine($"Dissolve ok={ok2}");
    log2.ForEach(Console.WriteLine);
    return 0;
}

// Diagnose: PresentMon-Download testen.
if (args.Contains("--download-presentmon"))
{
    var (ok, message) = await AiTuner.Core.Benchmark.PresentMonService.DownloadAsync();
    Console.WriteLine((ok ? "OK: " : "FEHLER: ") + message);
    return ok ? 0 : 1;
}

// Diagnose: vollständiges Backup anlegen und verifizieren.
if (args.Contains("--create-backup"))
{
    var analysis = SystemAnalyzer.Run();
    var profile = AiTuner.Core.Profiles.ProfileService.Capture(analysis, $"Backup {DateTime.Now:yyyy-MM-dd HH-mm-ss}", isBackup: true);
    var directory = AiTuner.Core.Profiles.ProfileService.DirectoryFor(profile.Name);
    Console.WriteLine($"Backup „{profile.Name}“ → {directory}");
    Console.WriteLine($"  profile.json vorhanden: {File.Exists(Path.Combine(directory, "profile.json"))}");
    Console.WriteLine($"  UserCfg.opt vorhanden:  {File.Exists(Path.Combine(directory, "UserCfg.opt"))}");
    Console.WriteLine($"  Windows-Werte: {profile.WindowsValues.Count} · NVIDIA-Settings: {profile.NvidiaSettings.Count} · Energieplan: {profile.PowerPlanGuid}");
    return 0;
}

Header("System");
var hw = result.Hardware;
if (hw.Cpu is { } cpu)
    Row("CPU", $"{cpu.Name} ({cpu.Manufacturer}, {cpu.Cores}C/{cpu.Threads}T, Basis {cpu.MaxClockMhz / 1000.0:0.0} GHz, L3 {cpu.L3CacheKb / 1024} MB)");
if (hw.Memory is { } mem)
    Row("RAM", $"{mem.TotalGb:0.#} GB ({mem.ModuleCount} Module, {mem.SpeedMtps} MT/s)");
foreach (var gpu in hw.Gpus)
    Row("GPU", $"{gpu.Name}, {gpu.VramMb / 1024.0:0.#} GB VRAM, Treiber {gpu.DriverVersion}, ReBAR: {gpu.ResizableBarState}");
foreach (var display in hw.Displays)
    Row(display.IsPrimary ? "Monitor *" : "Monitor", $"{display.DeviceName}: {display.Width}x{display.Height} @ {display.RefreshHz} Hz");
foreach (var volume in hw.Volumes)
    Row("Laufwerk", $"{volume.DriveLetter} {volume.BusType} {volume.MediaType}, {volume.FreeGb:0} von {volume.TotalGb:0} GB frei");
Row("Windows", hw.OsDescription);

Header("MSFS 2024");
foreach (var installation in result.MsfsInstallations)
{
    Row(installation.EditionName, installation.UserCfgExists ? installation.UserCfgPath : "UserCfg.opt nicht gefunden");
    if (installation.LoadUserCfg() is { } cfg)
    {
        var entries = cfg.Flatten().ToList();
        Row("", $"{entries.Count} Einstellungen gelesen");
    }
}

Header("Windows-Einstellungen");
foreach (var item in result.WindowsSettings.Items)
    Row($"{item.Category} / {item.Name}", item.Value);

Header("NVIDIA Treiberprofile");
if (!result.Nvidia.Available)
{
    Row("Status", "NVAPI nicht verfügbar");
}
else
{
    Row("Basisprofil", $"{result.Nvidia.GlobalSettings.Count} explizit gesetzte Einstellungen");
    foreach (var setting in result.Nvidia.GlobalSettings)
        Row($"  {setting.Name}", setting.Value);
    Row("MSFS-Profil", result.Nvidia.MsfsProfileName ?? "nicht gefunden");
    foreach (var setting in result.Nvidia.MsfsSettings)
        Row($"  {setting.Name}", setting.Value);
}

Header("DLSS");
if (result.DlssRuntimes.Count == 0)
    Row("Runtime", "Keine DLSS-DLLs gefunden (Store-Pakete ggf. geschützt)");
foreach (var dlss in result.DlssRuntimes)
    Row($"{dlss.FileName} ({dlss.Source})", dlss.Version);

Header($"Add-ons ({result.Addons.Addons.Count} · {result.Addons.AirportCount} Airports erkannt)");
foreach (var conflict in result.Addons.IcaoConflicts.Where(c => c.Packages.Count >= 2))
    Row($"ICAO-Konflikt {conflict.Icao}", string.Join("  +  ", conflict.Packages));
foreach (var issue in result.Addons.Issues)
    Row("Konflikt?", issue);
foreach (var addon in result.Addons.Addons.OrderBy(a => a.ContentType).ThenBy(a => a.Title))
    Row($"{addon.ContentType}", $"{addon.Title} {addon.Version} ({addon.Creator}){(addon.HasWasm ? " [WASM]" : "")}");

Header("Caches");
foreach (var cache in result.Caches)
    Row(cache.Name, cache.Exists ? $"{cache.SizeMb:0} MB — {cache.Path}" : "nicht vorhanden");

Header($"Empfehlungen ({result.Recommendations.Count})");
foreach (var rec in result.Recommendations)
{
    Console.WriteLine();
    Row($"[{rec.SeverityLabel}] {rec.Category}", rec.Title);
    Row("  Aktuell", rec.Current);
    Row("  Empfohlen", rec.Recommended);
    Row("  Warum", rec.Reason);
    if (rec.ApplyHint is not null)
        Row("  Umsetzung", rec.ApplyHint);
}

var warnings = result.Hardware.Warnings
    .Concat(result.WindowsSettings.Warnings)
    .Concat(result.Nvidia.Warnings)
    .ToList();
if (warnings.Count > 0)
{
    Header("Hinweise");
    foreach (var warning in warnings)
        Row("!", warning);
}

return 0;

static void Header(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}

static void Row(string label, string value)
    => Console.WriteLine(label.Length == 0 ? $"    {value}" : $"{label,-28} {value}");
