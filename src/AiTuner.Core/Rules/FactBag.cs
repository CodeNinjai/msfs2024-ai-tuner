using System.Globalization;
using System.Text.RegularExpressions;
using AiTuner.Core.Msfs;

namespace AiTuner.Core.Rules;

/// <summary>
/// Flattens an AnalysisResult into string facts ("cpu.cores" = "12", "msfs.cfg.video.vsync" = "1", …)
/// so the JSON rule set can be evaluated declaratively. Derived facts (e.g. cpu.x3ddualccd)
/// encode detection logic once, keeping the rules themselves data-only and generic.
/// </summary>
public sealed class FactBag
{
    private readonly Dictionary<string, string> _facts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> All => _facts;

    public void Set(string key, string? value)
    {
        if (value is not null)
            _facts[key] = value;
    }

    public string? Get(string key) => _facts.TryGetValue(key, out var value) ? value : null;

    public bool TryNumber(string key, out double number)
    {
        number = 0;
        var value = Get(key);
        return value is not null
            && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
    }

    public string Substitute(string template)
        => Regex.Replace(template, @"\{([A-Za-z0-9_.\-]+)\}", m => Get(m.Groups[1].Value) ?? "—");

    public static FactBag From(AnalysisResult analysis)
    {
        var facts = new FactBag();
        var hw = analysis.Hardware;

        if (hw.Cpu is { } cpu)
        {
            facts.Set("cpu.name", cpu.Name);
            facts.Set("cpu.manufacturer", cpu.Manufacturer);
            facts.Set("cpu.cores", cpu.Cores.ToString());
            facts.Set("cpu.threads", cpu.Threads.ToString());
            facts.Set("cpu.l3mb", (cpu.L3CacheKb / 1024).ToString());

            var isX3d = cpu.Manufacturer == "AMD" && cpu.L3CacheKb >= 96 * 1024;
            facts.Set("cpu.x3d", isX3d ? "1" : "0");
            // Dual-CCD X3D (7900X3D/7950X3D/9900X3D/9950X3D): V-Cache sits on one CCD only,
            // so thread placement matters. Single-CCD X3D (7800X3D etc.) has ≤ 8 cores.
            facts.Set("cpu.x3ddualccd", isX3d && cpu.Cores > 8 ? "1" : "0");

            // Hybrid (Intel P/E-Kerne, 12th Gen+): über die Windows-EfficiencyClass erkannt.
            var topology = Hardware.CpuTopology.Detect();
            facts.Set("cpu.hybrid", topology is { IsHybrid: true } ? "1" : "0");
            if (topology is { IsHybrid: true })
            {
                facts.Set("cpu.pcores", topology.PCoreCount.ToString());
                facts.Set("cpu.ecores", topology.ECoreCount.ToString());
            }
        }

        if (hw.Memory is { } memory)
        {
            facts.Set("ram.gb", memory.TotalGb.ToString(CultureInfo.InvariantCulture));
            facts.Set("ram.modules", memory.ModuleCount.ToString());
            facts.Set("ram.speed", memory.SpeedMtps.ToString());
        }

        var gpu = hw.Gpus.FirstOrDefault(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                  ?? hw.Gpus.FirstOrDefault();
        if (gpu is not null)
        {
            facts.Set("gpu.name", gpu.Name);
            facts.Set("gpu.vrammb", gpu.VramMb.ToString());
            facts.Set("gpu.driver", gpu.DriverVersion);
            facts.Set("gpu.rebar", gpu.ResizableBarState);
            facts.Set("gpu.isnvidia", gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "1" : "0");
            var rtx = Regex.Match(gpu.Name, @"RTX\s*(\d{2})\d{2}", RegexOptions.IgnoreCase);
            if (rtx.Success)
                facts.Set("gpu.rtxseries", rtx.Groups[1].Value);
        }

        var display = hw.Displays.FirstOrDefault(d => d.IsPrimary) ?? hw.Displays.FirstOrDefault();
        if (display is not null)
        {
            facts.Set("display.width", display.Width.ToString());
            facts.Set("display.height", display.Height.ToString());
            facts.Set("display.hz", display.RefreshHz.ToString());
            facts.Set("display.hz.half", (display.RefreshHz / 2).ToString());   // für Cap-Empfehlungen (VSync 1/2)
            facts.Set("display.hz.third", (display.RefreshHz / 3).ToString()); // … und VSync 1/3
            facts.Set("display.count", hw.Displays.Count.ToString());
        }

        foreach (var item in analysis.WindowsSettings.Items)
        {
            if (item.Key is null)
                continue;
            facts.Set($"win.{item.Key}", item.Value);
            if (item.Key == "powerplan")
                facts.Set("win.powerplan.guid", item.Detail);
        }

        var installation = analysis.MsfsInstallations.FirstOrDefault(i => i.UserCfgExists);
        facts.Set("msfs.installed", installation is null ? "0" : "1");
        if (installation is not null)
        {
            facts.Set("msfs.edition", installation.EditionName);
            var cfg = installation.LoadUserCfg();
            if (cfg is not null)
            {
                foreach (var entry in cfg.Flatten())
                {
                    var section = entry.Section.Replace(" › ", ".").Replace(" ", "");
                    var key = section.Length == 0
                        ? $"msfs.cfg.{entry.Key}"
                        : $"msfs.cfg.{section}.{entry.Key}";
                    facts.Set(key.ToLowerInvariant(), entry.Value.Trim().Trim('"'));
                }
            }

            var packagesPath = AddonScanner.GetInstalledPackagesPath(installation);
            if (packagesPath is not null)
            {
                facts.Set("msfs.packages.path", packagesPath);
                SetVolumeFacts(facts, "msfs.packages", packagesPath, analysis);
            }
        }

        facts.Set("nvidia.available", analysis.Nvidia.Available ? "1" : "0");
        facts.Set("nvidia.msfsprofile", analysis.Nvidia.MsfsProfileName is null ? "0" : "1");
        foreach (var setting in analysis.Nvidia.MsfsSettings)
        {
            var slug = Regex.Replace(setting.Name, "[^A-Za-z0-9]", "").ToLowerInvariant();
            if (slug.Length == 0)
                slug = setting.Id.ToLowerInvariant();
            facts.Set($"nvidia.msfs.{slug}",
                setting.Numeric?.ToString(CultureInfo.InvariantCulture) ?? setting.Value);
        }

        var rolling = analysis.Caches.FirstOrDefault(c => c.Name.StartsWith("MSFS Rolling Cache", StringComparison.OrdinalIgnoreCase));
        facts.Set("cache.rolling.exists", rolling is { Exists: true } ? "1" : "0");
        if (rolling is { Exists: true })
        {
            facts.Set("cache.rolling.sizemb", rolling.SizeMb.ToString(CultureInfo.InvariantCulture));
            facts.Set("cache.rolling.path", rolling.Path);
            SetVolumeFacts(facts, "cache.rolling", rolling.Path, analysis);
        }

        facts.Set("addons.count", analysis.Addons.Addons.Count.ToString());
        facts.Set("addons.issues", analysis.Addons.Issues.Count.ToString());
        facts.Set("addons.airports", analysis.Addons.AirportCount.ToString());
        facts.Set("addons.icaoconflicts",
            analysis.Addons.IcaoConflicts.Count(c => c.Packages.Count >= 2).ToString());
        facts.Set("dlss.runtimes", analysis.DlssRuntimes.Count.ToString());

        return facts;
    }

    private static void SetVolumeFacts(FactBag facts, string prefix, string path, AnalysisResult analysis)
    {
        try
        {
            if (path.Length < 2 || path[1] != ':')
                return;
            var drive = $"{char.ToUpperInvariant(path[0])}:";
            var volume = analysis.Hardware.Volumes.FirstOrDefault(v => v.DriveLetter == drive);
            if (volume is null)
                return;
            facts.Set($"{prefix}.drive", drive);
            facts.Set($"{prefix}.media", volume.MediaType);
            facts.Set($"{prefix}.bus", volume.BusType);
            facts.Set($"{prefix}.freegb", volume.FreeGb.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
    }
}
