using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CmlLib.Core;

namespace FrostClient;

public partial class MainWindow
{
    private string? _modsVersionId, _modsMc, _modsLoader;
    private bool _modsBusy;

    // Cada versão (não-Frost) tem a sua própria pasta de jogo: mods, saves, options.txt...
    // Libraries/assets/runtime/versions continuam partilhados (não se descarrega tudo outra vez).
    private MinecraftPath CreateGamePath(string realId)
    {
        var shared = new MinecraftPath(_gameDir);
        if (IsFrostClientVersion(realId)) return shared;

        var p = new MinecraftPath(Path.Combine(_gameDir, "instances", realId));
        p.Library  = shared.Library;
        p.Versions = shared.Versions;
        p.Resource = shared.Resource;
        p.Runtime  = shared.Runtime;
        p.Assets   = shared.Assets;
        return p;
    }

    // (loader, versão de MC) de uma versão instalada com loader; null se for vanilla.
    private (string Loader, string Mc)? DetectLoader(string realId)
    {
        try
        {
            var path = Path.Combine(_gameDir, "versions", realId, realId + ".json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("inheritsFrom", out var inh)) return null;
            var mc = inh.GetString();
            if (string.IsNullOrWhiteSpace(mc)) return null;

            var l = realId.ToLowerInvariant();
            string? loader = l.Contains("neoforge") ? "neoforge" : l.Contains("forge") ? "forge"
                           : l.Contains("quilt") ? "quilt" : l.Contains("fabric") ? "fabric" : null;
            return loader == null ? null : (loader, mc);
        }
        catch { return null; }
    }

    private void ManageMods_Click(object s, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry == null) { MessageBox.Show("Select a version first.", "Frost Client"); return; }

        if (IsFrostClientVersion(entry.RealId))
        {
            MessageBox.Show("The mods of Frost Client are managed by the launcher itself.", "Frost Client");
            return;
        }
        var info = DetectLoader(entry.RealId);
        if (info == null)
        {
            MessageBox.Show("This version has no mod loader. Install a Fabric / Quilt / Forge / NeoForge version first.",
                "Frost Client");
            return;
        }

        (_modsVersionId, _modsLoader, _modsMc) = (entry.RealId, info.Value.Loader, info.Value.Mc);
        ModsTitle.Text = $"Mods — {entry.DisplayName}  ({_modsLoader} {_modsMc})";
        ModsSearchBox.Text = "";
        ModsStatus.Text = "";
        ModsResults.ItemsSource = null;
        RefreshInstalledMods();
        ModsOverlay.Visibility = Visibility.Visible;
        ModsSearchBox.Focus();
        _ = SearchModsAsync("");          // vazio = mods populares
    }

    private void ModsClose_Click(object s, RoutedEventArgs e) => ModsOverlay.Visibility = Visibility.Collapsed;

    private void ModsSearchBox_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; _ = SearchModsAsync(ModsSearchBox.Text.Trim()); }
    }

    private void ModsSearch_Click(object s, RoutedEventArgs e) => _ = SearchModsAsync(ModsSearchBox.Text.Trim());

    private async Task SearchModsAsync(string query)
    {
        if (_modsMc == null || _modsLoader == null) return;
        try
        {
            ModsStatus.Text = "Searching Modrinth...";
            ModsResults.ItemsSource = await Modrinth.SearchAsync(query, _modsMc, _modsLoader);
            ModsStatus.Text = "";
        }
        catch (Exception ex) { ModsStatus.Text = "Search failed: " + ex.Message; }
    }

    private async void ModInstall_Click(object s, RoutedEventArgs e)
    {
        if (_modsBusy || _modsVersionId == null || _modsMc == null || _modsLoader == null) return;
        if (s is not FrameworkElement { Tag: string projectId }) return;

        _modsBusy = true;
        ModsProgress.Visibility = Visibility.Visible;
        try
        {
            var added = await Modrinth.InstallAsync(projectId, _modsMc, _modsLoader,
                GameModsDir(_modsVersionId), m => Dispatcher.InvokeAsync(() => ModsStatus.Text = m));
            ModsStatus.Text = added.Count == 0 ? "Already installed." : $"Installed: {string.Join(", ", added)}";
            RefreshInstalledMods();
        }
        catch (Exception ex) { ModsStatus.Text = "Install failed: " + ex.Message; }
        finally { _modsBusy = false; ModsProgress.Visibility = Visibility.Collapsed; }
    }

    private void ModRemove_Click(object s, RoutedEventArgs e)
    {
        if (_modsVersionId == null || s is not FrameworkElement { Tag: string file }) return;
        try { Modrinth.Remove(GameModsDir(_modsVersionId), file); RefreshInstalledMods(); }
        catch (Exception ex) { ModsStatus.Text = "Could not remove: " + ex.Message; }
    }

    private void RefreshInstalledMods()
        => ModsInstalled.ItemsSource = _modsVersionId == null ? null : Modrinth.ListInstalled(GameModsDir(_modsVersionId));
}