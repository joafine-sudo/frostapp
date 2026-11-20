using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CmlLib.Core;

namespace FrostClient;

// ─────────────────────────────────────────────────────────────
// Handlers que o MainWindow.xaml referencia mas que NÃO existiam no code-behind
// (versões, renomear, confirmar, definições). Sem eles o projeto nem compila
// e, numa build antiga, abrir "Versions" ou mexer nas definições rebentava.
// ─────────────────────────────────────────────────────────────
public partial class MainWindow
{
    private string? _pendingConfirmVersionId;
    private string? _renameTargetId;
    private bool _installingVersion;

    // ── Versions page: category tabs ─────────────────────────
    private string _versionTab = "frost";   // "frost" | "vanilla" | "custom"
    private List<VersionEntry> _frostVersions   = new();
    private List<VersionEntry> _vanillaVersions = new();
    private List<VersionEntry> _customVersions  = new();

    private void VersionTab_Click(object s, RoutedEventArgs e)
    {
        if (s is not System.Windows.Controls.Button { CommandParameter: string tab }) return;
        _versionTab = tab;
        ApplyVersionTab();
    }

    private void VersionSearch_Changed(object s, TextChangedEventArgs e)
    {
        if (VersionSearchHint != null)
            VersionSearchHint.Visibility = string.IsNullOrEmpty(VersionSearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        ApplyVersionTab();
    }

    private void ApplyVersionTab()
    {
        if (VersionTabList == null) return;

        var source = _versionTab switch
        {
            "vanilla" => _vanillaVersions,
            "custom"  => _customVersions,
            _         => _frostVersions
        };

        var query = VersionSearchBox?.Text?.Trim() ?? "";
        var shown = (query.Length == 0
            ? source
            : source.Where(v => v.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || v.RealId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

        VersionTabList.ItemTemplate = (DataTemplate)FindResource(
            _versionTab == "frost" ? "FrostClientItemTemplate" : "VersionItemTemplate");
        VersionTabList.ItemsSource = shown;

        TabFrostButton.Content   = $"Frost Client  ({_frostVersions.Count})";
        TabVanillaButton.Content = $"Vanilla  ({_vanillaVersions.Count})";
        TabCustomButton.Content  = $"Custom  ({_customVersions.Count})";
        TabFrostButton.Tag   = _versionTab == "frost"   ? "Active" : null;
        TabVanillaButton.Tag = _versionTab == "vanilla" ? "Active" : null;
        TabCustomButton.Tag  = _versionTab == "custom"  ? "Active" : null;

        bool empty = shown.Count == 0;
        VersionsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            VersionsEmptyText.Text = query.Length > 0
                ? "No versions match your search."
                : _versionTab switch
                {
                    "vanilla" => "No vanilla versions installed yet.\nPick a Minecraft version on the right and press Download.",
                    "custom"  => "No custom versions found.\nForge, Fabric, Quilt and OptiFine installs will show up here.",
                    _         => "Frost Client is not installed yet. It is set up automatically when the launcher starts."
                };
        }
    }

    // Small tag shown on each card (FROST / FORGE / FABRIC / RELEASE / SNAPSHOT ...).
    private string GetVersionBadge(string realId)
    {
        if (IsFrostClientVersion(realId)) return "FROST";

        var l = realId.ToLowerInvariant();
        if (l.Contains("neoforge")) return "NEOFORGE";
        if (l.Contains("forge"))    return "FORGE";
        if (l.Contains("fabric"))   return "FABRIC";
        if (l.Contains("quilt"))    return "QUILT";
        if (l.Contains("optifine")) return "OPTIFINE";

        try
        {
            var path = Path.Combine(_gameDir, "versions", realId, realId + ".json");
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("inheritsFrom", out _) &&
                    doc.RootElement.TryGetProperty("type", out var t))
                {
                    return (t.GetString() ?? "").ToLowerInvariant() switch
                    {
                        "release"   => "RELEASE",
                        "snapshot"  => "SNAPSHOT",
                        "old_beta"  => "BETA",
                        "old_alpha" => "ALPHA",
                        _           => "VANILLA"
                    };
                }
            }
        }
        catch { }

        return "CUSTOM";
    }

    // ── Pages: Home / Versions (a full page now, no longer a popup) ──
    private void ShowPage(bool versions)
    {
        HomePage.Visibility     = versions ? Visibility.Collapsed : Visibility.Visible;
        VersionsPage.Visibility = versions ? Visibility.Visible   : Visibility.Collapsed;

        // NavLink style highlights the button whose Tag is "Active".
        NavHomeButton.Tag     = versions ? null : "Active";
        NavVersionsButton.Tag = versions ? "Active" : null;

        if (versions && InstallVersionBox.Items.Count == 0) _ = LoadRemoteVersionsAsync();
    }

