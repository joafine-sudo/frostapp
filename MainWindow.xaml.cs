using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
// DATA MODEL
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

// ──────────────────────────────────────────────────────────
// MAIN WINDOW
// ──────────────────────────────────────────────────────────
public partial class MainWindow : Window
{
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
    private bool _wasMicrosoftLoggedIn;

    // Cache local de versões em memória (evita depender do ItemsControl do XAML)
    private List<VersionEntry> _versions = new();

    // version rename
    private string? _renameTargetId;

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

        UpdateProfileUI();
        await TryRestoreMsSessionAsync();
        UpdateProfileUI();

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
        bool ms = _session?.UserType == "msa";
        string name = _session?.Username ?? "Not logged in";

        if (TopPlayerName != null)     TopPlayerName.Text     = name;
        if (AccountPlayerName != null) AccountPlayerName.Text = name;
        if (AccountStatusDetail != null)
            AccountStatusDetail.Text = _session == null ? "Not logged in"
                                       : ms ? $"Microsoft · {name}" : $"Offline · {name}";

        string id = ms && !string.IsNullOrEmpty(_session!.UUID) ? _session.UUID
                    : (_session?.Username ?? "Steve");
        LoadAvatar(id);
    }

    private void LoadAvatar(string id, bool fallback = false)
    {
        try
        {
            var url = $"https://mc-heads.net/avatar/{Uri.EscapeDataString(id)}/100";
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            if (bmp.IsDownloading && !fallback)
                bmp.DownloadFailed += (_, _) => Dispatcher.Invoke(() => LoadAvatar("Steve", true));

            if (TopAvatar != null)     TopAvatar.Source     = bmp;
            if (AccountAvatar != null) AccountAvatar.Source = bmp;

            // Full body render for the home page
            var body = new BitmapImage();
            body.BeginInit();
            body.UriSource   = new Uri($"https://mc-heads.net/body/{Uri.EscapeDataString(id)}/260");
            body.CacheOption = BitmapCacheOption.OnLoad;
            body.EndInit();
            if (HomeBody != null) HomeBody.Source = body;
        }
        catch { if (!fallback) LoadAvatar("Steve", true); }
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
        _session = MSession.CreateOfflineSession(name!);
        if (AccountOfflineNameBox != null) AccountOfflineNameBox.Text = name!;
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
        public int    RamGb              { get; set; } = 4;
        public string JavaArguments      { get; set; } = "";
        public string Username           { get; set; } = "FrostPlayer";
        public bool   SetupComplete      { get; set; } = false;
        public string JavaPath           { get; set; } = "";
        public bool   IsMicrosoftLoggedIn{ get; set; } = false;
        public int    ResolutionIndex    { get; set; } = 0;
        public int    OnLaunchIndex      { get; set; } = 0;
        public bool   DiscordPresence    { get; set; } = true;
        // version display names: { realId -> displayName }
        public Dictionary<string, string> VersionNames { get; set; } = new();
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
            UsernameBox.Text        = string.IsNullOrWhiteSpace(_cfg.Username) ? "FrostPlayer" : _cfg.Username;
            AccountOfflineNameBox.Text = UsernameBox.Text;
            ResolutionBox.SelectedIndex = Math.Clamp(_cfg.ResolutionIndex, 0, ResolutionBox.Items.Count - 1);
            OnLaunchBox.SelectedIndex   = Math.Clamp(_cfg.OnLaunchIndex,   0, OnLaunchBox.Items.Count - 1);
            DiscordToggle.IsChecked  = _cfg.DiscordPresence;

            _wasMicrosoftLoggedIn = _cfg.IsMicrosoftLoggedIn;
            if (!_wasMicrosoftLoggedIn && IsValidName(UsernameBox.Text) && _cfg.SetupComplete)
                _session = MSession.CreateOfflineSession(UsernameBox.Text);

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
            _cfg.RamGb              = (int)(RamSlider?.Value ?? 4);
            _cfg.JavaArguments      = JavaArgumentsBox?.Text?.Trim() ?? "";
            _cfg.Username           = AccountOfflineNameBox?.Text?.Trim()
                                      ?? UsernameBox?.Text?.Trim() ?? "FrostPlayer";
            _cfg.SetupComplete      = _setupComplete;
            _cfg.JavaPath           = JavaPathBox?.Text?.Trim() ?? WizardJavaPathBox?.Text?.Trim() ?? "";
            _cfg.IsMicrosoftLoggedIn = _session?.UserType == "msa";
            _cfg.ResolutionIndex    = ResolutionBox?.SelectedIndex ?? 0;
            _cfg.OnLaunchIndex      = OnLaunchBox?.SelectedIndex   ?? 0;
            _cfg.DiscordPresence    = DiscordToggle?.IsChecked != false;
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
    // ACCOUNT
    // ──────────────────────────────────────────────────────────
    private async void MicrosoftLogin_Click(object s, RoutedEventArgs e) => await MicrosoftLoginAsync();

    private async Task MicrosoftLoginAsync()
    {
        if (MicrosoftLoginButton != null) MicrosoftLoginButton.IsEnabled = false;
        try
        {
            SetStatus("Opening Microsoft login…");
            _session = await _loginHandler.AuthenticateInteractively();
            _wasMicrosoftLoggedIn = true;
            UpdateProfileUI();
            SaveSilently();
            SetStatus($"Logged in as {_session.Username}.");
        }
        catch (Exception ex)
        {
            _session = null;
            UpdateProfileUI();
            SetStatus("Login failed.");
            MessageBox.Show(ex.Message, "Login Error");
        }
        finally { if (MicrosoftLoginButton != null) MicrosoftLoginButton.IsEnabled = true; }
    }

    private async Task TryRestoreMsSessionAsync()
    {
        if (!_wasMicrosoftLoggedIn) return;
        try
        {
            SetStatus("Restoring Microsoft session…");
            _session = await _loginHandler.AuthenticateSilently();
        }
        catch { _session = null; SetStatus("Microsoft session expired. Please log in again."); }
    }

    private void SaveOfflineName_Click(object s, RoutedEventArgs e)
    {
        var name = AccountOfflineNameBox?.Text?.Trim() ?? "";
        if (!IsValidName(name)) { MessageBox.Show("Name must be 3–16 chars (letters, numbers, _).", "Invalid Name"); return; }
        if (UsernameBox != null) UsernameBox.Text = name;
        _session = MSession.CreateOfflineSession(name);
        _wasMicrosoftLoggedIn = false;
        UpdateProfileUI();
        SaveSilently();
        MessageBox.Show("Offline name saved!", "Frost Client");
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

    private async void DeleteVersion_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string realId }) return;
        var r = MessageBox.Show($"Delete '{GetDisplayName(realId)}'?\n\nThis removes the version files only. Your worlds and mods are kept.",
                                "Frost Client", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
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

    // ── RENAME (shared for pencil button on home + list) ───────
    private void OpenRenameOverlay(string realId)
    {
        _renameTargetId      = realId;
        RenameSubText.Text   = $"Real id: {realId}";
        RenameBox.Text       = GetDisplayName(realId);
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
        var newName = RenameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName)) newName = _renameTargetId;

        if (newName == _renameTargetId)
            _cfg.VersionNames.Remove(_renameTargetId);   // volta ao original
        else
            _cfg.VersionNames[_renameTargetId] = newName;

        SaveSilently();
        RenameOverlay.Visibility = Visibility.Collapsed;
        _renameTargetId = null;
        _ = RefreshVersionsAsync();
    }

    // ──────────────────────────────────────────────────────────
    // LAUNCH GAME
    // ──────────────────────────────────────────────────────────
    private async void Play_Click(object s, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_session == null)
        {
            var name = AccountOfflineNameBox?.Text?.Trim() ?? "";
            if (!IsValidName(name))
            {
                MessageBox.Show("No account selected. Log in with Microsoft or set a valid offline name in Account.", "Frost Client");
                return;
            }
            _session = MSession.CreateOfflineSession(name);
            UpdateProfileUI();
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
            var jvmArgs = JavaArgumentsBox?.Text?.Trim();
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
            process.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _gameRunning = false;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                UpdatePresence("In Launcher", "Frost Client", null);
                SetStatus("Game closed.");
            });
            process.Start();
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