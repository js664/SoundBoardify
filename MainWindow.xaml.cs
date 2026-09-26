using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;
using System.Windows.Media;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;

namespace VRSoundboard;

public partial class MainWindow : Window
{
    private readonly AppCoordinator _core;
    private readonly WebServerService _web;
    private readonly WebSocketHub _hub;
    private readonly SteamMicDiagnostic _diagnostic;
    private bool _loading;
    private bool? _lightApplied;
    private Point _dragOrigin;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly System.Windows.Threading.DispatcherTimer _masterSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public MainWindow(AppCoordinator core, WebServerService web, WebSocketHub hub, SteamMicDiagnostic diagnostic)
    {
        InitializeComponent(); _core = core; _web = web; _hub = hub; _diagnostic = diagnostic;
        _tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "VRSoundboard", Visible = true };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open VRSoundboard", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Stop audio", null, (_, _) => _core.Stop());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(Close));
        _tray.ContextMenuStrip = menu;
        _masterSaveTimer.Tick += (_, _) => { _masterSaveTimer.Stop(); Act(() => _core.UpdateSettings(s => { s.MasterVolume = (float)MasterSlider.Value; s.MicOutputGain = (float)MicSlider.Value; })); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && _core.Settings.MinimizeToTray) Hide(); };
        Closed += (_, _) => { _tray.Visible = false; _tray.Dispose(); menu.Dispose(); };
        core.Changed += (type, _) => { if (type != "position") Dispatcher.BeginInvoke(() => { RefreshAll(); if (type == "audio-device") RefreshDevices_Click(this, new RoutedEventArgs()); }); };
        hub.ClientCountChanged += () => Dispatcher.BeginInvoke(RefreshAll);
        Loaded += (_, _) => RefreshDevices_Click(this, new RoutedEventArgs());
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => HeaderStatus.Text = _core.Audio.Playback.Playing ? "● PLAYING" : _core.Audio.EndpointName is null ? "● AUDIO UNAVAILABLE" : "● READY";
        timer.Start();
    }
    public void RefreshAll()
    {
        _loading = true;
        try
        {
            UrlText.Text = WebUrlText.Text = _core.PhoneUrl;
            ServerText.Text = _web.Running ? "Running" : _web.LastError is null ? "Stopped" : "Stopped · " + _web.LastError;
            ClientsText.Text = $"{_hub.ClientCount} connected phone{(_hub.ClientCount == 1 ? "" : "s")}";
            AudioText.Text = _core.Audio.Status.StartsWith("Connected", StringComparison.Ordinal) ? "Connected" : _core.Audio.Status;
            EndpointText.Text = _core.Audio.EndpointName ?? "No playback endpoint selected";
            MonitorText.Text = _core.Settings.MonitorLocally
                ? "Local output: " + (_core.Devices.DefaultRenderName() ?? "Windows default output unavailable")
                : "Local output: Off";
            WebDetails.Text = $"Listening on port {_core.Settings.Port} · {_hub.ClientCount} connected clients";
            var sounds = _core.Library.All;
            var playingId = _core.Audio.Playback.SoundId;
            foreach (var sound in sounds) sound.PlaybackStatus = sound.Id == playingId ? "● Playing" : "";
            var selectedId = Selected?.Id;
            SoundsGrid.ItemsSource = sounds;
            SoundsGrid.SelectedItem = sounds.FirstOrDefault(s => s.Id == selectedId);
            MasterSlider.Value = _core.Settings.MasterVolume;
            MasterText.Text = $"{_core.Settings.MasterVolume:P0}";
            MicSlider.Value = _core.Settings.MicOutputGain;
            MicText.Text = $"{_core.Settings.MicOutputGain:P1}";
            ReconnectCheck.IsChecked = _core.Settings.ReconnectAudio;
            MonitorCheck.IsChecked = _core.Settings.MonitorLocally;
            MinimizedCheck.IsChecked = _core.Settings.StartMinimized;
            LanCheck.IsChecked = _core.Settings.LanAccess;
            WebCheck.IsChecked = _core.Settings.WebEnabled;
            PortBox.Text = _core.Settings.Port.ToString();
            LimitBox.Text = (_core.Settings.MaxUploadBytes / (1024 * 1024)).ToString();
            TokenBox.Text = _core.Settings.PairingToken ?? "";
            TrayCheck.IsChecked = _core.Settings.MinimizeToTray;
            WindowsCheck.IsChecked = _core.Settings.StartWithWindows;
            foreach (ComboBoxItem item in ThemeBox.Items) if (item.Content?.ToString() == _core.Settings.Theme) { ThemeBox.SelectedItem = item; break; }
            ApplyTheme();
            DataPathText.Text = _core.Storage.Root;
            foreach (ComboBoxItem item in DensityCombo.Items) if (item.Content?.ToString() == _core.Settings.ButtonDensity.ToString()) { DensityCombo.SelectedItem = item; break; }
            if (_core.PhoneUrl is { } phoneUrl)
            {
                using var qr = new QRCodeGenerator(); using var data = qr.CreateQrCode(phoneUrl, QRCodeGenerator.ECCLevel.Q); using var png = new PngByteQRCode(data);
                var image = new BitmapImage(); using var stream = new MemoryStream(png.GetGraphic(8)); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); QrImage.Source = image;
            }
            else QrImage.Source = null;
        }
        finally { _loading = false; }
    }
    private Sound? Selected => SoundsGrid.SelectedItem as Sound;
    private void CopyUrl_Click(object sender, RoutedEventArgs e) { if (_core.PhoneUrl is { } url) Clipboard.SetText(url); }
    private void OpenUrl_Click(object sender, RoutedEventArgs e) { if (_core.PhoneUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    private void RestoreFromTray() => Dispatcher.BeginInvoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
    private void Stop_Click(object sender, RoutedEventArgs e) => _core.Stop();
    private void Test_Click(object sender, RoutedEventArgs e) => Act(() => { var settings = _core.Settings; TestTone.Play(_core.Audio, settings.MasterVolume, _core.Storage, settings.MonitorLocally, settings.MonitorEndpointId, settings.UseVirtualMicHeadroom); });
    private async void CheckMic_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) button.IsEnabled = false;
        MicCheckText.Text = "Checking the Steam microphone capture signal…";
        try
        {
            var result = await _diagnostic.RunAsync();
            MicCheckText.Text = $"{result.Summary} Start {result.OnsetDelayMs:F0} ms · peak {result.PeakSampleLevel:P0} · level response {result.GainChangeRatio:F1}× · clipped {result.ClippedSampleCount} · gaps {result.GapCount}";
        }
        catch (Exception ex) { MicCheckText.Text = "Mic path check failed: " + ex.Message; }
        finally { if (button is not null) button.IsEnabled = true; }
    }
    private void RestartAudio_Click(object sender, RoutedEventArgs e) => Act(() => _core.Audio.Connect(_core.Settings.EndpointId));
    private async void RestartServer_Click(object sender, RoutedEventArgs e) { try { await _web.RestartAsync(); RefreshAll(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Server error"); } }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.wma", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (var file in dialog.FileNames)
        {
            try { await using var stream = File.OpenRead(file); await _core.Library.ImportAsync(stream, Path.GetFileName(file)); }
            catch (Exception ex) { MessageBox.Show($"{Path.GetFileName(file)}: {ex.Message}", "Import failed"); }
        }
        RefreshAll();
    }
    private void Play_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) Act(() => _core.Play(s.Id)); }
    private void Edit_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) new SoundEditor(_core, s) { Owner = this }.ShowDialog(); }
    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s || MessageBox.Show($"Delete {s.Name}?", "Delete sound", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (_core.Audio.Playback.SoundId == s.Id) _core.Stop();
        Act(() => _core.Library.Delete(s.Id));
    }
    private void Grid_MouseDown(object sender, MouseButtonEventArgs e) => _dragOrigin = e.GetPosition(SoundsGrid);
    private void Grid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Selected is not { } selected) return;
        var point = e.GetPosition(SoundsGrid);
        if (Math.Abs(point.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(SoundsGrid, selected, DragDropEffects.Move);
    }
    private void Grid_Drop(object sender, DragEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(SoundsGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (e.Data.GetData(typeof(Sound)) is not Sound source || row?.Item is not Sound target || source.Id == target.Id) return;
        var ids = _core.Library.All.Select(s => s.Id).ToList(); ids.Remove(source.Id); ids.Insert(ids.IndexOf(target.Id), source.Id); Act(() => _core.Library.Reorder(ids));
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        var list = _core.Devices.Enumerate(_core.Settings.EndpointId); DeviceCombo.ItemsSource = list;
        DeviceCombo.SelectedItem = list.FirstOrDefault(d => d.Id == (_core.Settings.EndpointId ?? _core.Audio.EndpointId));
    }
    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is DeviceInfo d) DeviceDetails.Text = $"{d.Name}\n{d.Id}\n{d.State} · {d.Format}\n{d.SampleRate} Hz · {d.Channels} channel(s)";
    }
    private void SelectDevice_Click(object sender, RoutedEventArgs e) { if (DeviceCombo.SelectedItem is DeviceInfo d) Act(() => _core.SelectDevice(d.Id)); }
    private void Density_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading && DensityCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content.ToString(), out var n)) Act(() => _core.UpdateSettings(s => s.ButtonDensity = n)); }
    private void Lan_Click(object sender, RoutedEventArgs e) { if (!_loading) Act(() => _core.UpdateSettings(s => s.LanAccess = LanCheck.IsChecked == true)); }
    private async void Web_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { _core.UpdateSettings(s => s.WebEnabled = WebCheck.IsChecked == true); if (_core.Settings.WebEnabled) await _web.StartAsync(); else await _web.StopAsync(); RefreshAll(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Server error"); }
    }
    private async void Port_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port)) { MessageBox.Show("Enter a valid port."); return; }
        try { _core.UpdateSettings(s => s.Port = port); await _web.RestartAsync(); RefreshAll(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Port error"); }
    }
    private void Token_Click(object sender, RoutedEventArgs e) => Act(() => { _core.UpdateSettings(s => s.PairingToken = string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text.Trim()); RefreshAll(); });
    private async void Limit_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(LimitBox.Text, out var mb)) { MessageBox.Show("Enter an upload limit in MB."); return; }
        try { _core.UpdateSettings(s => s.MaxUploadBytes = mb * 1024 * 1024); await _web.RestartAsync(); RefreshAll(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Upload limit error"); }
    }
    private void Master_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        MasterText.Text = $"{MasterSlider.Value:P0}";
        _masterSaveTimer.Stop(); _masterSaveTimer.Start();
    }
    private void Mic_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        MicText.Text = $"{MicSlider.Value:P1}";
        _masterSaveTimer.Stop(); _masterSaveTimer.Start();
    }
    private void Reconnect_Click(object sender, RoutedEventArgs e) { if (!_loading) Act(() => _core.UpdateSettings(s => s.ReconnectAudio = ReconnectCheck.IsChecked == true)); }
    private void Monitor_Click(object sender, RoutedEventArgs e) { if (!_loading) Act(() => _core.UpdateSettings(s => s.MonitorLocally = MonitorCheck.IsChecked == true)); }
    private void Minimized_Click(object sender, RoutedEventArgs e) { if (!_loading) Act(() => _core.UpdateSettings(s => s.StartMinimized = MinimizedCheck.IsChecked == true)); }
    private void Tray_Click(object sender, RoutedEventArgs e) { if (!_loading) Act(() => _core.UpdateSettings(s => s.MinimizeToTray = TrayCheck.IsChecked == true)); }
    private void Windows_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Act(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true) ?? throw new InvalidOperationException("Windows startup settings are unavailable.");
            if (WindowsCheck.IsChecked == true) key.SetValue("VRSoundboard", $"\"{Environment.ProcessPath}\""); else key.DeleteValue("VRSoundboard", false);
            _core.UpdateSettings(s => s.StartWithWindows = WindowsCheck.IsChecked == true);
        });
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        Act(() => { _core.UpdateSettings(s => s.Theme = item.Content.ToString()!); ApplyTheme(); });
    }
    private void ApplyTheme()
    {
        var theme = _core.Settings.Theme;
        var light = theme == "light" || theme == "system" && Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is int value && value == 1;
        if (_lightApplied == light) return;
        _lightApplied = light;
        void Set(string key, string color) => System.Windows.Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Set("Bg", light ? "#F4F7F8" : "#111820"); Set("Panel", light ? "#FFFFFF" : "#1A242E"); Set("AltPanel", light ? "#E9F0F2" : "#202D38"); Set("Border", light ? "#C4D2D9" : "#31404E"); Set("Text", light ? "#15242D" : "#F2F6F8"); Set("Muted", light ? "#4B626F" : "#AFC0CC"); Set("Accent", light ? "#086F5A" : "#6DD9C1");
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_core.Storage.Root) { UseShellExecute = true });
    private void Logs_Click(object sender, RoutedEventArgs e) { var path = Directory.GetFiles(_core.Storage.LogsPath, "app-*.log").OrderDescending().FirstOrDefault(); LogsText.Text = path is null ? "No logs yet." : File.ReadAllText(path); LogsText.ScrollToEnd(); }
    private void CopyLogs_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(LogsText.Text)) Clipboard.SetText(LogsText.Text); }
    private static void Act(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "VRSoundboard"); } }
}
