using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using CmlLib.Core;
using Microsoft.Win32;

namespace FrostClient;

// Versions, rename, confirmation, and settings handlers.
public partial class MainWindow
{
    private string? _pendingConfirmVersionId;
    private string? _renameTargetId;

    // ──────────────────────────────────────────────────────────
    // VERSÕES
    // ──────────────────────────────────────────────────────────
    private void OpenVersions_Click(object s, RoutedEventArgs e)
    {
        VersionsOverlay.Visibility = Visibility.Visible;
        // If the network was unavailable at startup, try again when the panel opens.
        if (InstallVersionBox != null && InstallVersionBox.Items.Count == 0)
            _ = TryLoadRemoteVersionsAsync();
    }

    private void CloseVersions_Click(object s, RoutedEventArgs e) => VersionsOverlay.Visibility = Visibility.Collapsed;

    private async Task TryLoadRemoteVersionsAsync()
    {
        try { await LoadRemoteVersionsAsync(); } catch { }
    }

    private async Task LoadRemoteVersionsAsync()
    {
        var launcher = new MinecraftLauncher();
        var all = await launcher.GetAllVersionsAsync();
        var releases = all.Select(v => v.Name)
                          .Where(n => !string.IsNullOrEmpty(n))
                          .ToList();
        if (InstallVersionBox != null)
        {
            InstallVersionBox.ItemsSource = releases;
            if (releases.Any()) InstallVersionBox.SelectedIndex = 0;
        }
    }

    private async void InstallVersion_Click(object s, RoutedEventArgs e)
    {
        var installBtn = (s as Button) ?? InstallButton;

        if (InstallVersionBox?.SelectedItem is not string mc)
        { MessageBox.Show(Loc.T("select_mc_first")); return; }

        if (installBtn != null) installBtn.IsEnabled = false;
        if (InstallProgress != null) InstallProgress.Visibility = Visibility.Visible;
        if (InstallStatusText != null) InstallStatusText.Text = Loc.T("downloading_mc", mc);
        SetStatus(Loc.T("installing_mc", mc));

        try
        {
            var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));
            await launcher.InstallAsync(mc);
            if (InstallStatusText != null) InstallStatusText.Text = Loc.T("installed_ok", mc);
            SetStatus(Loc.T("installed_status", mc));
            await RefreshVersionsAsync(mc);
        }
        catch (Exception ex)
        {
            if (InstallStatusText != null) InstallStatusText.Text = Loc.T("install_error");
            SetStatus(Loc.T("failed"));
            MessageBox.Show(ex.ToString(), Loc.T("title_install_error"));
        }
        finally
        {
            if (installBtn != null) installBtn.IsEnabled = true;
            if (InstallProgress != null) InstallProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshVersions_Click(object s, RoutedEventArgs e) => _ = RefreshVersionsAsync();

    private void SelectVersion_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string realId }) return;
        var entry = _versions.FirstOrDefault(v => v.RealId == realId);
        if (entry == null) return;
        if (VersionBox != null) VersionBox.SelectedItem = entry;
        SetStatus(Loc.T("selected_version", entry.DisplayName));
        VersionsOverlay.Visibility = Visibility.Collapsed;
    }

    private void DeleteVersion_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string realId }) return;
        _pendingConfirmVersionId = realId;
        _pendingConfirmAccountId = null;
        ConfirmTitle.Text = Loc.T("delete_version_q");
        ConfirmMessage.Text = Loc.T("delete_version_msg", GetDisplayName(realId));
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private async Task DeleteVersionConfirmedAsync(string realId)
    {
        try
        {
            var dir = Path.Combine(_gameDir, "versions", realId);
            await Task.Run(() => { if (Directory.Exists(dir)) Directory.Delete(dir, true); });
            _cfg.VersionNames.Remove(realId);
            SaveSilently();
            SetStatus(Loc.T("deleted_version", realId));
            await RefreshVersionsAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("title_delete_error")); }
    }

    // ── Confirmation (used for accounts and versions) ────────────────
    private void ConfirmCancel_Click(object s, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _pendingConfirmVersionId = null;
        _pendingConfirmAccountId = null;
    }

    private async void ConfirmAccept_Click(object s, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        var versionId = _pendingConfirmVersionId;
        var accountId = _pendingConfirmAccountId;
        _pendingConfirmVersionId = null;
        _pendingConfirmAccountId = null;
        if (versionId != null) await DeleteVersionConfirmedAsync(versionId);
        else if (accountId != null) await DeleteAccountConfirmedAsync(accountId);
    }

    // ── Rename version ────────────────────────────────────────
    private void RenameVersionFromList_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string realId }) return;
        _renameTargetId = realId;
        RenameTitle.Text   = Loc.T("rename_version");
        RenameLabel.Text   = Loc.T("display_name");
        RenameSubText.Text = Loc.T("real_id", realId);
        RenameBox.Text     = GetDisplayName(realId);
        RenameOverlay.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void RenameCancel_Click(object s, RoutedEventArgs e)
    {
        RenameOverlay.Visibility = Visibility.Collapsed;
        _renameTargetId = null;
    }

    private void RenameSave_Click(object s, RoutedEventArgs e) => ApplyRename();

    private void RenameBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  ApplyRename();
        if (e.Key == Key.Escape) RenameCancel_Click(s, e);
    }

    private void ApplyRename()
    {
        if (_renameTargetId == null) return;
        var id = _renameTargetId;
        var newVal = RenameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(newVal)) newVal = id;
        if (newVal == id) _cfg.VersionNames.Remove(id);
        else _cfg.VersionNames[id] = newVal;

        SaveSilently();
        _ = RefreshVersionsAsync(GetSelectedEntry()?.RealId);

        RenameOverlay.Visibility = Visibility.Collapsed;
        _renameTargetId = null;
    }

    // ──────────────────────────────────────────────────────────
    // SETTINGS (Java, JVM, resolution, Discord)
    // ──────────────────────────────────────────────────────────
    private void Settings_TextChanged(object s, TextChangedEventArgs e)       { if (IsLoaded) SaveSilently(); }
    private void Settings_ComboChanged(object s, SelectionChangedEventArgs e) { if (IsLoaded) SaveSilently(); }

    private void DiscordToggle_Changed(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveSilently();
        if (DiscordToggle.IsChecked == true) UpdatePresence("In Launcher", "Frost Client", null);
        else _discord?.ClearPresence();
    }

    private void BrowseJava_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = Loc.T("select_javaw"), Filter = "Java|javaw.exe|All|*.*", CheckFileExists = true };
        if (d.ShowDialog() == true && JavaPathBox != null) { JavaPathBox.Text = d.FileName; SaveSilently(); }
    }

    private void JvmPreset_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string key } || JavaArgumentsBox == null) return;
        JavaArgumentsBox.Text = key switch
        {
            "Balanced"    => "-XX:+UseG1GC -XX:MaxGCPauseMillis=50 -XX:+UnlockExperimentalVMOptions",
            "Performance" => "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 -XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch",
            "MaxFps"      => "-XX:+UseG1GC -XX:MaxGCPauseMillis=30 -XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch -XX:+UseStringDeduplication",
            _             => ""
        };
    }
}
