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
using System.Windows.Media.Animation;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.ProcessBuilder;
using DiscordRPC;
using Microsoft.Win32;

namespace FrostClient;

public partial class MainWindow : Window
{
    private readonly string _gameDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    private readonly string _gameModsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "mods");
    private readonly string _defaultVersionDir = Path.Combine(AppContext.BaseDirectory, "default_version");

    private readonly JELoginHandler _loginHandler;
    private MSession? _session;
    private bool _busy;
    private readonly string _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrostClient", "settings.json");

    private List<string> _allRemoteVersions = new List<string>();

    // Discord Rich Presence
    private DiscordRpcClient? _discordClient;
    private const string DiscordAppId = "1426668060642644209";
    private const string LargeImageKey = "icon_6_";
    private const string SmallImageKey = "logo";

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            _loginHandler = JELoginHandlerBuilder.BuildDefault();
            LoadSettings();
            InitializeDiscord();
        }
        catch (Exception ex)
        {
            MessageBox.Show("UI Error (XAML):\n\n" + ex.Message + "\n\nStacktrace:\n" + ex.StackTrace, "Frost Client - UI Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }

    // ==========================================
    // DISCORD RICH PRESENCE
    // ==========================================
    private void InitializeDiscord()
    {
        try
        {
            _discordClient = new DiscordRpcClient(DiscordAppId);
            _discordClient.OnReady += (sender, e) => Debug.WriteLine($"Discord connected as {e.User.Username}");
            _discordClient.Initialize();
            UpdateDiscordPresence("In Launcher", "Frost Client", null);
        }
        catch (Exception ex) { Debug.WriteLine("Discord RPC error: " + ex.Message); }
    }

    private void UpdateDiscordPresence(string details, string state, string? versionName)
    {
        if (_discordClient == null || !_discordClient.IsInitialized) return;

        string? smallKey = versionName != null ? SmallImageKey : null;
        string finalState = versionName ?? state;

        _discordClient.SetPresence(new RichPresence()
        {
            Details = details,
            State = finalState,
            Assets = new Assets()
            {
                LargeImageKey = LargeImageKey,
                LargeImageText = "Frost Client",
                SmallImageKey = smallKey,
                SmallImageText = smallKey != null ? "Frost Client" : null
            },
            Timestamps = Timestamps.Now
        });
    }

    private void ClearDiscordPresence()
    {
        _discordClient?.ClearPresence();
    }

    // ==========================================
    // WINDOW LOAD
    // ==========================================
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SidebarMenu != null) { SidebarMenu.Opacity = 0; }
            AnimateMenu();
            AnimateHome();

            RefreshMods();
            await RefreshVersionsAsync();
            SetStatus("Ready.");

            try { await LoadRemoteVersionsAsync(); } catch { }
        }
        catch { }
    }

    private void AnimateMenu()
    {
        if (SidebarMenu == null) return;
        var transform = new System.Windows.Media.TranslateTransform { X = -50 };
        SidebarMenu.RenderTransform = transform;
        SidebarMenu.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(-50, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void AnimateHome()
    {
        if (HomePage == null) return;
        HomePage.Opacity = 0;
        var transform = new System.Windows.Media.TranslateTransform { Y = 30 };
        HomePage.RenderTransform = transform;
        HomePage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromMilliseconds(200) });
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromMilliseconds(200), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ==========================================
    // UI VISIBILITY & WIZARD LOGIC
    // ==========================================
    
    private void HideMainUI() 
    { 
        if (MainLauncherUI != null) 
        { 
            MainLauncherUI.Visibility = Visibility.Collapsed; 
            MainLauncherUI.IsHitTestVisible = false;
        } 
    }

    private void ShowMainUI() 
    { 
        if (MainLauncherUI != null) 
        { 
            MainLauncherUI.Visibility = Visibility.Visible; 
            MainLauncherUI.IsHitTestVisible = true; 
        } 
    }

    private void Wizard_Step1_Next_Click(object sender, RoutedEventArgs e)
    {
        if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Collapsed;
        if (WizardScreen_Java != null) WizardScreen_Java.Visibility = Visibility.Visible;
    }

    private void Wizard_Step2_Back_Click(object sender, RoutedEventArgs e)
    {
        if (WizardScreen_Java != null) WizardScreen_Java.Visibility = Visibility.Collapsed;
        if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Visible;
    }

    private void Wizard_BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select Java 8 (javaw.exe)", Filter = "Java executable|javaw.exe|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() == true && WizardJavaPathBox != null) WizardJavaPathBox.Text = dialog.FileName;
    }

    private void Wizard_Step2_Next_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(WizardJavaPathBox?.Text) && !File.Exists(WizardJavaPathBox.Text))
        {
            MessageBox.Show("The Java file you selected does not exist. Please pick a valid javaw.exe or leave it empty for auto-install.", "Invalid Java", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        if (JavaPathBox != null && !string.IsNullOrWhiteSpace(WizardJavaPathBox?.Text)) 
            JavaPathBox.Text = WizardJavaPathBox.Text;
        
        SaveSettingsSilently();

        if (WizardScreen_Java != null) WizardScreen_Java.Visibility = Visibility.Collapsed;
        if (WizardScreen_Login != null) WizardScreen_Login.Visibility = Visibility.Visible;
    }

    private void Wizard_Step4_Back_Click(object sender, RoutedEventArgs e)
    {
        if (WizardScreen_Login != null) WizardScreen_Login.Visibility = Visibility.Collapsed;
        if (WizardScreen_Java != null) WizardScreen_Java.Visibility = Visibility.Visible;
    }

    private async void Wizard_LoginMicrosoft_Click(object sender, RoutedEventArgs e) 
    { 
        await MicrosoftLoginAsync(); 
        if (_session != null && _session.UserType == "msa") FinishWizard(); 
    }

    private void Wizard_LoginOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = UsernameBox?.Text?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length < 3) name = "FrostPlayer";
        
        _session = MSession.CreateOfflineSession(name);
        if (AccountStatusDetail != null) AccountStatusDetail.Text = $"Offline: {name}";
        FinishWizard();
    }

    private void FinishWizard()
    {
        if (WizardScreen_Login != null) WizardScreen_Login.Visibility = Visibility.Collapsed;
        if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Collapsed;
        if (WizardScreen_Java != null) WizardScreen_Java.Visibility = Visibility.Collapsed;
        
        ShowMainUI();
        SaveSettingsSilently();
    }

    // ==========================================
    // SETTINGS MANAGEMENT (COM SESSÃO)
    // ==========================================
    private sealed class LauncherSettings
    {
        public int RamGb { get; set; } = 4;
        public string JavaArguments { get; set; } = "";
        public string Username { get; set; } = "FrostPlayer";
        public int LoginMode { get; set; } = 0;
        public bool SetupComplete { get; set; } = false;
        public string JavaPath { get; set; } = "";

        // CAMPOS PARA GUARDAR A SESSÃO
        public string SessionUsername { get; set; } = "";
        public string SessionUuid { get; set; } = "";
        public string SessionAccessToken { get; set; } = "";
        public string SessionUserType { get; set; } = "";
        public bool IsMicrosoftLoggedIn { get; set; } = false;
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile))
            {
                HideMainUI(); if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Visible; return;
            }

            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_settingsFile));
            if (settings == null) { HideMainUI(); if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Visible; return; }

            if (RamSlider != null) RamSlider.Value = Math.Clamp(settings.RamGb, 1, 16);
            if (JavaArgumentsBox != null) JavaArgumentsBox.Text = settings.JavaArguments ?? "";
            if (UsernameBox != null) UsernameBox.Text = string.IsNullOrWhiteSpace(settings.Username) ? "FrostPlayer" : settings.Username;
            if (AccountOfflineNameBox != null) AccountOfflineNameBox.Text = UsernameBox?.Text ?? "FrostPlayer";
            
            if (JavaPathBox != null) JavaPathBox.Text = settings.JavaPath ?? "";
            if (WizardJavaPathBox != null) WizardJavaPathBox.Text = settings.JavaPath ?? "";

            // TENTA RESTAURAR A SESSÃO
            if (settings.IsMicrosoftLoggedIn && !string.IsNullOrEmpty(settings.SessionAccessToken))
            {
                _session = new MSession
                {
                    Username = settings.SessionUsername,
                    UUID = settings.SessionUuid,
                    AccessToken = settings.SessionAccessToken,
                    UserType = settings.SessionUserType
                };
                if (AccountStatusDetail != null) AccountStatusDetail.Text = $"Logged in: {_session.Username}";
            }
            else if (!string.IsNullOrEmpty(settings.Username) && settings.Username != "FrostPlayer")
            {
                _session = MSession.CreateOfflineSession(settings.Username);
                if (AccountStatusDetail != null) AccountStatusDetail.Text = $"Offline: {_session.Username}";
            }

            if (settings.SetupComplete)
            {
                if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Collapsed;
                if (WizardScreen_Java != null) WizardScreen_Java.Visibility = Visibility.Collapsed;
                if (WizardScreen_Login != null) WizardScreen_Login.Visibility = Visibility.Collapsed;
                ShowMainUI();
            }
            else
            {
                HideMainUI(); if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Visible;
            }
        }
        catch { HideMainUI(); if (WizardScreen_Welcome != null) WizardScreen_Welcome.Visibility = Visibility.Visible; }
    }

    private void SaveSettingsSilently()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFile)!;
            Directory.CreateDirectory(directory);
            var settings = new LauncherSettings
            {
                RamGb = (int)(RamSlider?.Value ?? 4),
                JavaArguments = JavaArgumentsBox?.Text?.Trim() ?? "",
                Username = UsernameBox?.Text?.Trim() ?? "FrostPlayer",
                LoginMode = 0,
                SetupComplete = (WizardScreen_Welcome == null || WizardScreen_Welcome.Visibility == Visibility.Collapsed) &&
                                (WizardScreen_Java == null || WizardScreen_Java.Visibility == Visibility.Collapsed) &&
                                (WizardScreen_Login == null || WizardScreen_Login.Visibility == Visibility.Collapsed),
                JavaPath = JavaPathBox?.Text?.Trim() ?? WizardJavaPathBox?.Text?.Trim() ?? "",

                // GUARDA A SESSÃO
                SessionUsername = _session?.Username ?? "",
                SessionUuid = _session?.UUID ?? "",
                SessionAccessToken = _session?.AccessToken ?? "",
                SessionUserType = _session?.UserType ?? "",
                IsMicrosoftLoggedIn = _session != null && _session.UserType == "msa"
            };
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try { SaveSettingsSilently(); SetStatus("Settings saved."); MessageBox.Show("Settings saved successfully!", "Frost Client"); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
    }

    private void BrowseJavaPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select Java 8 (javaw.exe)", Filter = "Java executable|javaw.exe|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() == true && JavaPathBox != null) { JavaPathBox.Text = dialog.FileName; SaveSettingsSilently(); }
    }

    // ==========================================
    // ACCOUNT MANAGEMENT
    // ==========================================
    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e) => await MicrosoftLoginAsync();

    private async Task MicrosoftLoginAsync()
    {
        if (MicrosoftLoginButton != null) MicrosoftLoginButton.IsEnabled = false;
        try
        {
            SetStatus("Opening Microsoft login...");
            _session = await _loginHandler.AuthenticateInteractively();
            if (UsernameBox != null) UsernameBox.Text = _session.Username;
            if (AccountStatusDetail != null) AccountStatusDetail.Text = $"Logged in: {_session.Username}";
            SaveSettingsSilently(); // GUARDA O TOKEN
        }
        catch (Exception ex) { _session = null; MessageBox.Show(ex.Message, "Login Error"); }
        finally { if (MicrosoftLoginButton != null) MicrosoftLoginButton.IsEnabled = true; }
    }

    private void SaveOfflineName_Click(object sender, RoutedEventArgs e)
    {
        var name = AccountOfflineNameBox?.Text?.Trim() ?? "";
        if (name.Length < 3 || name.Length > 16 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            MessageBox.Show("Name must be 3-16 characters long.", "Invalid Name"); return;
        }
        if (UsernameBox != null) UsernameBox.Text = name;
        _session = MSession.CreateOfflineSession(name);
        if (AccountStatusDetail != null) AccountStatusDetail.Text = $"Offline: {name}";
        SaveSettingsSilently();
        MessageBox.Show("Offline name saved successfully!", "Frost Client");
    }

    // ==========================================
    // WINDOW & NAVIGATION
    // ==========================================
    private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ClearDiscordPresence();
        _discordClient?.Dispose();
        Application.Current.Shutdown();
    }

    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var parent = ((FrameworkElement)sender).Parent as UIElement;
        if (parent != null) { var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender }; parent.RaiseEvent(args); }
    }

    private void SetStatus(string text) { if (StatusText != null) StatusText.Text = text; }
    private void SettingsText_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) SaveSettingsSilently(); }
    private void RamSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (RamLabel != null) RamLabel.Text = $"{(int)e.NewValue} GB"; if (IsLoaded) SaveSettingsSilently(); }

    private void RefreshMods() { try { Directory.CreateDirectory(_gameModsDir); var mods = Directory.GetFiles(_gameModsDir, "*.jar", SearchOption.TopDirectoryOnly); } catch { } }

    private async Task LoadRemoteVersionsAsync()
    {
        try
        {
            var launcher = new MinecraftLauncher();
            var versions = await launcher.GetAllVersionsAsync();
            _allRemoteVersions = versions.Select(v => v.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (InstallVersionBox != null) { InstallVersionBox.ItemsSource = _allRemoteVersions; if (_allRemoteVersions.Any()) InstallVersionBox.SelectedIndex = 0; }
        }
        catch { if (InstallStatusText != null) InstallStatusText.Text = "Offline: Failed to download version list."; }
    }

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (InstallVersionBox?.SelectedItem is not string version) { MessageBox.Show("Please select a version."); return; }
        if (InstallStatusText != null) InstallStatusText.Text = "Preparing installation...";
        try
        {
            var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));
            if (InstallStatusText != null) InstallStatusText.Text = $"Downloading Vanilla {version}...";
            await launcher.InstallAsync(version);
            if (InstallStatusText != null) InstallStatusText.Text = $"{version} installed successfully!";
            await RefreshVersionsAsync();
        }
        catch (Exception ex) { if (InstallStatusText != null) InstallStatusText.Text = "Error during installation."; MessageBox.Show(ex.ToString(), "Installation Error"); }
    }

    private void RefreshVersions_Click(object sender, RoutedEventArgs e) => _ = RefreshVersionsAsync();

    private void UpdatePlayButtonUI(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName) || PlayButton == null) return;
        PlayButton.Content = $"LAUNCH {versionName.ToUpper()}";
        if (HomeVersionLabel != null) HomeVersionLabel.Text = versionName;
    }

    private async Task RefreshVersionsAsync()
    {
        try
        {
            var versionsDir = Path.Combine(_gameDir, "versions");
            Directory.CreateDirectory(versionsDir);
            var allVersions = await Task.Run(() => Directory.GetDirectories(versionsDir).Where(dir => File.Exists(Path.Combine(dir, Path.GetFileName(dir) + ".json"))).Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase).ToList());
            if (VersionBox != null) VersionBox.ItemsSource = allVersions;
            if (allVersions.Any())
            {
                var target = allVersions.FirstOrDefault(name => name != null && name.Contains("frost client", StringComparison.OrdinalIgnoreCase)) ?? allVersions[0];
                if (VersionBox != null) VersionBox.SelectedItem = target;
                UpdatePlayButtonUI(target);
            }
            else { if (PlayButton != null) PlayButton.Content = "NO VERSION FOUND"; }
        }
        catch { }
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e) { if (VersionBox?.SelectedItem is string version) UpdatePlayButtonUI(version); }

    private void ShowPage(string page)
    {
        if (HomePage == null || VersionsPage == null || SettingsPage == null || AccountPage == null) return;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        VersionsPage.Visibility = page == "Versions" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility = page == "Account" ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(page);
        var currentPage = page switch { "Home" => (UIElement)HomePage, "Versions" => VersionsPage, "Settings" => SettingsPage, _ => AccountPage };
        currentPage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_gameModsDir);
    private void OpenGameFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_gameDir);
    private void OpenFolder(string path) { try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { } }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowPage("Home");
    private void VersionsNav_Click(object sender, RoutedEventArgs e) => ShowPage("Versions");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage("Settings");
    private void AccountNav_Click(object sender, RoutedEventArgs e) => ShowPage("Account");
    private void Website_Click(object sender, RoutedEventArgs e) { Process.Start(new ProcessStartInfo { FileName = "https://eu.frostclient.org/invite/discord", UseShellExecute = true }); }

    // ==========================================
    // LAUNCH GAME
    // ==========================================
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        
        string username = UsernameBox?.Text?.Trim() ?? "";
        if (_session == null)
        {
            if (username.Length < 3 || username.Length > 16 || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) 
            { MessageBox.Show("Invalid offline name."); return; }
            _session = MSession.CreateOfflineSession(username);
        }

        if (VersionBox?.SelectedItem is not string version) 
        { MessageBox.Show("No version selected. Go to Versions."); return; }

        _busy = true; 
        if (PlayButton != null) PlayButton.IsEnabled = false;
        SetStatus($"Preparing Minecraft {version}...");

        UpdateDiscordPresence($"Playing {version}", version, version);

        try
        {
            Directory.CreateDirectory(_gameDir);
            var launcher = new MinecraftLauncher(new MinecraftPath(_gameDir));

            var options = new MLaunchOption 
            { 
                Session = _session, 
                MaximumRamMb = (int)(RamSlider?.Value ?? 4) * 1024,
                GameLauncherName = "Frost Client", 
                GameLauncherVersion = "1.0.0" 
            };

            SetStatus("Verifying files..."); await Task.Delay(500);
            SetStatus("Checking Java runtime...");
            var process = await launcher.InstallAndBuildProcessAsync(version, options);
            
            SetStatus("Starting Java process..."); await Task.Delay(300);
            process.Start();
            SetStatus($"Minecraft {version} is starting...");
            await Task.Delay(2000); SetStatus($"Minecraft {version} is running.");
        }
        catch (Exception ex) 
        { 
            MessageBox.Show(ex.ToString(), "Launch Error"); 
            SetStatus("Failed to launch."); 
            UpdateDiscordPresence("In Launcher", "Frost Client", null);
        }
        finally 
        { 
            _busy = false; if (PlayButton != null) PlayButton.IsEnabled = true; 
        }
    }
}