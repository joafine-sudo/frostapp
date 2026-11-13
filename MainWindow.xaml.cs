using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.ProcessBuilder;
using DiscordRPC;

namespace FrostClient;

// ──────────────────────────────────────────────────────────
// DATA MODELS
// ──────────────────────────────────────────────────────────
public sealed class VersionEntry
{
    public string RealId { get; }              // pasta / id real da versão
    public string DisplayName { get; set; }    // nome personalizado (ou igual ao RealId)
    public string Letter => DisplayName.Length > 0 ? DisplayName[0].ToString().ToUpperInvariant() : "?";

    public VersionEntry(string realId, string displayName)
    {
        RealId = realId;
        DisplayName = displayName;
    }

    // A ComboBox usa ToString() na caixa de seleção fechada.
    public override string ToString() => DisplayName;
}

// Conta guardada: offline ou Microsoft.
public sealed class AccountEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "offline";
    public string? Uuid { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string TypeLabel => Type == "microsoft" ? "Microsoft" : "Offline";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Letter => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    [System.Text.Json.Serialization.JsonIgnore]
    public string AvatarUrl => Type == "microsoft" && !string.IsNullOrWhiteSpace(Uuid)
        ? $"https://mc-heads.net/avatar/{Uuid.Replace("-", "")}/64"
        : $"https://minotar.net/avatar/{Uri.EscapeDataString(DisplayName)}/64";
}

// ──────────────────────────────────────────────────────────
// MAIN WINDOW
// ──────────────────────────────────────────────────────────
public partial class MainWindow : Window
{
    private static readonly HttpClient ProfileHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    // ── Paths ──────────────────────────────────────────────
    private readonly string _gameDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    private string GameModsDir(string? versionId = null)
    {
        if (string.IsNullOrWhiteSpace(versionId) ||
            versionId.Contains("frost client", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_gameDir, "mods");

        return Path.Combine(_gameDir, "versions", versionId, "mods");
    }

    private readonly string _settingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "FrostClient", "settings.json");

    // ── State ──────────────────────────────────────────────
    private readonly JELoginHandler _loginHandler;
    private MSession? _session;
    private bool _busy;
    private bool _gameRunning;

    private List<VersionEntry> _versions = new();
    private List<AccountEntry> _accounts = new();
    private string? _pendingConfirmAccountId;

    // Prevents an old avatar request from replacing the current account avatar.
    private int _profileRequestId;

    // ── Discord ────────────────────────────────────────────
    private DiscordRpcClient? _discord;
    private const string AppId    = "1426668060642644209";
    private const string LargeKey = "icon_6_";
    private const string SmallKey = "logo";

    // ──────────────────────────────────────────────────────────
    public MainWindow()
    {
        try
        {
            _loginHandler = JELoginHandlerBuilder.BuildDefault();
            Loc.SetLanguage(Loc.DetectSystemLanguage());
            Loc.LanguageChanged += RefreshLocalizedTexts;
            InitializeComponent();
            LoadSettings();
            SyncLanguageBox();
            InitDiscord();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"UI Error:\n\n{ex.Message}\n\n{ex.StackTrace}",
                            "Frost Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }

    // ──────────────────────────────────────────────────────────
    // DISCORD
    // ──────────────────────────────────────────────────────────
    private void InitDiscord()
    {
        try
        {
            _discord = new DiscordRpcClient(AppId);
            _discord.OnError += (_, e) => Debug.WriteLine("Discord: " + e.Message);
            _discord.Initialize();
            UpdatePresence("In Launcher", "Frost Client", null);
        }
        catch (Exception ex) { Debug.WriteLine("Discord: " + ex.Message); }
    }

    private void UpdatePresence(string details, string state, string? version)
    {
        if (_discord == null || !_cfg.DiscordPresence) return;
        try
        {
            _discord.SetPresence(new RichPresence
            {
                Details = details,
                State = version ?? state,
                Assets = new Assets
                {
                    LargeImageKey  = LargeKey,
                    LargeImageText = "Frost Client",
                    SmallImageKey  = version != null ? SmallKey : null,
                    SmallImageText = version != null ? "Frost Client" : null
                },
                Timestamps = Timestamps.Now
            });
        }
        catch { /* swallow */ }
    }

    private async Task CheckForLauncherUpdateAsync()
    {
        try
        {
            await FrostUpdater.CheckAndUpdateAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Updater: " + ex.Message);
        }
    }

    // ──────────────────────────────────────────────────────────
    // WINDOW LOAD / CLOSE
    // ──────────────────────────────────────────────────────────
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = CheckForLauncherUpdateAsync();

        RenderAccounts();
        await RestoreActiveAccountAsync();
        UpdateProfileUI();

        // No account yet (first launch): open the account panel immediately.
        if (_accounts.Count == 0) AccountOverlay.Visibility = Visibility.Visible;

        await EnsureFrostResourcesAsync();
        await RefreshVersionsAsync();
        SetStatus(Loc.T("status_ready"));
        try { await LoadRemoteVersionsAsync(); } catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _discord?.ClearPresence(); _discord?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch { }
    }

    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object s, RoutedEventArgs e)
    {
        try { _discord?.ClearPresence(); _discord?.Dispose(); } catch { }
        Application.Current.Shutdown();
    }

