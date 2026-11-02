using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.ProcessBuilder;
using DiscordRPC;
using Microsoft.Win32;

namespace FrostClient;

// ──────────────────────────────────────────────────────────
// DATA MODELS
// ──────────────────────────────────────────────────────────
public sealed class VersionEntry
{
    public string RealId { get; }              // actual folder / launch id
    public string DisplayName { get; set; }   // custom label (or same as RealId)
    public string Letter => DisplayName.Length > 0 ? DisplayName[0].ToString().ToUpperInvariant() : "?";

    public VersionEntry(string realId, string displayName)
    {
        RealId = realId;
        DisplayName = displayName;
    }
}

// Saved launcher account: offline or Microsoft.
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
        // "frost client" versions use the default mods folder.
        // Custom / Vanilla versions get their own sub-folder.
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
    private bool _setupComplete;

    // Cache local de versões em memória (evita depender do ItemsControl do XAML)
    private List<VersionEntry> _versions = new();

    // Saved offline and Microsoft accounts.
    private List<AccountEntry> _accounts = new();

    // shared rename/edit overlay target
    private string? _renameTargetId;
    private string? _pendingConfirmVersionId;
    private string? _pendingConfirmAccountId;

    // ── Discord ────────────────────────────────────────────
    private DiscordRpcClient? _discord;
    private const string AppId        = "1426668060642644209";
    private const string LargeKey     = "icon_6_";
    private const string SmallKey     = "logo";

    // ──────────────────────────────────────────────────────────
    public MainWindow()
    {
        try
        {
            _loginHandler = JELoginHandlerBuilder.BuildDefault();
            InitializeComponent();
            LoadSettings();
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
            _discord.OnReady += (_, e) => SetDiscordStatus($"Discord: {e.User.Username}", true);
            _discord.OnConnectionFailed += (_, _) => SetDiscordStatus("Discord: not running", false);
            _discord.OnError += (_, e) => SetDiscordStatus($"Discord error: {e.Message}", false);
            _discord.Initialize();
            UpdatePresence("In Launcher", "Frost Client", null);
        }
        catch (Exception ex) { SetDiscordStatus($"Discord: {ex.Message}", false); }
    }

    private void SetDiscordStatus(string text, bool ok) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (DiscordStatusText == null) return;
            DiscordStatusText.Text = text;
            DiscordStatusText.Foreground =
                new BrushConverter().ConvertFromString(ok ? "#10B981" : "#F59E0B") as Brush;
        });

    private void UpdatePresence(string details, string state, string? version)
    {
        if (_discord == null || DiscordToggle?.IsChecked == false) return;
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

    private void DiscordToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (DiscordToggle.IsChecked == true) UpdatePresence("In Launcher", "Frost Client", null);
        else _discord?.ClearPresence();
        SaveSilently();
    }

    // ──────────────────────────────────────────────────────────
    // WINDOW LOAD / CLOSE
    // ──────────────────────────────────────────────────────────
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateNavHighlight("Home");
        AnimateSidebar();
        AnimatePage(HomePage);

        RenderAccounts();
        await RestoreActiveAccountAsync();
        UpdateProfileUI();

        await EnsureFrostResourcesAsync();
        await RefreshVersionsAsync();
        SetStatus("Ready.");
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

    // ──────────────────────────────────────────────────────────
    // ANIMATIONS
    // ──────────────────────────────────────────────────────────
    private void AnimateSidebar()
    {
        if (SidebarMenu == null) return;
        var t = new TranslateTransform { X = -30 };
        SidebarMenu.RenderTransform = t;
        SidebarMenu.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
        t.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(500))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static void AnimatePage(UIElement? el)
    {
        if (el == null) return;
        el.Opacity = 0;
        var t = new TranslateTransform { Y = 20 };
        el.RenderTransform = t;
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ──────────────────────────────────────────────────────────
    // PROFILE UI
    // ──────────────────────────────────────────────────────────
    private static bool IsValidName(string? n) =>
        !string.IsNullOrEmpty(n) && n.Length >= 3 && n.Length <= 16
        && n.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private void UpdateProfileUI()
    {
        bool microsoft = _session?.UserType == "msa";
        string name = _session?.Username ?? "Not logged in";

        if (TopPlayerName != null) TopPlayerName.Text = name;
        if (AccountPlayerName != null) AccountPlayerName.Text = name;
        if (AccountStatusDetail != null)
            AccountStatusDetail.Text = _session == null ? "Not logged in"
                : microsoft ? $"Microsoft · {name}" : $"Offline · {name}";

        _ = LoadPlayerProfileAsync(name, microsoft ? _session?.UUID : null);
    }

    // Loads the player's actual Minecraft skin head. Try multiple skin renderers so
    // a temporary outage from one provider does not leave the profile avatar blank.
    private async Task LoadPlayerProfileAsync(string playerName, string? knownUuid)
    {
        if (string.IsNullOrWhiteSpace(playerName) || playerName == "Not logged in") return;

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

        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            urls.Add($"https://mc-heads.net/avatar/{Uri.EscapeDataString(uuid)}/64");
            urls.Add($"https://crafatar.com/avatars/{Uri.EscapeDataString(uuid)}?size=64&overlay=true");
        }
        // Username renderer is also a useful fallback for offline-mode names.
        urls.Add($"https://minotar.net/avatar/{Uri.EscapeDataString(playerName)}/64");
        urls.Add($"https://mc-heads.net/avatar/{Uri.EscapeDataString(playerName)}/64");

        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("FrostClient/1.0");
                using var response = await ProfileHttp.SendAsync(request);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 100) continue;
                await Dispatcher.InvokeAsync(() =>
                {
                    SetAvatarImage(TopPlayerAvatar, bytes);
                    SetAvatarImage(AccountPlayerAvatar, bytes);
                });
                return;
            }
            catch { /* Try the next skin renderer. */ }
        }
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
    // WIZARD
    // ──────────────────────────────────────────────────────────
    private void HideMainUI()
    {
        MainLauncherUI.Visibility    = Visibility.Collapsed;
        MainLauncherUI.IsHitTestVisible = false;
        WizardOverlay.Visibility    = Visibility.Visible;
    }
    private void ShowMainUI()
    {
        WizardOverlay.Visibility       = Visibility.Collapsed;
        MainLauncherUI.Visibility      = Visibility.Visible;
        MainLauncherUI.IsHitTestVisible = true;
    }
    private void ShowWizard()
    {
        HideMainUI();
        WizardScreen_Welcome.Visibility = Visibility.Visible;
    }

    private void Wizard_Step1_Next_Click(object s, RoutedEventArgs e)
    {
        WizardScreen_Welcome.Visibility = Visibility.Collapsed;
        WizardScreen_Java.Visibility    = Visibility.Visible;
    }
    private void Wizard_Step2_Back_Click(object s, RoutedEventArgs e)
    {
        WizardScreen_Java.Visibility    = Visibility.Collapsed;
        WizardScreen_Welcome.Visibility = Visibility.Visible;
    }
    private void Wizard_BrowseJava_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Select javaw.exe", Filter = "Java|javaw.exe|All|*.*", CheckFileExists = true };
        if (d.ShowDialog() == true && WizardJavaPathBox != null) WizardJavaPathBox.Text = d.FileName;
    }
    private void Wizard_Step2_Next_Click(object s, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(WizardJavaPathBox?.Text) && !File.Exists(WizardJavaPathBox.Text))
        {
            MessageBox.Show("The file does not exist. Pick a valid javaw.exe or leave it empty.", "Invalid Java", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (JavaPathBox != null) JavaPathBox.Text = WizardJavaPathBox?.Text?.Trim() ?? "";
        SaveSilently();
        WizardScreen_Java.Visibility  = Visibility.Collapsed;
        WizardScreen_Login.Visibility = Visibility.Visible;
    }
    private void Wizard_Step4_Back_Click(object s, RoutedEventArgs e)
    {
        WizardScreen_Login.Visibility = Visibility.Collapsed;
        WizardScreen_Java.Visibility  = Visibility.Visible;
    }

    // MicrosoftLoginAsync already creates/activates the account, so we only finish the wizard here.
    private async void Wizard_LoginMicrosoft_Click(object s, RoutedEventArgs e)
    {
        await MicrosoftLoginAsync();
        if (_session?.UserType == "msa") FinishWizard();
    }


    private void Wizard_LoginOffline_Click(object s, RoutedEventArgs e)
    {
        var name = UsernameBox?.Text?.Trim();
        if (!IsValidName(name))
        {
            MessageBox.Show("Name must be 3–16 characters (letters, numbers or _).", "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var acc = new AccountEntry { DisplayName = name!, Type = "offline" };
        _accounts.Add(acc);
        _cfg.Accounts = _accounts;
        _cfg.ActiveAccountId = acc.Id;
        _session = MSession.CreateOfflineSession(name!);
        RenderAccounts();
        UpdateProfileUI();
        FinishWizard();
    }

    private void FinishWizard()
    {
        _setupComplete = true;
        WizardScreen_Welcome.Visibility = Visibility.Collapsed;
        WizardScreen_Java.Visibility    = Visibility.Collapsed;
        WizardScreen_Login.Visibility   = Visibility.Collapsed;
        ShowMainUI();
        UpdateProfileUI();
        SaveSilently();
    }

    // ──────────────────────────────────────────────────────────
    // SETTINGS
    // ──────────────────────────────────────────────────────────
    private sealed class AppSettings
    {
        public int    RamGb           { get; set; } = 4;
        public string JavaArguments   { get; set; } = "";
        public bool   SetupComplete   { get; set; } = false;
        public string JavaPath        { get; set; } = "";
        public int    ResolutionIndex { get; set; } = 0;
        public int    OnLaunchIndex   { get; set; } = 0;
        public bool   DiscordPresence { get; set; } = true;
        // version display names: { realId -> displayName }
        public Dictionary<string, string> VersionNames { get; set; } = new();

        // Saved accounts and which one is currently active.
        public List<AccountEntry> Accounts        { get; set; } = new();
        public string             ActiveAccountId  { get; set; } = "";

        // Legacy fields kept only so old settings.json files can be migrated once.
        public string Username            { get; set; } = "";
        public bool   IsMicrosoftLoggedIn { get; set; } = false;
    }

    private AppSettings _cfg = new();

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile)) { ShowWizard(); return; }
            _cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile)) ?? new();

            RamSlider.Value         = Math.Clamp(_cfg.RamGb, 1, 16);
            JavaArgumentsBox.Text   = _cfg.JavaArguments;
            JavaPathBox.Text        = _cfg.JavaPath;
            WizardJavaPathBox.Text  = _cfg.JavaPath;
            ResolutionBox.SelectedIndex = Math.Clamp(_cfg.ResolutionIndex, 0, ResolutionBox.Items.Count - 1);
            OnLaunchBox.SelectedIndex   = Math.Clamp(_cfg.OnLaunchIndex,   0, OnLaunchBox.Items.Count - 1);
            DiscordToggle.IsChecked  = _cfg.DiscordPresence;

            _accounts = _cfg.Accounts ?? new();
            foreach (var account in _accounts)
            {
                if (account.Type != "microsoft")
                    account.Type = "offline";
            }

            // Migrate from the old single-account settings format, once.
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

            if (UsernameBox != null) UsernameBox.Text = "FrostPlayer";
            if (NewAccountNameBox != null) NewAccountNameBox.Text = "";

            _setupComplete = _cfg.SetupComplete;
            if (_setupComplete) { WizardOverlay.Visibility = Visibility.Collapsed; ShowMainUI(); }
            else ShowWizard();
        }
        catch { ShowWizard(); }
    }

    private void SaveSilently()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            _cfg.RamGb           = (int)(RamSlider?.Value ?? 4);
            _cfg.JavaArguments   = JavaArgumentsBox?.Text?.Trim() ?? "";
            _cfg.SetupComplete   = _setupComplete;
            _cfg.JavaPath        = JavaPathBox?.Text?.Trim() ?? WizardJavaPathBox?.Text?.Trim() ?? "";
            _cfg.ResolutionIndex = ResolutionBox?.SelectedIndex ?? 0;
            _cfg.OnLaunchIndex   = OnLaunchBox?.SelectedIndex   ?? 0;
            _cfg.DiscordPresence = DiscordToggle?.IsChecked != false;
            _cfg.Accounts        = _accounts;
            File.WriteAllText(_settingsFile,
                JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Debug.WriteLine("Save error: " + ex.Message); }
    }

    private void SaveSettings_Click(object s, RoutedEventArgs e)
    {
        SaveSilently();
        SetStatus("Settings saved.");
        MessageBox.Show("Settings saved!", "Frost Client");
    }
    private void BrowseJava_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Select javaw.exe", Filter = "Java|javaw.exe|All|*.*", CheckFileExists = true };
        if (d.ShowDialog() == true && JavaPathBox != null) { JavaPathBox.Text = d.FileName; SaveSilently(); }
    }
    private void Settings_TextChanged(object s, TextChangedEventArgs e)   { if (IsLoaded) SaveSilently(); }
    private void Settings_ComboChanged(object s, SelectionChangedEventArgs e) { if (IsLoaded) SaveSilently(); }
    private void RamSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RamLabel != null) RamLabel.Text = $"{(int)e.NewValue} GB";
        if (IsLoaded) SaveSilently();
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

    // ──────────────────────────────────────────────────────────
    // ACCOUNTS (offline + Microsoft)
    // ──────────────────────────────────────────────────────────
    private AccountEntry? GetActiveAccount() =>
        _accounts.FirstOrDefault(a => a.Id == _cfg.ActiveAccountId) ?? _accounts.FirstOrDefault();

    // Rebuilds the home-page rail and the Account-page list, and flags the active entry.
    private void RenderAccounts()
    {
        foreach (var a in _accounts) a.IsActive = a.Id == _cfg.ActiveAccountId;

        if (AccountsStripList != null) { AccountsStripList.ItemsSource = null; AccountsStripList.ItemsSource = _accounts; }
        if (AccountsListBox   != null) { AccountsListBox.ItemsSource   = null; AccountsListBox.ItemsSource   = _accounts; }
    }


    // Restores the currently selected offline or Microsoft account.
    private async Task RestoreActiveAccountAsync()
    {
        var acc = GetActiveAccount();
        if (acc == null) { _session = null; return; }

        if (acc.Type == "microsoft")
        {
            try
            {
                SetStatus("Restoring Microsoft session…");
                _session = await _loginHandler.AuthenticateSilently();
            }
            catch
            {
                _session = null;
                SetStatus("Microsoft session expired. Please log in again.");
            }
        }
        else
        {
            _session = MSession.CreateOfflineSession(acc.DisplayName);
        }
    }

    private void OpenAddAccountPage_Click(object s, RoutedEventArgs e)
    {
        ShowPage("Account");
        NewAccountNameBox?.Focus();
    }

    private void AddOfflineAccountConfirm_Click(object s, RoutedEventArgs e)
    {
        var name = NewAccountNameBox?.Text?.Trim() ?? "";
        if (!IsValidName(name))
        {
            MessageBox.Show("Name must be 3–16 characters (letters, numbers or _).", "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        SetStatus($"Offline account '{name}' added.");
        if (NewAccountNameBox != null) NewAccountNameBox.Text = "";
    }

    private async void AddMicrosoftAccountConfirm_Click(object s, RoutedEventArgs e) => await MicrosoftLoginAsync();

    private async Task MicrosoftLoginAsync()
    {
        if (MicrosoftLoginButton != null) MicrosoftLoginButton.IsEnabled = false;
        try
        {
            SetStatus("Opening Microsoft login…");
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
            SetStatus($"Logged in as {session.Username}.");
        }
        catch (Exception ex)
        {
            SetStatus("Login failed.");
            MessageBox.Show(ex.Message, "Login Error");
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
        SetStatus($"Active account: {acc.DisplayName}.");
    }

    private void DeleteAccount_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string id }) return;
        var acc = _accounts.FirstOrDefault(a => a.Id == id);
        if (acc == null) return;

        _pendingConfirmAccountId = id;
        _pendingConfirmVersionId = null;
        ConfirmTitle.Text = "Delete account?";
        ConfirmMessage.Text = $"Remove '{acc.DisplayName}' from this launcher? This does not delete the online account itself.";
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
    // NAVIGATION
    // ──────────────────────────────────────────────────────────
    private void SetStatus(string t) { if (StatusText != null) StatusText.Text = t; }

    private void UpdateNavHighlight(string page)
    {
        var map = new[] { (NavHome, "Home"), (NavVersions, "Versions"), (NavSettings, "Settings"), (NavAccount, "Account") };
        var normal = (Style)FindResource("NavBtn");
        var active = (Style)FindResource("NavBtnActive");
        foreach (var (btn, name) in map)
            if (btn != null) btn.Style = name == page ? active : normal;
    }

    private void ShowPage(string page)
    {
        var pages = new (string n, UIElement? el)[]
        {
            ("Home", HomePage), ("Versions", VersionsPage), ("Settings", SettingsPage), ("Account", AccountPage)
        };
        if (pages.Any(p => p.el == null)) return;
        foreach (var (n, el) in pages)
            el!.Visibility = n == page ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavHighlight(page);
        var cur = pages.First(p => p.n == page).el!;
        AnimatePage(cur);
    }

    private void HomeNav_Click    (object s, RoutedEventArgs e) => ShowPage("Home");
    private void VersionsNav_Click(object s, RoutedEventArgs e) => ShowPage("Versions");
    private void SettingsNav_Click(object s, RoutedEventArgs e) => ShowPage("Settings");
    private void AccountNav_Click (object s, RoutedEventArgs e) => ShowPage("Account");

    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click   (object s, RoutedEventArgs e)
    {
        _discord?.ClearPresence(); _discord?.Dispose();
        Application.Current.Shutdown();
    }

    private void CB_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox c && c.IsDropDownOpen) return;
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Parent is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = sender
            });
        }
    }

    private void OpenGameFolder_Click (object s, RoutedEventArgs e) => OpenFolder(_gameDir);
    private void OpenModsFolder_Click (object s, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        OpenFolder(GameModsDir(entry?.RealId));
    }

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
    private void YouTube_Click(object s, RoutedEventArgs e) => OpenUrl("https://www.youtube.com/@FrostClient-q4i");
    private void GitHub_Click (object s, RoutedEventArgs e) => OpenUrl("https://github.com/joafine-sudo/frostapp");

    // ──────────────────────────────────────────────────────────
    // VERSIONS
    // ──────────────────────────────────────────────────────────
    private string GetDisplayName(string realId)
        => _cfg.VersionNames.TryGetValue(realId, out var n) ? n : realId;

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

            // ComboBox mostra os nomes de exibição
            if (VersionBox != null)
            {
                VersionBox.ItemsSource   = _versions.Select(e => e.DisplayName).ToList();
                VersionBox.SelectedIndex = 0;
            }
            if (VersionList != null) VersionList.ItemsSource = _versions;

            var targetEntry = (selectId != null ? _versions.FirstOrDefault(e => e.RealId == selectId) : null)
                ?? _versions.FirstOrDefault(e => e.RealId.Contains("frost client", StringComparison.OrdinalIgnoreCase))
                ?? _versions.FirstOrDefault();

            if (targetEntry != null)
            {
                if (VersionBox != null) VersionBox.SelectedItem = targetEntry.DisplayName;
                UpdateHomeVersion(targetEntry);
            }
            else
            {
                if (PlayButton != null) PlayButton.Content = "NO VERSION";
                if (HomeSubText != null) HomeSubText.Text = "Install a version to get started.";
                if (HomeVersionLabel != null) HomeVersionLabel.Text = "";
            }
        }
        catch (Exception ex) { Debug.WriteLine("RefreshVersions: " + ex.Message); }
    }

    // Consulta direta à lista privada _versions em memória
    private VersionEntry? GetSelectedEntry()
    {
        var display = VersionBox?.SelectedItem as string;
        if (string.IsNullOrEmpty(display)) return null;
        return _versions.FirstOrDefault(e => e.DisplayName == display);
    }

    private void UpdateHomeVersion(VersionEntry e)
    {
        if (PlayButton       != null) PlayButton.Content       = "PLAY";
        if (HomeVersionLabel != null) HomeVersionLabel.Text     = $"{e.RealId}";
        if (HomeSubText      != null) HomeSubText.Text         = $"{e.DisplayName}  ·  Ready to launch.";
    }

    private void Version_Changed(object s, SelectionChangedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry == null) return;
        UpdateHomeVersion(entry);
        if (!_busy && !_gameRunning) UpdatePresence("In Launcher", $"Selected {entry.DisplayName}", null);
    }

    // ──────────────────────────────────────────────────────────
    // FROST RESOURCES (download individual files; libraries stay outside the EXE)
    // The release only needs the small version JSON and the Frost client JAR.
    // Libraries are downloaded separately from the URLs declared in the version JSON.
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
                ResourcesStatusText.Text = "Download failed: " + ex.Message +
                    "\n\nCheck your internet connection and try again.";
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

    private async Task DownloadAndInstallResourcesAsync()
    {
        ResourcesOverlay.Visibility = Visibility.Visible;
        SetResourcesProgress(0, "Installing Frost Client version files…");

        // The version is shipped beside the launcher in:
        // default_version/frost client/frost client.{json,jar}
        // It is copied into the Minecraft game directory; no JSON/JAR release download is needed.
        var bundledVersionDir = Path.Combine(AppContext.BaseDirectory, "default_version", "frost client");
        var bundledJson = Path.Combine(bundledVersionDir, "frost client.json");
        var bundledJar = Path.Combine(bundledVersionDir, "frost client.jar");

        if (!File.Exists(bundledJson) || !File.Exists(bundledJar))
            throw new FileNotFoundException(
                "Frost Client version files were not found. Expected default_version\\frost client\\frost client.json and frost client.jar beside the launcher.");

        Directory.CreateDirectory(FrostVersionDir);
        File.Copy(bundledJson, FrostVersionJson, overwrite: true);
        File.Copy(bundledJar, FrostVersionJar, overwrite: true);
        SetResourcesProgress(20, "Reading Frost Client dependencies…");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(FrostVersionJson));
        if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Frost Client JSON has no valid 'libraries' list.");

        var jobs = new List<(string Url, string Destination)>();
        foreach (var library in libraries.EnumerateArray())
        {
            // Skip OS-specific native classifiers; CmlLib/Minecraft resolves native libraries.
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
        SetResourcesProgress(25, $"Downloading {totalJobs} missing libraries…");
        // A small concurrency limit avoids opening hundreds of connections at once.
        using var semaphore = new System.Threading.SemaphoreSlim(4);
        var tasks = jobs.Select(async job =>
        {
            await semaphore.WaitAsync();
            try
            {
                await DownloadFileAsync(job.Url, job.Destination);
                int done = System.Threading.Interlocked.Increment(ref completed);
                SetResourcesProgress(25 + (totalJobs == 0 ? 75 : done * 75d / totalJobs),
                    $"Downloading libraries… {done}/{totalJobs}");
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);

        // Forge's main JAR is required by this custom 1.8.9 profile.
        if (!File.Exists(ForgeJarPath))
            throw new FileNotFoundException("Forge 1.8.9 library is missing after dependency download.", ForgeJarPath);

        File.WriteAllText(ResourcesMarker, ResourcesVersion);
        SetResourcesProgress(100, "Frost Client installed!");
        await Task.Delay(400);
        ResourcesOverlay.Visibility = Visibility.Collapsed;
    }

    // ── Install / Delete / Rename ──────────────────────────────
    private async void InstallVersion_Click(object s, RoutedEventArgs e)
    {
        var installBtn = (s as Button) ?? InstallButton;

        if (InstallVersionBox?.SelectedItem is not string mc)
        { MessageBox.Show("Select a Minecraft version first."); return; }

        if (installBtn != null) installBtn.IsEnabled = false;
        if (InstallProgress != null) InstallProgress.Visibility = Visibility.Visible;
        if (InstallStatusText != null) InstallStatusText.Text = $"Downloading Minecraft {mc}…";
        SetStatus($"Installing {mc}…");

        try
        {
            var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));
            await launcher.InstallAsync(mc);
            if (InstallStatusText != null) InstallStatusText.Text = $"{mc} installed successfully!";
            SetStatus($"{mc} installed.");
            await RefreshVersionsAsync(mc);
        }
        catch (Exception ex)
        {
            if (InstallStatusText != null) InstallStatusText.Text = "Installation error.";
            SetStatus("Failed.");
            MessageBox.Show(ex.ToString(), "Install Error");
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
        var entry = _versions.FirstOrDefault(e => e.RealId == realId);
        if (entry == null) return;
        if (VersionBox != null) VersionBox.SelectedItem = entry.DisplayName;
        SetStatus($"Selected {entry.DisplayName}.");
    }

    private void DeleteVersion_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string realId }) return;
        _pendingConfirmVersionId = realId;
        _pendingConfirmAccountId = null;
        ConfirmTitle.Text = "Delete this version?";
        ConfirmMessage.Text = $"Delete '{GetDisplayName(realId)}'? Version files will be removed. Your worlds and mods will be kept.";
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private async Task DeleteVersionConfirmedAsync(string realId)
    {
        try
        {
            var dir = Path.Combine(_gameDir, "versions", realId);
            await Task.Run(() => { if (Directory.Exists(dir)) Directory.Delete(dir, true); });
            // Remove custom name entry if any
            _cfg.VersionNames.Remove(realId);
            SaveSilently();
            SetStatus($"Deleted {realId}.");
            await RefreshVersionsAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Delete Error"); }
    }

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

    // ── RENAME VERSION OVERLAY ─────────────────────────────────
    private void OpenRenameOverlay(string realId)
    {
        _renameTargetId = realId;
        RenameTitle.Text   = "Rename Version";
        RenameLabel.Text   = "DISPLAY NAME";
        RenameSubText.Text = $"Real id: {realId}";
        RenameBox.Text     = GetDisplayName(realId);
        RenameOverlay.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }


    // Pencil on the home page ComboBox row
    private void RenameVersion_Click(object s, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry == null) return;
        OpenRenameOverlay(entry.RealId);
    }

    // Pencil inside the Versions list
    private void RenameVersionFromList_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string realId }) return;
        OpenRenameOverlay(realId);
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
        var newVal = RenameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(newVal)) newVal = _renameTargetId;
        if (newVal == _renameTargetId)
            _cfg.VersionNames.Remove(_renameTargetId);
        else
            _cfg.VersionNames[_renameTargetId] = newVal;

        SaveSilently();
        _ = RefreshVersionsAsync();

        RenameOverlay.Visibility = Visibility.Collapsed;
        _renameTargetId = null;
    }

    // ──────────────────────────────────────────────────────────
    // LAUNCH GAME
    // ──────────────────────────────────────────────────────────
    private async void Play_Click(object s, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_session == null)
        {
            var activeAcc = GetActiveAccount();
            if (activeAcc == null)
            {
                MessageBox.Show("No account selected. Add one in the Account tab.", "Frost Client");
                return;
            }
            await RestoreActiveAccountAsync();
            UpdateProfileUI();
            if (_session == null)
            {
                MessageBox.Show("Could not restore the selected account. Try logging in again.", "Frost Client");
                return;
            }
        }

        var entry = GetSelectedEntry();
        if (entry == null) { MessageBox.Show("No version selected. Go to Versions."); return; }
        var realId = entry.RealId;

        _busy = true;
        if (PlayButton != null) PlayButton.IsEnabled = false;
        SetStatus($"Preparing {entry.DisplayName}…");
        UpdatePresence($"Playing {entry.DisplayName}", realId, realId);

        try
        {
            Directory.CreateDirectory(_gameDir);
            // ensure per-version mods folder exists
            Directory.CreateDirectory(GameModsDir(realId));

            // Garante que a versão Frost e as libraries (Forge, etc.) estão instaladas antes de lançar.
            if (realId.Contains("frost client", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Checking Frost Client resources…");
                var ok = await EnsureFrostResourcesAsync();
                if (!ok || !File.Exists(ForgeJarPath))
                {
                    MessageBox.Show(
                        $"Frost Client resources are not installed.\n\nStatus: {_frostSetupInfo}\n\nCheck your internet connection and press PLAY again.",
                        "Frost Client", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetStatus("Missing resources.");
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

            // Java path
            var javaPath = JavaPathBox?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(javaPath) && File.Exists(javaPath))
                options.JavaPath = javaPath;

            // JVM args
            var jvmArgs = JavaArgumentsBox?.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(jvmArgs))
                options.ExtraJvmArguments = new[] { MArgument.FromCommandLine(jvmArgs) };

            // Resolution
            if (ResolutionBox?.SelectedItem is ComboBoxItem { Content: string res } && res.Contains('×'))
            {
                var p = res.Split('×');
                if (p.Length == 2
                    && int.TryParse(p[0].Trim(), out var w)
                    && int.TryParse(p[1].Trim(), out var h))
                {
                    options.ScreenWidth  = w;
                    options.ScreenHeight = h;
                }
            }

            SetStatus("Verifying files…");  await Task.Delay(300);
            SetStatus("Checking runtime…");
            var process = await launcher.InstallAndBuildProcessAsync(realId, options);

            process.EnableRaisingEvents = true;
            _gameRunning = true;

            // Captura a saída do jogo para um log (para diagnosticar crashes no PC de outras pessoas).
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
                    SetStatus("Game closed.");
                    if (code != 0)
                        MessageBox.Show(
                            $"The game closed with an error (code {code}).\n\nA log was saved at:\n{logPath}\n\nSend this file to the developer.",
                            "Frost Client", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetStatus($"{entry.DisplayName} is starting…");
            await Task.Delay(2000);
            SetStatus($"{entry.DisplayName} is running.");

            if (OnLaunchBox?.SelectedIndex == 1) WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Launch Error");
            SetStatus("Failed to launch.");
            UpdatePresence("In Launcher", "Frost Client", null);
        }
        finally
        {
            _busy = false;
            if (PlayButton != null) PlayButton.IsEnabled = true;
        }
    }
}