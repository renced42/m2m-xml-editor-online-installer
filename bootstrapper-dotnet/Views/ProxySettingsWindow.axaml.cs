using Avalonia.Controls;
using Avalonia.Interactivity;
using M2M.XmlEditor.Bootstrapper.Models;
using M2M.XmlEditor.Bootstrapper.Services;

namespace M2M.XmlEditor.Bootstrapper.Views;

public sealed partial class ProxySettingsWindow : Window
{
    private readonly BootstrapperConfig _config;
    private readonly ProxySettingsService _settingsService = new();
    private CancellationTokenSource? _testCts;

    public bool WasAccepted { get; private set; }
    public ProxySettings? AcceptedSettings { get; private set; }

    public ProxySettingsWindow(BootstrapperConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadPersistedSettings();
        UpdateCustomFields();
    }

    private void LoadPersistedSettings()
    {
        var settings = _settingsService.Load();
        SystemProxyRadio.IsChecked = settings.Mode == ProxyMode.System;
        CustomProxyRadio.IsChecked = settings.Mode == ProxyMode.Custom;
        NoProxyRadio.IsChecked = settings.Mode == ProxyMode.None;
        HttpProxyUrlBox.Text = settings.HttpProxyUrl;
        HttpsProxyUrlBox.Text = settings.HttpsProxyUrl;
        SameProxyCheckBox.IsChecked = settings.UseSameProxyForHttps;
        NonProxyHostsBox.Text = settings.NonProxyHosts;
        UsernameBox.Text = settings.Username;
        PasswordBox.Text = settings.Password;
        RememberCheckBox.IsChecked = settings.Remember;
    }

    private void ProxyMode_OnChanged(object? sender, RoutedEventArgs e) => UpdateCustomFields();

    private void SameProxyCheckBox_OnClick(object? sender, RoutedEventArgs e) => UpdateCustomFields();

    private void UpdateCustomFields()
    {
        var enabled = CustomProxyRadio.IsChecked == true;
        var sameProxy = SameProxyCheckBox.IsChecked == true;
        HttpProxyUrlBox.IsEnabled = enabled;
        SameProxyCheckBox.IsEnabled = enabled;
        HttpsProxyUrlBox.IsEnabled = enabled && !sameProxy;
        UsernameBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
        NonProxyHostsBox.IsEnabled = NoProxyRadio.IsChecked != true;

        if (enabled && sameProxy)
            HttpsProxyUrlBox.Text = HttpProxyUrlBox.Text;
    }

    private ProxySettings CollectSettings()
    {
        var sameProxy = SameProxyCheckBox.IsChecked == true;
        var httpProxy = HttpProxyUrlBox.Text?.Trim() ?? string.Empty;
        var httpsProxy = sameProxy ? httpProxy : HttpsProxyUrlBox.Text?.Trim() ?? string.Empty;

        return new ProxySettings
        {
            Mode = CustomProxyRadio.IsChecked == true
                ? ProxyMode.Custom
                : NoProxyRadio.IsChecked == true
                    ? ProxyMode.None
                    : ProxyMode.System,
            HttpProxyUrl = httpProxy,
            HttpsProxyUrl = httpsProxy,
            UseSameProxyForHttps = sameProxy,
            NonProxyHosts = NonProxyHostsBox.Text?.Trim() ?? string.Empty,
            Username = UsernameBox.Text?.Trim() ?? string.Empty,
            Password = PasswordBox.Text ?? string.Empty,
            Remember = RememberCheckBox.IsChecked == true
        };
    }

    private async void TestButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var settings = CollectSettings();
        try
        {
            ProxyNetworkService.Validate(settings);
            SetBusy(true);
            StatusText.Text = "GitHub API és Temurin kapcsolat ellenőrzése folyamatban...";
            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await ProxyNetworkService.TestAsync(settings, _config, _testCts.Token);
            StatusText.Text = "GitHub API (.NET): OK; Temurin letöltési útvonal (.NET): OK. A MinGit kapcsolatot a Git előkészítése után külön ellenőrzi a telepítő.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "A kapcsolat ellenőrzése időtúllépés miatt megszakadt.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "A kapcsolat nem sikerült: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ContinueButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var settings = CollectSettings();
        try
        {
            ProxyNetworkService.Validate(settings);
            _settingsService.Save(settings);
            AcceptedSettings = settings;
            WasAccepted = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "A proxy beállítás nem menthető: " + ex.Message;
        }
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WasAccepted = false;
        AcceptedSettings = null;
        Close();
    }

    private void SetBusy(bool busy)
    {
        TestButton.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy;
    }
}