    private void OpenVersions_Click(object s, RoutedEventArgs e) => ShowPage(true);
    private void ShowHome_Click(object s, RoutedEventArgs e) => ShowPage(false);

    private async void RefreshVersions_Click(object s, RoutedEventArgs e)
    {
        try
        {
            await RefreshVersionsAsync();
            await LoadRemoteVersionsAsync();
        }
        catch (Exception ex) { Debug.WriteLine("RefreshVersions_Click: " + ex.Message); }
    }

    // Fills the "install new version" ComboBox with Mojang's release list.
    // Never throws: being offline must not break the launcher.
    private async Task LoadRemoteVersionsAsync()
    {
        try
        {
            var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));
            var all = await launcher.GetAllVersionsAsync();

            var names = all
                .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Name)
                .ToList();

            var previous = InstallVersionBox.SelectedItem as string;
            InstallVersionBox.ItemsSource = names;
            InstallVersionBox.SelectedItem =
                previous != null && names.Contains(previous) ? previous
                : names.Contains("1.8.9") ? "1.8.9"
                : names.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("LoadRemoteVersions: " + ex.Message);
            if (InstallStatusText != null)
                InstallStatusText.Text = "Could not load the Minecraft version list (offline?).";
        }
    }

    private void SelectVersion_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string id }) return;
        var entry = _versions.FirstOrDefault(v => v.RealId == id);
        if (entry == null) return;

        // Stays on the Versions page; the card shows the SELECTED badge. Go to Home to play.
        VersionBox.SelectedItem = entry;
        SetStatus($"{entry.DisplayName} selected.");
    }

    private async void InstallVersion_Click(object s, RoutedEventArgs e)
    {
        if (_installingVersion || _busy) return;
        if (InstallVersionBox.SelectedItem is not string versionId)
        {
            MessageBox.Show("Select a Minecraft version first.", "Frost Client");
            return;
        }

        _installingVersion = true;
        InstallButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        InstallStatusText.Text = $"Installing {versionId}...";

        try
        {
            Directory.CreateDirectory(_gameDir);
            var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));

            launcher.FileProgressChanged += (_, a) => Dispatcher.InvokeAsync(() =>
            {
                if (a.TotalTasks <= 0) return;
                InstallProgress.IsIndeterminate = false;
                InstallProgress.Minimum = 0;
                InstallProgress.Maximum = 100;
                InstallProgress.Value = a.ProgressedTasks * 100.0 / a.TotalTasks;
                InstallStatusText.Text = $"{a.Name} ({a.ProgressedTasks}/{a.TotalTasks})";
            });

            var kind = LoaderBox?.SelectedIndex is int i and >= 0 ? (LoaderKind)i : LoaderKind.Vanilla;

            // 1) vanilla (necessário como base de qualquer loader)
            await launcher.InstallAsync(versionId);

            // 2) loader por cima, se escolhido
            string finalId = versionId;
            if (kind != LoaderKind.Vanilla)
            {
                InstallStatusText.Text = $"Installing {kind} for {versionId}...";
                finalId = await FrostLoaders.InstallAsync(kind, versionId, launcher, _gameDir);
                await launcher.InstallAsync(finalId);   // libs do loader
            }

            InstallStatusText.Text = $"{finalId} installed.";
            _versionTab = kind == LoaderKind.Vanilla ? "vanilla" : "custom";
            await RefreshVersionsAsync(finalId);
        }
        catch (Exception ex)
        {
            InstallStatusText.Text = "Install failed: " + ex.Message;
        }
        finally
        {
            _installingVersion = false;
            InstallButton.IsEnabled = true;
            InstallProgress.Visibility = Visibility.Collapsed;
            InstallProgress.IsIndeterminate = true;
        }
    }

    // ── Rename ───────────────────────────────────────────────
    private void RenameVersionFromList_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string id }) return;

        _renameTargetId = id;
        RenameBox.Text = GetDisplayName(id);
        RenameOverlay.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void RenameBox_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; _ = CommitRenameAsync(); }
        else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; CancelRename(); }
    }

    private void RenameCancel_Click(object s, RoutedEventArgs e) => CancelRename();
    private async void RenameSave_Click(object s, RoutedEventArgs e) => await CommitRenameAsync();

    private void CancelRename()
    {
        _renameTargetId = null;
        RenameOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task CommitRenameAsync()
    {
        try
        {
            var id = _renameTargetId;
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renameTargetId = null;
            if (string.IsNullOrWhiteSpace(id)) return;

            var name = RenameBox.Text.Trim();
            _cfg.VersionNames ??= new Dictionary<string, string>();

            // Empty (or identical to the folder name) = go back to the real id.
            if (name.Length == 0 || name == id) _cfg.VersionNames.Remove(id);
            else _cfg.VersionNames[id] = name;

            SaveSilently();
            await RefreshVersionsAsync();   // keeps the current selection
        }
        catch (Exception ex) { Debug.WriteLine("Rename: " + ex.Message); }
    }

    // ── Delete version / account (shared confirm dialog) ─────
    private void DeleteVersion_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string id }) return;
        if (IsFrostClientVersion(id)) return;

        _pendingConfirmAccountId = null;
        _pendingConfirmVersionId = id;
        ConfirmTitle.Text = "Delete version?";
        ConfirmMessage.Text =
            $"\"{GetDisplayName(id)}\", its version files, and its isolated instance data (mods, saves, options) will be removed. This cannot be undone.";
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void ConfirmCancel_Click(object s, RoutedEventArgs e)
    {
        _pendingConfirmAccountId = null;
        _pendingConfirmVersionId = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmAccept_Click(object s, RoutedEventArgs e)
    {
        var accountId = _pendingConfirmAccountId;
        var versionId = _pendingConfirmVersionId;
        _pendingConfirmAccountId = null;
        _pendingConfirmVersionId = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;

        try
        {
            if (accountId != null) await DeleteAccountConfirmedAsync(accountId);
            else if (versionId != null) await DeleteVersionConfirmedAsync(versionId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Frost Client", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DeleteVersionConfirmedAsync(string versionId)
    {
        if (_gameRunning)
        {
            MessageBox.Show("Close the game before deleting a version.", "Frost Client",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsFrostClientVersion(versionId)) return;

        // Version IDs originate in the UI, but validate both paths before deleting.
        string versionsRoot = Path.GetFullPath(Path.Combine(_gameDir, "versions"));
        string instancesRoot = Path.GetFullPath(Path.Combine(_gameDir, "instances"));
        string versionsPrefix = versionsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string instancesPrefix = instancesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string versionDir = Path.GetFullPath(Path.Combine(versionsRoot, versionId));
        string instanceDir = Path.GetFullPath(Path.Combine(instancesRoot, versionId));
        if (!versionDir.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase) ||
            !instanceDir.StartsWith(instancesPrefix, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            SetStatus($"Deleting {GetDisplayName(versionId)}...");
            await Task.Run(() =>
            {
                if (Directory.Exists(versionDir)) Directory.Delete(versionDir, recursive: true);
                if (Directory.Exists(instanceDir)) Directory.Delete(instanceDir, recursive: true);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Could not completely delete the version and its instance data:\n\n" + ex.Message, "Frost Client",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cfg.VersionNames?.Remove(versionId);
        SaveSilently();
        await RefreshVersionsAsync();
        SetStatus(Loc.T("status_ready"));
    }

    // ── Settings ─────────────────────────────────────────────
    // These fire while InitializeComponent / LoadSettings are still setting initial
    // values, so they must do nothing until the window is loaded.
    private void Settings_ComboChanged(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveSilently();
    }

    private void Settings_TextChanged(object s, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveSilently();
    }

    private void DiscordToggle_Changed(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveSilently();

        try
        {
            if (_cfg.DiscordPresence) UpdatePresence("In Launcher", "Frost Client", null);
            else _discord?.ClearPresence();
        }
        catch { }
    }

    private void BrowseJava_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select java.exe / javaw.exe",
            Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe|Executables (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            JavaPathBox.Text = dialog.FileName;   // TextChanged saves it
        }
    }

    // NOTE: the original preset values were not in the uploaded files — adjust if you had your own.
    private void JvmPreset_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string preset }) return;

        JavaArgumentsBox.Text = preset switch
        {
            "Balanced"    => "-XX:+UseG1GC -XX:MaxGCPauseMillis=50 -XX:+DisableExplicitGC",
            "Performance" => "-XX:+UseG1GC -XX:+UnlockExperimentalVMOptions -XX:G1NewSizePercent=20 -XX:G1ReservePercent=20 -XX:MaxGCPauseMillis=50 -XX:G1HeapRegionSize=32M -XX:+DisableExplicitGC",
            "MaxFps"      => "-XX:+UseG1GC -XX:+UnlockExperimentalVMOptions -XX:MaxGCPauseMillis=30 -XX:+UseStringDeduplication -XX:+OptimizeStringConcat -XX:+DisableExplicitGC",
            _             => ""
        };
    }
}