    private void SetStatus(string t) { if (StatusText != null) StatusText.Text = t; }

    private void SetPlayText(string t) { if (PlayText != null) PlayText.Text = t; }

    // ──────────────────────────────────────────────────────────
    // PROFILE UI
    // ──────────────────────────────────────────────────────────
    private static bool IsValidName(string? n) =>
        !string.IsNullOrEmpty(n) && n.Length >= 3 && n.Length <= 16
        && n.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private void UpdateProfileUI()
    {
        bool microsoft = _session?.UserType == "msa";
        string name = _session?.Username ?? Loc.T("not_logged_in");

        if (TopPlayerName != null) TopPlayerName.Text = name;

        var activeAcc = GetActiveAccount();
        if (TopPlayerType != null)
            TopPlayerType.Text = _session == null
                ? ""
                : (activeAcc?.TypeLabel ?? (microsoft ? "Microsoft" : "Offline"));

        if (TopPlayerAvatar != null) TopPlayerAvatar.Source = null;

        int requestId = ++_profileRequestId;
        _ = LoadPlayerProfileAsync(name, microsoft ? _session?.UUID : null, requestId);
    }

    private async Task LoadPlayerProfileAsync(string playerName, string? knownUuid, int requestId)
    {
        if (string.IsNullOrWhiteSpace(playerName) || playerName == Loc.T("not_logged_in")) return;

        string? uuid = string.IsNullOrWhiteSpace(knownUuid) ? null : knownUuid.Replace("-", "");
        if (string.IsNullOrWhiteSpace(uuid) && IsValidName(playerName))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(playerName)}");
                request.Headers.UserAgent.ParseAdd("FrostClient/1.0");
                using var response = await ProfileHttp.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (json.RootElement.TryGetProperty("id", out var id)) uuid = id.GetString();
                }
            }
            catch { }
        }

        var avatarUrls = new List<string>();
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            avatarUrls.Add($"https://mc-heads.net/avatar/{Uri.EscapeDataString(uuid)}/64");
            avatarUrls.Add($"https://crafatar.com/avatars/{Uri.EscapeDataString(uuid)}?size=64&overlay=true");
        }
        avatarUrls.Add($"https://minotar.net/avatar/{Uri.EscapeDataString(playerName)}/64");
        avatarUrls.Add($"https://mc-heads.net/avatar/{Uri.EscapeDataString(playerName)}/64");

        byte[]? avatarBytes = await TryDownloadFirstAsync(avatarUrls, requestId);
        if (requestId != _profileRequestId) return;
        if (avatarBytes != null)
            await Dispatcher.InvokeAsync(() => SetAvatarImage(TopPlayerAvatar, avatarBytes));
    }

    private async Task<byte[]?> TryDownloadFirstAsync(List<string> urls, int requestId)
    {
        foreach (var url in urls)
        {
            if (requestId != _profileRequestId) return null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("FrostClient/1.0");
                using var response = await ProfileHttp.SendAsync(request);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 100) continue;
                if (requestId != _profileRequestId) return null;
                return bytes;
            }
            catch { }
        }
        return null;
    }

    private static void SetAvatarImage(Image? target, byte[] bytes)
    {
        if (target == null) return;
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        target.Source = bitmap;
    }

    // ──────────────────────────────────────────────────────────
    // SETTINGS
    // ──────────────────────────────────────────────────────────
    private sealed class AppSettings
    {
        public int    RamGb           { get; set; } = 4;
        public string JavaArguments   { get; set; } = "";
        public bool   SetupComplete   { get; set; } = true;
        public string JavaPath        { get; set; } = "";
        public int    ResolutionIndex { get; set; } = 0;
        public int    OnLaunchIndex   { get; set; } = 0;
        public bool   DiscordPresence { get; set; } = true;
        // "green" = legacy green background; otherwise, the image file name (default.png, ...)
        public string Wallpaper       { get; set; } = "green";
        public string Language        { get; set; } = "";
        public Dictionary<string, string> VersionNames { get; set; } = new();

        public List<AccountEntry> Accounts        { get; set; } = new();
        public string             ActiveAccountId { get; set; } = "";

        // Legacy fields kept only to migrate older settings.json files.
        public string Username            { get; set; } = "";
        public bool   IsMicrosoftLoggedIn { get; set; } = false;
    }

    private AppSettings _cfg = new();

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
                _cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile)) ?? new();

            if (!string.IsNullOrWhiteSpace(_cfg.Language)) Loc.SetLanguage(_cfg.Language);

            RamSlider.Value = Math.Clamp(_cfg.RamGb, 1, 16);
            JavaPathBox.Text = _cfg.JavaPath;
            JavaArgumentsBox.Text = _cfg.JavaArguments;
            ResolutionBox.SelectedIndex = Math.Clamp(_cfg.ResolutionIndex, 0, ResolutionBox.Items.Count - 1);
            OnLaunchBox.SelectedIndex = Math.Clamp(_cfg.OnLaunchIndex, 0, OnLaunchBox.Items.Count - 1);
            DiscordToggle.IsChecked = _cfg.DiscordPresence;

            // "green" was the old option; keep compatibility without showing it in the UI.
            if (string.Equals(_cfg.Wallpaper, "green", StringComparison.OrdinalIgnoreCase))
                _cfg.Wallpaper = "default.png";

            var match = WallpaperBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == _cfg.Wallpaper);
            WallpaperBox.SelectedItem = match ?? WallpaperBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            ApplyWallpaper(_cfg.Wallpaper);

            _accounts = _cfg.Accounts ?? new();
            foreach (var account in _accounts)
                if (account.Type != "microsoft") account.Type = "offline";

            // Migrate the old single-account format.
            if (_accounts.Count == 0 && !string.IsNullOrWhiteSpace(_cfg.Username))
            {
                var migrated = new AccountEntry
                {
                    DisplayName = _cfg.Username,
                    Type        = _cfg.IsMicrosoftLoggedIn ? "microsoft" : "offline",
                };
                _accounts.Add(migrated);
                _cfg.Accounts = _accounts;
                _cfg.ActiveAccountId = migrated.Id;
            }

            if (NewAccountNameBox != null) NewAccountNameBox.Text = "";
        }
        catch { /* usa os valores por defeito */ }
    }

    private void SaveSilently()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            _cfg.RamGb         = (int)(RamSlider?.Value ?? 4);
            _cfg.SetupComplete = true;
            if (JavaPathBox != null) _cfg.JavaPath = JavaPathBox.Text.Trim();
            if (JavaArgumentsBox != null) _cfg.JavaArguments = JavaArgumentsBox.Text.Trim();
            if (ResolutionBox != null && ResolutionBox.SelectedIndex >= 0) _cfg.ResolutionIndex = ResolutionBox.SelectedIndex;
            if (OnLaunchBox != null && OnLaunchBox.SelectedIndex >= 0) _cfg.OnLaunchIndex = OnLaunchBox.SelectedIndex;
            if (DiscordToggle != null) _cfg.DiscordPresence = DiscordToggle.IsChecked != false;
            _cfg.Accounts      = _accounts;
            File.WriteAllText(_settingsFile,
                JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Debug.WriteLine("Save error: " + ex.Message); }
    }

    private void OpenSettings_Click(object s, RoutedEventArgs e)  => SettingsOverlay.Visibility = Visibility.Visible;
    private void CloseSettings_Click(object s, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SaveSilently();
    }

    private void RamSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RamLabel != null) RamLabel.Text = $"{(int)e.NewValue} GB";
        if (IsLoaded) SaveSilently();
    }

    // "green" shows the green background; any other value is an image (next to the .exe
    // takes priority, otherwise use the image embedded in the .exe).
    private void ApplyWallpaper(string? fileName)
    {
        if (WallpaperRect == null || WallpaperShade == null || BackgroundImageBrush == null) return;

        if (string.IsNullOrWhiteSpace(fileName) || fileName == "green")
        {
            WallpaperRect.Visibility = Visibility.Collapsed;
            WallpaperShade.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var external = Path.Combine(AppContext.BaseDirectory, fileName);
            var uri = File.Exists(external)
                ? new Uri(external, UriKind.Absolute)
                : new Uri("pack://application:,,,/" + fileName, UriKind.Absolute);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = uri;
            bitmap.EndInit();
            bitmap.Freeze();
            BackgroundImageBrush.ImageSource = bitmap;
            WallpaperRect.Visibility = Visibility.Visible;
            WallpaperShade.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wallpaper '{fileName}' could not be loaded: {ex.Message}");
        }
    }

    private void Wallpaper_Changed(object s, SelectionChangedEventArgs e)
    {
        if (WallpaperBox?.SelectedItem is not ComboBoxItem { Tag: string file }) return;
        ApplyWallpaper(file);
        if (IsLoaded)
        {
            _cfg.Wallpaper = file;
            SaveSilently();
        }
    }

    // ── LANGUAGE ───────────────────────────────────────────────
    private void Language_Changed(object s, SelectionChangedEventArgs e)
    {
        if (LanguageBox?.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        _cfg.Language = code;
        Loc.SetLanguage(code);
        if (IsLoaded) SaveSilently();
    }

    private void SyncLanguageBox()
    {
        if (LanguageBox == null) return;
        var items = LanguageBox.Items.OfType<ComboBoxItem>().ToList();
        LanguageBox.SelectedItem = items.FirstOrDefault(i => (i.Tag as string) == Loc.CurrentCode)
                                   ?? items.FirstOrDefault();
    }

    // Text assigned from code (rather than bindings) is refreshed when the language changes.
    private void RefreshLocalizedTexts()
    {
        if (!IsLoaded) return;
        try
        {
            if (_session == null)
            {
                if (TopPlayerName != null) TopPlayerName.Text = Loc.T("not_logged_in");
                if (TopPlayerType != null) TopPlayerType.Text = "";
            }
            else if (TopPlayerType != null)
            {
                TopPlayerType.Text = GetActiveAccount()?.TypeLabel
                    ?? (_session.UserType == "msa" ? "Microsoft" : "Offline");
            }

            RenderAccounts();

            var entry = GetSelectedEntry();
            if (entry != null) UpdateHomeVersion(entry);
            else SetPlayText(Loc.T("no_version"));

            if (!_busy && !_gameRunning) SetStatus(Loc.T("status_ready"));
        }
        catch (Exception ex) { Debug.WriteLine("RefreshLocalizedTexts: " + ex.Message); }
    }

    // ──────────────────────────────────────────────────────────
    // ACCOUNTS (offline + Microsoft)
    // ──────────────────────────────────────────────────────────
    private AccountEntry? GetActiveAccount() =>
        _accounts.FirstOrDefault(a => a.Id == _cfg.ActiveAccountId) ?? _accounts.FirstOrDefault();

    private void RenderAccounts()
    {
        foreach (var a in _accounts) a.IsActive = a.Id == _cfg.ActiveAccountId;
        if (AccountsListBox != null) { AccountsListBox.ItemsSource = null; AccountsListBox.ItemsSource = _accounts; }
    }

    private void OpenAccount_Click(object s, RoutedEventArgs e)  => AccountOverlay.Visibility = Visibility.Visible;
    private void CloseAccount_Click(object s, RoutedEventArgs e) => AccountOverlay.Visibility = Visibility.Collapsed;

    private async Task RestoreActiveAccountAsync()
    {
        var acc = GetActiveAccount();
        if (acc == null) { _session = null; return; }

        if (acc.Type == "microsoft")
        {
            try
            {
                SetStatus(Loc.T("restoring_ms"));
                _session = await _loginHandler.AuthenticateSilently();
            }
            catch
            {
                _session = null;
                SetStatus(Loc.T("ms_expired"));
            }
        }
        else
        {
            _session = MSession.CreateOfflineSession(acc.DisplayName);
        }
    }

    private void AddOfflineAccountConfirm_Click(object s, RoutedEventArgs e)
    {
        var name = NewAccountNameBox?.Text?.Trim() ?? "";
        if (!IsValidName(name))
        {
            MessageBox.Show(Loc.T("err_name"), Loc.T("title_invalid_name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var acc = new AccountEntry { DisplayName = name, Type = "offline" };
        _accounts.Add(acc);
        _cfg.Accounts = _accounts;
        _cfg.ActiveAccountId = acc.Id;
        _session = MSession.CreateOfflineSession(name);

        UpdateProfileUI();
        SaveSilently();
        RenderAccounts();
        SetStatus(Loc.T("offline_added", name));
        if (NewAccountNameBox != null) NewAccountNameBox.Text = "";
        AccountOverlay.Visibility = Visibility.Collapsed;
    }

    private async void AddMicrosoftAccountConfirm_Click(object s, RoutedEventArgs e) => await MicrosoftLoginAsync();

    private async Task MicrosoftLoginAsync()
    {
        if (MicrosoftLoginButton != null) MicrosoftLoginButton.IsEnabled = false;
        try
        {
            SetStatus(Loc.T("opening_ms"));
            var session = await _loginHandler.AuthenticateInteractively();
            _session = session;

            var existing = _accounts.FirstOrDefault(a => a.Type == "microsoft" && a.Uuid == session.UUID);
            if (existing != null)
            {
                existing.DisplayName = session.Username;
                _cfg.ActiveAccountId = existing.Id;
            }
            else
            {
                var acc = new AccountEntry { DisplayName = session.Username, Type = "microsoft", Uuid = session.UUID };
                _accounts.Add(acc);
                _cfg.ActiveAccountId = acc.Id;
            }
            _cfg.Accounts = _accounts;

            UpdateProfileUI();
            RenderAccounts();
            SaveSilently();
            SetStatus(Loc.T("logged_in_as", session.Username));
            AccountOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("login_failed"));
            MessageBox.Show(ex.Message, Loc.T("title_login_error"));
        }
        finally { if (MicrosoftLoginButton != null) MicrosoftLoginButton.IsEnabled = true; }
    }

    private async void SelectAccount_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string id }) return;
        var acc = _accounts.FirstOrDefault(a => a.Id == id);
        if (acc == null) return;

        _cfg.ActiveAccountId = acc.Id;
        await RestoreActiveAccountAsync();
        UpdateProfileUI();
        SaveSilently();
        RenderAccounts();
        SetStatus(Loc.T("active_account_status", acc.DisplayName));
        AccountOverlay.Visibility = Visibility.Collapsed;
    }

    private void DeleteAccount_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string id }) return;
        var acc = _accounts.FirstOrDefault(a => a.Id == id);
        if (acc == null) return;

        _pendingConfirmAccountId = id;
        _pendingConfirmVersionId = null;
        ConfirmTitle.Text = Loc.T("delete_account_q");
        ConfirmMessage.Text = Loc.T("delete_account_msg", acc.DisplayName);
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private async Task DeleteAccountConfirmedAsync(string id)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == id);
        if (acc == null) return;
        _accounts.Remove(acc);
        _cfg.Accounts = _accounts;

        if (_cfg.ActiveAccountId == id)
        {
            _cfg.ActiveAccountId = _accounts.FirstOrDefault()?.Id ?? "";
            await RestoreActiveAccountAsync();
            UpdateProfileUI();
        }

        SaveSilently();
        RenderAccounts();
    }


    // ──────────────────────────────────────────────────────────
    // LINKS / PASTAS
    // ──────────────────────────────────────────────────────────
    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { }
    }
    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }
    private void Discord_Click(object s, RoutedEventArgs e) => OpenUrl("https://eu.frostclient.org/invite/discord");
    private void Website_Click(object s, RoutedEventArgs e) => OpenUrl("https://frostclient.org");
    private void Github_Click(object s, RoutedEventArgs e) => OpenUrl("https://github.com/joafine-sudo/frostapp");
    private void Youtube_Click(object s, RoutedEventArgs e) => OpenUrl("https://www.youtube.com/@FrostClient-q4i");

    // ──────────────────────────────────────────────────────────
    // VERSIONS (selection list only; no install/rename/delete from this UI)
    // ──────────────────────────────────────────────────────────
    private string GetDisplayName(string realId)
        => _cfg.VersionNames.TryGetValue(realId, out var n) ? n : realId;

    private async Task RefreshVersionsAsync(string? selectId = null)
    {
        try
        {
            var vDir = Path.Combine(_gameDir, "versions");
            Directory.CreateDirectory(vDir);

            var ids = await Task.Run(() =>
                Directory.GetDirectories(vDir)
                    .Where(d => File.Exists(Path.Combine(d, Path.GetFileName(d) + ".json")))
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList());

            _versions = ids.Select(id => new VersionEntry(id!, GetDisplayName(id!))).ToList();

            if (VersionBox != null)
            {
                VersionBox.ItemsSource   = _versions;
                VersionBox.SelectedIndex = 0;
            }
            if (VersionList != null) VersionList.ItemsSource = _versions;

            var targetEntry = (selectId != null ? _versions.FirstOrDefault(e => e.RealId == selectId) : null)
                ?? _versions.FirstOrDefault(e => e.RealId.Contains("frost client", StringComparison.OrdinalIgnoreCase))
                ?? _versions.FirstOrDefault();

            if (targetEntry != null)
            {
                if (VersionBox != null) VersionBox.SelectedItem = targetEntry;
                UpdateHomeVersion(targetEntry);
            }
            else
            {
                SetPlayText(Loc.T("no_version"));
                if (HomeVersionLabel != null) HomeVersionLabel.Text = Loc.T("install_to_start");
            }
        }
        catch (Exception ex) { Debug.WriteLine("RefreshVersions: " + ex.Message); }
    }

    private VersionEntry? GetSelectedEntry() => VersionBox?.SelectedItem as VersionEntry;

    private void UpdateHomeVersion(VersionEntry e)
    {
        SetPlayText(Loc.T("play"));
        if (HomeVersionLabel != null) HomeVersionLabel.Text = e.RealId;
    }

    private void Version_Changed(object s, SelectionChangedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry == null) return;
        UpdateHomeVersion(entry);
        if (!_busy && !_gameRunning) UpdatePresence("In Launcher", $"Selected {entry.DisplayName}", null);
    }

    // ──────────────────────────────────────────────────────────
    // FROST RESOURCES
    // ──────────────────────────────────────────────────────────
    private const string ResourcesVersion = "4";
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(20) };
    private TaskCompletionSource<bool>? _resourcesChoice;
    private string _frostSetupInfo = "not run";

    private string FrostVersionDir => Path.Combine(_gameDir, "versions", "frost client");
    private string FrostVersionJson => Path.Combine(FrostVersionDir, "frost client.json");
    private string FrostVersionJar => Path.Combine(FrostVersionDir, "frost client.jar");
    private string ForgeJarPath => Path.Combine(_gameDir, "libraries", "net", "minecraftforge", "forge",
        "1.8.9-11.15.1.2318-1.8.9", "forge-1.8.9-11.15.1.2318-1.8.9.jar");
    private string ResourcesMarker => Path.Combine(FrostVersionDir, ".resources-version");

    private bool FrostResourcesReady()
    {
        try
        {
            return File.Exists(FrostVersionJson)
                && File.Exists(FrostVersionJar)
                && File.Exists(ForgeJarPath)
                && File.Exists(ResourcesMarker)
                && File.ReadAllText(ResourcesMarker).Trim() == ResourcesVersion;
        }
        catch { return false; }
    }

    private void SetResourcesProgress(double pct, string? text = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetResourcesProgress(pct, text)));
            return;
        }

        pct = Math.Clamp(pct, 0, 100);
        if (ResourcesProgress != null) ResourcesProgress.Value = pct;
        if (ResourcesPercentText != null) ResourcesPercentText.Text = $"{(int)pct}%";
        if (text != null && ResourcesStatusText != null) ResourcesStatusText.Text = text;
    }

    private void ResourcesRetry_Click(object s, RoutedEventArgs e) => _resourcesChoice?.TrySetResult(true);
    private void ResourcesSkip_Click (object s, RoutedEventArgs e) => _resourcesChoice?.TrySetResult(false);

    private async Task<bool> EnsureFrostResourcesAsync()
    {
        if (FrostResourcesReady()) { _frostSetupInfo = "already installed"; return true; }

        while (true)
        {
            try
            {
                await DownloadAndInstallResourcesAsync();
                _frostSetupInfo = "ok (version files and libraries downloaded)";
                return FrostResourcesReady();
            }
            catch (Exception ex)
            {
                _frostSetupInfo = "ERROR: " + ex.Message;
                ResourcesOverlay.Visibility = Visibility.Visible;
                ResourcesProgress.Visibility = Visibility.Collapsed;
                ResourcesPercentText.Text = "";
                ResourcesStatusText.Text = Loc.T("download_failed", ex.Message);
                ResourcesRetryButton.Visibility = Visibility.Visible;
                ResourcesSkipButton.Visibility  = Visibility.Visible;

                _resourcesChoice = new TaskCompletionSource<bool>();
                var retry = await _resourcesChoice.Task;

                ResourcesRetryButton.Visibility = Visibility.Collapsed;
                ResourcesSkipButton.Visibility  = Visibility.Collapsed;
                ResourcesProgress.Visibility    = Visibility.Visible;
                if (!retry) { ResourcesOverlay.Visibility = Visibility.Collapsed; return false; }
            }
        }
    }

    private async Task DownloadFileAsync(string url, string destination, Action<long, long>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("FrostClient/1.0");
            using var response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count));
                received += count;
                progress?.Invoke(received, total);
            }
            await output.FlushAsync();
            File.Move(temp, destination, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string MavenPathFromCoordinate(string coordinate)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3) throw new InvalidDataException("Invalid library coordinate: " + coordinate);
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    private static bool ExtractEmbedded(string resourceName, string destination)
    {
        using var s = typeof(MainWindow).Assembly.GetManifestResourceStream(resourceName);
        if (s == null) return false;
        using var f = File.Create(destination);
        s.CopyTo(f);
        return true;
    }

    private async Task DownloadAndInstallResourcesAsync()
    {
        ResourcesOverlay.Visibility = Visibility.Visible;
        SetResourcesProgress(0, Loc.T("res_installing"));

        var bundledVersionDir = Path.Combine(AppContext.BaseDirectory, "default_version", "frost client");
        var bundledJson = Path.Combine(bundledVersionDir, "frost client.json");
        var bundledJar = Path.Combine(bundledVersionDir, "frost client.jar");

        Directory.CreateDirectory(FrostVersionDir);
        if (File.Exists(bundledJson) && File.Exists(bundledJar))
        {
            File.Copy(bundledJson, FrostVersionJson, overwrite: true);
            File.Copy(bundledJar, FrostVersionJar, overwrite: true);
        }
        else if (!(ExtractEmbedded("FrostBundle.json", FrostVersionJson) &&
                   ExtractEmbedded("FrostBundle.jar",  FrostVersionJar)))
        {
            throw new FileNotFoundException(
                "Frost Client version files were not found (neither beside the launcher nor embedded in it).");
        }
        SetResourcesProgress(20, Loc.T("res_reading"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(FrostVersionJson));
        if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Frost Client JSON has no valid 'libraries' list.");

        var jobs = new List<(string Url, string Destination)>();
        foreach (var library in libraries.EnumerateArray())
        {
            string? coordinate = library.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(coordinate)) continue;

            string relativePath;
            string? downloadUrl = null;
            if (library.TryGetProperty("downloads", out var downloads) &&
                downloads.TryGetProperty("artifact", out var artifact))
            {
                if (artifact.TryGetProperty("path", out var pathNode))
                    relativePath = pathNode.GetString() ?? MavenPathFromCoordinate(coordinate);
                else relativePath = MavenPathFromCoordinate(coordinate);
                if (artifact.TryGetProperty("url", out var urlNode))
                    downloadUrl = urlNode.GetString();
            }
            else
            {
                relativePath = MavenPathFromCoordinate(coordinate);
                string repository = library.TryGetProperty("url", out var repoNode)
                    ? (repoNode.GetString() ?? "https://libraries.minecraft.net/")
                    : "https://libraries.minecraft.net/";
                downloadUrl = new Uri(new Uri(repository.EndsWith('/') ? repository : repository + '/'),
                    relativePath.Replace('\\', '/')).ToString();
            }

            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                       .Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(_gameDir, "libraries", relativePath));
            var libraryRoot = Path.GetFullPath(Path.Combine(_gameDir, "libraries")) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid library path in version JSON.");

            if (File.Exists(destination) && new FileInfo(destination).Length > 0) continue;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                string repository = library.TryGetProperty("url", out var repoNode)
                    ? (repoNode.GetString() ?? "https://libraries.minecraft.net/")
                    : "https://libraries.minecraft.net/";
                downloadUrl = new Uri(new Uri(repository.EndsWith('/') ? repository : repository + '/'),
                    relativePath.Replace(Path.DirectorySeparatorChar, '/')).ToString();
            }
            jobs.Add((downloadUrl, destination));
        }

        int totalJobs = jobs.Count;
        int completed = 0;
        SetResourcesProgress(25, Loc.T("res_downloading_libs", totalJobs));
        using var semaphore = new System.Threading.SemaphoreSlim(4);
        var tasks = jobs.Select(async job =>
        {
            await semaphore.WaitAsync();
            try
            {
                await DownloadFileAsync(job.Url, job.Destination);
                int done = System.Threading.Interlocked.Increment(ref completed);
                SetResourcesProgress(25 + (totalJobs == 0 ? 75 : done * 75d / totalJobs),
                    Loc.T("res_libs_progress", done, totalJobs));
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);

        if (!File.Exists(ForgeJarPath))
            throw new FileNotFoundException("Forge 1.8.9 library is missing after dependency download.", ForgeJarPath);

        File.WriteAllText(ResourcesMarker, ResourcesVersion);
        SetResourcesProgress(100, Loc.T("res_done"));
        await Task.Delay(400);
        ResourcesOverlay.Visibility = Visibility.Collapsed;
    }

    // ──────────────────────────────────────────────────────────
    // LAUNCH GAME
    // ──────────────────────────────────────────────────────────
    private static (int W, int H)? ResolutionFor(int index) => index switch
    {
        1 => (1280, 720),
        2 => (1600, 900),
        3 => (1920, 1080),
        _ => null
    };

    private async void Play_Click(object s, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_session == null)
        {
            var activeAcc = GetActiveAccount();
            if (activeAcc == null)
            {
                AccountOverlay.Visibility = Visibility.Visible;
                return;
            }
            await RestoreActiveAccountAsync();
            UpdateProfileUI();
            if (_session == null)
            {
                MessageBox.Show(Loc.T("restore_account_failed"), "Frost Client");
                return;
            }
        }

        var entry = GetSelectedEntry();
        if (entry == null) { MessageBox.Show(Loc.T("no_version_selected")); return; }
        var realId = entry.RealId;

        _busy = true;
        if (PlayButton != null) PlayButton.IsEnabled = false;
        SetStatus(Loc.T("preparing", entry.DisplayName));
        UpdatePresence($"Playing {entry.DisplayName}", realId, realId);

        try
        {
            Directory.CreateDirectory(_gameDir);
            Directory.CreateDirectory(GameModsDir(realId));

            if (realId.Contains("frost client", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(Loc.T("checking_resources"));
                var ok = await EnsureFrostResourcesAsync();
                if (!ok || !File.Exists(ForgeJarPath))
                {
                    MessageBox.Show(
                        Loc.T("resources_missing_box", _frostSetupInfo),
                        "Frost Client", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetStatus(Loc.T("missing_resources"));
                    UpdatePresence("In Launcher", "Frost Client", null);
                    return;
                }
            }

            var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));
            var options  = new MLaunchOption
            {
                Session             = _session,
                MaximumRamMb        = (int)(RamSlider?.Value ?? 4) * 1024,
                GameLauncherName    = "Frost Client",
                GameLauncherVersion = "1.0.11"
            };

            // Java / JVM arguments / resolution are still read from settings.json
            // (there is no current UI for them, but existing settings are preserved).
            var javaPath = _cfg.JavaPath?.Trim();
            if (!string.IsNullOrWhiteSpace(javaPath) && File.Exists(javaPath))
                options.JavaPath = javaPath;

            var jvmArgs = _cfg.JavaArguments?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(jvmArgs))
                options.ExtraJvmArguments = new[] { MArgument.FromCommandLine(jvmArgs) };

            if (ResolutionFor(_cfg.ResolutionIndex) is { } res)
            {
                options.ScreenWidth  = res.W;
                options.ScreenHeight = res.H;
            }

            // IMPORTANTE:
            // Install files separately before creating the process.
            // With CmlLib.Core 4.0.6, InstallAsync installs missing files
            // and includes the appropriate Java Runtime when required.
            // This allows the launcher to work on a machine without Java installed.
            SetStatus(Loc.T("verifying"));
            await Task.Delay(300);

            SetStatus(Loc.T("installing_mc_java"));
            await launcher.InstallAsync(realId);

            SetStatus(Loc.T("preparing_java"));
            var process = await launcher.BuildProcessAsync(realId, options);

            process.EnableRaisingEvents = true;
            _gameRunning = true;

            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FrostClient", "last-launch.log");
            var logSb = new System.Text.StringBuilder();
            logSb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            logSb.AppendLine($"Version: {realId}");
            logSb.AppendLine($"Java: {process.StartInfo.FileName}");
            logSb.AppendLine($"Frost setup: {_frostSetupInfo}");
            logSb.AppendLine($"Forge jar present: {File.Exists(ForgeJarPath)}");
            logSb.AppendLine($"Launcher exe: {Environment.ProcessPath} ({(File.Exists(Environment.ProcessPath) ? new FileInfo(Environment.ProcessPath!).Length / 1024 / 1024 : 0)} MB)");
            logSb.AppendLine($"OS: {Environment.OSVersion}");
            logSb.AppendLine($"RAM requested: {options.MaximumRamMb} MB");
            logSb.AppendLine("----- game output -----");

            process.StartInfo.UseShellExecute        = false;
            process.StartInfo.CreateNoWindow         = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError  = true;
            process.OutputDataReceived += (_, a) => { if (a.Data != null) lock (logSb) logSb.AppendLine(a.Data); };
            process.ErrorDataReceived  += (_, a) => { if (a.Data != null) lock (logSb) logSb.AppendLine(a.Data); };

            process.Exited += (_, _) =>
            {
                int code = -1;
                try { process.WaitForExit(); code = process.ExitCode; } catch { }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    lock (logSb) { logSb.AppendLine($"----- exit code: {code} -----"); File.WriteAllText(logPath, logSb.ToString()); }
                }
                catch { }

                Dispatcher.BeginInvoke(() =>
                {
                    _gameRunning = false;
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    UpdatePresence("In Launcher", "Frost Client", null);
                    SetStatus(Loc.T("game_closed"));
                    if (code != 0)
                        MessageBox.Show(
                            Loc.T("game_crash_box", code, logPath),
                            "Frost Client", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetStatus(Loc.T("starting", entry.DisplayName));
            await Task.Delay(2000);
            SetStatus(Loc.T("running", entry.DisplayName));

            if (_cfg.OnLaunchIndex == 1) WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), Loc.T("title_launch_error"));
            SetStatus(Loc.T("launch_failed"));
            UpdatePresence("In Launcher", "Frost Client", null);
        }
        finally
        {
            _busy = false;
            if (PlayButton != null) PlayButton.IsEnabled = true;
        }
    }
}