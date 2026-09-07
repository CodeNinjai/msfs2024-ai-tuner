using System.Windows;
using System.Windows.Controls;
using AiTuner.App.ViewModels;

namespace AiTuner.App.Views;

public partial class LibrariesView : UserControl
{
    public LibrariesView()
    {
        InitializeComponent();
        DataContext = MainViewModel.Instance;
    }

    private void InstallMenu_Click(object sender, RoutedEventArgs e) => LibraryMenu_Click(sender, e);

    private static bool IsInstallable(string path)
        => System.IO.Directory.Exists(path) || Core.Libraries.AddonInstaller.IsArchive(path);

    private static string[] DroppedSources(DragEventArgs e)
        => e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.Where(IsInstallable).ToArray()
            : Array.Empty<string>();

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) && DroppedSources(e).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var sources = DroppedSources(e);
        if (sources.Length > 0)
            await MainViewModel.Instance.InstallFromSourcesAsync(sources);
    }

    private void LibraryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>GSX-Chip: grün → Profilordner öffnen, grau → flightsim.to-Suche nach dem ICAO.</summary>
    private void GsxChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LibraryRowItem row })
            return;
        try
        {
            if (row.GsxHasProfile)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", Core.Libraries.GsxProfileService.ProfilesPath) { UseShellExecute = true });
            else if (row.GsxSearchIcao.Length > 0)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"https://flightsim.to/search?query={Uri.EscapeDataString(row.GsxSearchIcao + " gsx profile")}")
                { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Aktivieren: ohne Community2024 direkt; mit Community2024 als Zielwahl-Menü.</summary>
    private void ActivateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryRowItem row } button)
            return;

        var vm = MainViewModel.Instance;
        if (!vm.HasCommunity2024)
        {
            vm.ActivateAddonTo(row, toCommunity2024: false);
            return;
        }

        var menu = new ContextMenu();
        var classic = new MenuItem { Header = "→ Link in Community (MSFS 2020 + 2024)" };
        classic.Click += (_, _) => vm.ActivateAddonTo(row, toCommunity2024: false);
        menu.Items.Add(classic);
        var c2024 = new MenuItem { Header = "→ Link in Community2024 (nur MSFS 2024)" };
        c2024.Click += (_, _) => vm.ActivateAddonTo(row, toCommunity2024: true);
        menu.Items.Add(c2024);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>Baut das „Verschieben“-Menü dynamisch: alle Bibliotheken + Community-Ordner (ohne den aktuellen Ort).</summary>
    private void MoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryRowItem row } button)
            return;

        var vm = MainViewModel.Instance;
        var menu = new ContextMenu();
        var isFixedIn = (string root) =>
            !row.Addon.IsLink && row.Addon.Location.Equals(root, StringComparison.OrdinalIgnoreCase);

        foreach (var library in vm.LibraryList)
        {
            if (library.Managed) // verwaltete Strukturen (Aerosoft One, FSDT) sind kein Ziel
                continue;
            if (string.Equals(library.Path, row.Addon.Location, StringComparison.OrdinalIgnoreCase))
                continue; // aktueller Ort
            var target = library.Path;
            var item = new MenuItem { Header = $"→ Bibliothek: {target}" };
            item.Click += async (_, _) => await vm.MoveAddonToTargetAsync(row, target);
            menu.Items.Add(item);
        }

        if (!isFixedIn("Community") || vm.HasCommunity2024)
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

        if (!isFixedIn("Community"))
        {
            var header = vm.HasCommunity2024
                ? "→ Community-Ordner (fest — MSFS 2020 + 2024)"
                : "→ Community-Ordner (fest installiert, aktiv)";
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await vm.MoveAddonToTargetAsync(row, null);
            menu.Items.Add(item);
        }

        if (vm.HasCommunity2024 && !isFixedIn("Community2024"))
        {
            var item = new MenuItem { Header = "→ Community2024-Ordner (fest — nur MSFS 2024)" };
            item.Click += async (_, _) => await vm.MoveAddonToTargetAsync(row, null, toCommunity2024: true);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Kein anderes Ziel verfügbar — erst eine Bibliothek hinzufügen", IsEnabled = false });
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
