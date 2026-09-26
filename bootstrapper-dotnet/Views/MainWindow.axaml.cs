using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using M2M.XmlEditor.Bootstrapper.Models;
using M2M.XmlEditor.Bootstrapper.Services;

namespace M2M.XmlEditor.Bootstrapper.Views;

public sealed partial class MainWindow : Window
{
    private readonly BootstrapperConfig _config;
    private readonly InstallationScope _scope;
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly WindowsPlatformService _platform;
    private readonly DatabaseProfileService _database;
    private readonly GitHubReleaseService _github;
    private readonly BuildPipelineService _pipeline;
    private readonly LegacyUninstallCompatibilityService _legacyCompatibility;
    private List<LegacyUninstallInfo> _legacyUninstallers = new();
    private InstalledVersionInfo? _newest;
    private List<ReleaseInfo> _releases = new();
    private CancellationTokenSource? _cts;
    private bool _shutdownRequested;
    private bool _userRequestedExit;
    private InstallationResult? _installationResult;
    private bool _licenseAccepted;
    private bool _finishDialogActive;
    private readonly string _thirdPartyLegalDirectory;
    private readonly ConcurrentQueue<string> _pendingUiLogLines = new();
    private readonly StringBuilder _uiLogText = new();
    private int _uiLogFlushScheduled;
    private bool _uiLogUpdatesEnabled = true;
    private const int UiLogMaxCharacters = 600_000;
    private const int UiLogBatchSize = 250;

    public MainWindow(InstallationScope scope, ProxySettings proxySettings)
    {
        InitializeComponent();
        _scope = scope;
        _config = ConfigService.Load();
        _paths = new AppPaths(_config, scope);
        EmbeddedAssetsService.ExtractInstallerProject(_paths.InstallerProjectDirectory);
        _thirdPartyLegalDirectory = Path.Combine(_paths.CacheRoot, "third-party-licenses");
        EmbeddedAssetsService.ExtractBundledLegalDocuments(_thirdPartyLegalDirectory);
        _log = new LogService(_paths);
        _platform = new WindowsPlatformService(_paths, _log, _config.ApplicationName);
        _database = new DatabaseProfileService(_paths, _log);
        var downloader = new DownloadService(_log, proxySettings);
        var zip = new ZipService();
        var process = new ProcessService(_log);
        var runtime = new RuntimeService(_config, _paths, downloader, zip, _log);
        _github = new GitHubReleaseService(_config, _paths, _log, proxySettings);
        _pipeline = new BuildPipelineService(_config, _paths, downloader, zip, runtime, process, _platform, _database, _log, proxySettings, ConfirmServerCertificateAsync);
        _legacyCompatibility = new LegacyUninstallCompatibilityService(_platform, runtime, _log);
        InstallScopeText.Text = scope == InstallationScope.AllUsers
            ? "Minden felhasználónak (gépszintű telepítés)"
            : "Csak nekem (felhasználói telepítés)";
        _log.LineWritten += QueueUiLogLine;
        _log.Write("Proxy üzemmód: " + ProxyNetworkService.Describe(proxySettings));
        Opened += async (_, _) => await LoadReleasesAsync();
        Closing += MainWindow_OnClosing;
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _log.Write($"Főablak bezárási esemény: reason={e.CloseReason}, programmatic={e.IsProgrammatic}, " +
                   $"shutdownRequested={_shutdownRequested}, userRequestedExit={_userRequestedExit}, " +
                   $"finishDialogActive={_finishDialogActive}.");

        if (_shutdownRequested) return;

        // A kész képernyőt sem programozott Close(), sem egy kívülről érkező
        // ApplicationShutdown nem zárhatja be. Bezárás csak explicit felhasználói
        // műveletre történhet.
        if (_finishDialogActive && !_userRequestedExit && e.IsProgrammatic)
        {
            e.Cancel = true;
            _log.Write("Programozott alkalmazásleállítás blokkolva a befejező képernyőn.");
            return;
        }

        // A címsor X nem programozott bezárás, ezért valódi felhasználói kérésnek számít.
        if (!e.IsProgrammatic)
            _userRequestedExit = true;

        if (!_userRequestedExit)
        {
            e.Cancel = true;
            _log.Write("Nem felhasználó által kezdeményezett bezárás blokkolva.");
            return;
        }

        _shutdownRequested = true;
        try
        {
            _cts?.Cancel();
            _log.Write("A bootstrapper bezárását a felhasználó kérte. A futó műveletek leállítása...");
        }
        catch (ObjectDisposedException) { }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void QueueUiLogLine(string line)
    {
        if (!_uiLogUpdatesEnabled) return;

        _pendingUiLogLines.Enqueue(line);
        ScheduleUiLogFlush();
    }

    private void ScheduleUiLogFlush()
    {
        if (!_uiLogUpdatesEnabled) return;
        if (Interlocked.Exchange(ref _uiLogFlushScheduled, 1) != 0) return;

        Dispatcher.UIThread.Post(FlushPendingUiLogLines, DispatcherPriority.Background);
    }

    private void FlushPendingUiLogLines()
    {
        try
        {
            if (!_uiLogUpdatesEnabled)
            {
                while (_pendingUiLogLines.TryDequeue(out _)) { }
                return;
            }

            var appended = 0;
            while (appended < UiLogBatchSize && _pendingUiLogLines.TryDequeue(out var line))
            {
                _uiLogText.AppendLine(line);
                appended++;
            }

            if (_uiLogText.Length > UiLogMaxCharacters)
            {
                var removeCount = _uiLogText.Length - UiLogMaxCharacters;
                var nextLineBreak = _uiLogText.ToString().IndexOf('\n', removeCount);
                if (nextLineBreak >= 0) removeCount = nextLineBreak + 1;
                _uiLogText.Remove(0, Math.Min(removeCount, _uiLogText.Length));
            }

            if (appended > 0)
            {
                LogBox.Text = _uiLogText.ToString();
                LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _uiLogFlushScheduled, 0);
            if (_uiLogUpdatesEnabled && !_pendingUiLogLines.IsEmpty) ScheduleUiLogFlush();
        }
    }

    private void StopUiLogUpdates()
    {
        _uiLogUpdatesEnabled = false;
        _log.LineWritten -= QueueUiLogLine;
        while (_pendingUiLogLines.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _uiLogFlushScheduled, 0);
    }

    private async Task LoadReleasesAsync()
    {
        HideError();
        SetBusy(true);
        StageText.Text = "GitHub release-lista betöltése...";
        try
        {
            _platform.AssertApplicationNotRunning();
            await RefreshLegacyCompatibilityAsync();
            _newest = _platform.GetNewestInstalledVersion();
            if (_newest is null)
            {
                InstalledVersionText.Text = "Nincs telepített release";
                DatabaseText.Text = "–";
            }
            else
            {
                var legacy = _newest.Legacy ? " (legacy)" : string.Empty;
                InstalledVersionText.Text = _newest.Version + legacy;
                DatabaseText.Text = _database.DetectDatabaseKind(_newest.DataDirectory);
            }

            var catalog = await _github.GetCatalogAsync(CancellationToken.None);
            _releases = FilterInstallable(catalog).ToList();
            ReleaseCombo.ItemsSource = _releases.Select(r => r.DisplayName).ToArray();
            ReleaseCombo.SelectedIndex = GetDefaultReleaseIndex();
            StageText.Text = _releases.Count > 0
                ? "A telepítés indítására kész."
                : "Nincs telepíthető release.";
            DetailText.Text = _newest is null ? "Első telepítés" : $"Downgrade tiltva: minimum {_newest.Version}";
        }
        catch (Exception ex)
        {
            ShowInlineError(ex.Message);
            StageText.Text = "A release-lista betöltése nem sikerült.";
        }
        finally
        {
            SetBusy(false);
            UpdateSelectionState();
        }
    }

    private int GetDefaultReleaseIndex()
    {
        if (_releases.Count == 0) return -1;
        var stable = _releases.FindIndex(r => !r.Prerelease);
        return stable >= 0 ? stable : 0;
    }

    private IEnumerable<ReleaseInfo> FilterInstallable(IEnumerable<ReleaseInfo> catalog)
    {
        SemVer? minimum = null;
        if (_newest is not null) SemVer.TryParse(_newest.Version, out minimum);
        return catalog.Where(r => SemVer.TryParse(r.TagName, out var v) && v is not null && (minimum is null || v.CompareTo(minimum) >= 0));
    }

    private void ReleaseCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void UpdateSelectionState()
    {
        InstallButton.IsEnabled = _licenseAccepted && ReleaseCombo.SelectedIndex >= 0 && !ProgressBar.IsIndeterminate;
        if (_newest is null || ReleaseCombo.SelectedIndex < 0)
        {
            CloneH2CheckBox.IsEnabled = false;
            CloneH2CheckBox.IsChecked = false;
            return;
        }

        var selected = _releases[ReleaseCombo.SelectedIndex];
        SemVer.TryParse(selected.TagName, out var target);
        SemVer.TryParse(_newest.Version, out var current);
        var db = _database.DetectDatabaseKind(_newest.DataDirectory);
        DatabaseText.Text = db;
        var canClone = db == "H2" && target is not null && current is not null && target.CompareTo(current) > 0;
        CloneH2CheckBox.IsEnabled = canClone;
        if (!canClone) CloneH2CheckBox.IsChecked = false;
        CloneH2CheckBox.Content = canClone
            ? $"Az {_newest.Version} H2 konfigurációjának és adatainak másolása a {target!.Normalized} verzióhoz"
            : db == "H2"
                ? "H2 adatmásolás csak újabb release telepítésekor választható"
                : "Konfiguráció/adat másolása tiltott (csak H2 adatbázis esetén engedélyezett)";
    }

    private async Task RefreshLegacyCompatibilityAsync()
    {
        _legacyUninstallers = _platform.GetLegacyIzPackRegistryListenerInstallations().ToList();
        LegacyCompatibilityBorder.IsVisible = _legacyUninstallers.Count > 0;
        LegacyUninstallCombo.ItemsSource = _legacyUninstallers
            .Select(item => $"{item.Version} – {(item.Scope == InstallationScope.AllUsers ? "minden felhasználó" : "csak nekem")}")
            .ToArray();
        LegacyUninstallCombo.SelectedIndex = _legacyUninstallers.Count > 0 ? 0 : -1;
        LegacyCompatibilityStatus.Text = _legacyUninstallers.Count == 0
            ? string.Empty
            : $"{_legacyUninstallers.Count} korábbi, IzPack registry-listeneres telepítés észlelve. Eltávolításukhoz UAC-jóváhagyás szükséges lehet.";
        await Task.CompletedTask;
    }

    private async void LegacyUninstallButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LegacyUninstallCombo.SelectedIndex < 0 || LegacyUninstallCombo.SelectedIndex >= _legacyUninstallers.Count)
            return;

        HideError();
        var item = _legacyUninstallers[LegacyUninstallCombo.SelectedIndex];
        LegacyUninstallButton.IsEnabled = false;
        try
        {
            _platform.AssertApplicationNotRunning();
            var progress = new Progress<ProgressInfo>(p =>
            {
                StageText.Text = p.Stage;
                DetailText.Text = p.Detail;
            });
            await _legacyCompatibility.UninstallAsync(item, progress, CancellationToken.None);
            await RefreshLegacyCompatibilityAsync();
            _newest = _platform.GetNewestInstalledVersion();
            InstalledVersionText.Text = _newest is null ? "Nincs telepített release" : _newest.Version + (_newest.Legacy ? " (legacy)" : string.Empty);
            DatabaseText.Text = _newest is null ? "–" : _database.DetectDatabaseKind(_newest.DataDirectory);
            StageText.Text = "A korábbi telepítés eltávolítása befejeződött.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowInlineError("A rendszergazdai jóváhagyás elmaradt, ezért a korábbi telepítés eltávolítása megszakadt.");
        }
        catch (Exception ex)
        {
            _log.Write("Korábbi telepítés kompatibilitási eltávolítási hiba: " + ex);
            ShowInlineError(ex.Message);
        }
        finally
        {
            LegacyUninstallButton.IsEnabled = _legacyUninstallers.Count > 0;
        }
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e) => await LoadReleasesAsync();

    private async Task ShowThirdPartyLicensesDialogAsync(Window owner)
    {
        var text = await ReadThirdPartyLegalTextAsync();
        var dialog = new Window
        {
            Title = "M2M XML EDITOR – Harmadik féltől származó licencek",
            Width = 860,
            Height = 680,
            MinWidth = 680,
            MinHeight = 500,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var closeButton = new Button
        {
            Content = "Bezárás",
            MinWidth = 120,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => dialog.Close();

        var legalText = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = "Consolas"
        };

        var content = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12
        };
        var intro = new TextBlock
        {
            Text = "A bundled telepítőben található Eclipse Temurin, Apache Maven és Git for Windows MinGit komponensek eredeti licenc- és jogi közleményei.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        Grid.SetRow(intro, 0);
        Grid.SetRow(legalText, 1);
        Grid.SetRow(closeButton, 2);
        content.Children.Add(intro);
        content.Children.Add(legalText);
        content.Children.Add(closeButton);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
    }

    private async Task<string> ReadThirdPartyLegalTextAsync()
    {
        var builder = new StringBuilder();
        var noticePath = Path.Combine(_thirdPartyLegalDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(noticePath))
        {
            builder.AppendLine(await File.ReadAllTextAsync(noticePath));
            builder.AppendLine();
            builder.AppendLine(new string('=', 88));
        }

        var licensesRoot = Path.Combine(_thirdPartyLegalDirectory, "licenses");
        if (!Directory.Exists(licensesRoot))
            return builder.ToString();

        foreach (var file in Directory.EnumerateFiles(licensesRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(licensesRoot, file);
            builder.AppendLine();
            builder.AppendLine($"===== {relative} =====");
            try
            {
                builder.AppendLine(await File.ReadAllTextAsync(file));
            }
            catch (Exception ex)
            {
                builder.AppendLine($"[A fájl szöveges megjelenítése nem sikerült: {ex.Message}]");
            }
        }

        return builder.ToString();
    }

    private async void LicenseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var licensePath = Path.Combine(_paths.InstallerProjectDirectory, "src", "main", "izpack", "resources", "LICENSE.txt");
        if (!File.Exists(licensePath))
        {
            ShowInlineError("A licencszöveg nem található, ezért a telepítés nem indítható.");
            return;
        }

        var text = await File.ReadAllTextAsync(licensePath);
        var thirdPartyNoticePath = Path.Combine(_thirdPartyLegalDirectory, "THIRD-PARTY-NOTICES.txt");
        var hasThirdPartyLicenses = File.Exists(thirdPartyNoticePath);
        var accepted = false;
        var dialog = new Window
        {
            Title = "M2M XML EDITOR – Licenc és nyilatkozatok",
            Width = 800,
            Height = 700,
            MinWidth = 660,
            MinHeight = 540,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var acceptButton = new Button
        {
            Content = "Tovább a telepítéshez",
            MinWidth = 180,
            IsDefault = true,
            IsEnabled = false
        };
        var rejectButton = new Button
        {
            Content = "Elutasítom",
            MinWidth = 120,
            IsCancel = true
        };

        var licenseText = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var m2mLicenseCheckBox = new CheckBox
        {
            Content = "Elfogadom az M2M XML EDITOR licencfeltételeit.",
            IsChecked = false
        };
        var thirdPartyLicenseCheckBox = new CheckBox
        {
            Content = "Elfogadom a harmadik féltől származó komponensek licencfeltételeit.",
            IsChecked = false,
            IsVisible = hasThirdPartyLicenses
        };

        var thirdPartyButton = new Button
        {
            Content = "Harmadik féltől származó licencek megtekintése",
            MinWidth = 300,
            IsVisible = hasThirdPartyLicenses,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        thirdPartyButton.Click += async (_, _) => await ShowThirdPartyLicensesDialogAsync(dialog);

        void UpdateAcceptButtonState()
        {
            var m2mAccepted = m2mLicenseCheckBox.IsChecked == true;
            var thirdPartyAccepted = !hasThirdPartyLicenses || thirdPartyLicenseCheckBox.IsChecked == true;
            acceptButton.IsEnabled = m2mAccepted && thirdPartyAccepted;
        }

        m2mLicenseCheckBox.Click += (_, _) => UpdateAcceptButtonState();
        thirdPartyLicenseCheckBox.Click += (_, _) => UpdateAcceptButtonState();

        acceptButton.Click += (_, _) =>
        {
            if (!acceptButton.IsEnabled)
                return;

            accepted = true;
            dialog.Close();
        };
        rejectButton.Click += (_, _) => dialog.Close();

        var acceptancePanel = new StackPanel
        {
            Spacing = 8
        };
        acceptancePanel.Children.Add(m2mLicenseCheckBox);
        if (hasThirdPartyLicenses)
        {
            acceptancePanel.Children.Add(thirdPartyButton);
            acceptancePanel.Children.Add(thirdPartyLicenseCheckBox);
        }

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 10
        };
        Grid.SetColumn(rejectButton, 1);
        Grid.SetColumn(acceptButton, 2);
        footer.Children.Add(rejectButton);
        footer.Children.Add(acceptButton);

        var content = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
            RowSpacing = 14
        };
        Grid.SetRow(licenseText, 0);
        Grid.SetRow(acceptancePanel, 1);
        Grid.SetRow(footer, 2);
        content.Children.Add(licenseText);
        content.Children.Add(acceptancePanel);
        content.Children.Add(footer);
        dialog.Content = content;

        await dialog.ShowDialog(this);

        _licenseAccepted = accepted;
        LicenseStatusText.Text = accepted
            ? hasThirdPartyLicenses
                ? "Az M2M XML EDITOR és a harmadik féltől származó komponensek licencfeltételeit elfogadta. A telepítés folytatható."
                : "Az M2M XML EDITOR licencfeltételeit elfogadta. A telepítés folytatható."
            : "A szükséges licencfeltételeket nem fogadta el. A telepítés nem folytatható.";
        HideError();
        UpdateSelectionState();
    }

    private async void InstallButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ReleaseCombo.SelectedIndex < 0) return;
        HideError();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var release = _releases[ReleaseCombo.SelectedIndex];
            var progress = new Progress<ProgressInfo>(p =>
            {
                StageText.Text = p.Stage;
                DetailText.Text = p.Detail;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = p.Percent;
            });
            _installationResult = await _pipeline.RunAsync(release, CloneH2CheckBox.IsChecked == true, progress, _cts.Token);
            _log.Write($"A telepítési pipeline sikeresen visszatért. Verzió: {_installationResult.Version}. A befejező dialógus megnyitása következik.");
            await ShowFinishDialogAsync(_installationResult);
        }
        catch (OperationCanceledException) when (_shutdownRequested)
        {
            _log.Write("A folyamat a bootstrapper bezárása miatt megszakadt.");
        }
        catch (Exception ex)
        {
            _log.Write("HIBA: " + ex);
            ShowInlineError(ex.Message);
            StageText.Text = "A telepítés nem sikerült.";
            DetailText.Text = "A hiba elhárítása után az Újrapróbálás gombbal ismét elindítható.";
            InstallButton.Content = "Újrapróbálás";
        }
        finally
        {
            if (!_shutdownRequested && !_finishDialogActive)
            {
                SetBusy(false);
                UpdateSelectionState();
            }
        }
    }

    private async Task<bool> ConfirmServerCertificateAsync(TlsServerCertificateInfo certificate)
    {
        var accepted = false;
        var dialog = new Window
        {
            Title = "TLS tanúsítvány megbízhatósága",
            Width = 760,
            Height = 570,
            MinWidth = 620,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var trustButton = new Button
        {
            Content = "Megbízom és folytatom",
            MinWidth = 190,
            IsDefault = true
        };
        var cancelButton = new Button
        {
            Content = "Mégse",
            MinWidth = 110,
            IsCancel = true
        };

        trustButton.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var details = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "A Maven nem tudta ellenőrizni a szerver TLS tanúsítványát.",
                    FontSize = 20,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Csak akkor folytasd, ha felismered és megbízhatónak tartod ezt a szervert és tanúsítványt. Az elfogadás a fingerprinthez kötődik; tanúsítványcsere esetén a program újra rákérdez.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                CreateCertificateDetail("Szerver", certificate.Endpoint),
                CreateCertificateDetail("Subject", certificate.Subject),
                CreateCertificateDetail("Issuer", certificate.Issuer),
                CreateCertificateDetail("Érvényes ettől", certificate.NotBefore.ToString("yyyy-MM-dd HH:mm:ss")),
                CreateCertificateDetail("Érvényes eddig", certificate.NotAfter.ToString("yyyy-MM-dd HH:mm:ss")),
                CreateCertificateDetail("SHA-256 fingerprint", certificate.Sha256Fingerprint)
            }
        };

        var scroller = new ScrollViewer
        {
            Content = details,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var footer = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelButton, trustButton }
        };

        var root = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 18
        };
        Grid.SetRow(scroller, 0);
        Grid.SetRow(footer, 1);
        root.Children.Add(scroller);
        root.Children.Add(footer);
        dialog.Content = root;

        await dialog.ShowDialog(this);
        return accepted;
    }

    private static Control CreateCertificateDetail(string label, string value)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = label + ":",
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        panel.Children.Add(new TextBox
        {
            Text = value,
            IsReadOnly = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 36
        });
        return panel;
    }

    private async Task ShowFinishDialogAsync(InstallationResult result)
    {
        _finishDialogActive = true;
        StopUiLogUpdates();
        StageText.Text = "A telepítés sikeresen befejeződött.";
        DetailText.Text = $"Telepített verzió: {result.Version}";
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 100;

        var launchCheckBox = new CheckBox
        {
            Content = "M2M XML EDITOR indítása",
            IsChecked = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var closeButton = new Button
        {
            Content = "Bezárás",
            MinWidth = 120,
            IsDefault = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        var finishWindow = new Window
        {
            Title = "M2M XML EDITOR – Telepítés befejezve",
            Width = 680,
            Height = 360,
            MinWidth = 620,
            MinHeight = 320,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowState = Avalonia.Controls.WindowState.Maximized,
            ShowInTaskbar = true
        };

        var body = new StackPanel
        {
            Spacing = 16,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "A telepítés sikeresen befejeződött.",
                    FontSize = 28,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    TextAlignment = Avalonia.Media.TextAlignment.Center
                },
                new TextBlock
                {
                    Text = $"Telepített verzió: {result.Version}",
                    FontSize = 18,
                    TextAlignment = Avalonia.Media.TextAlignment.Center
                },
                new TextBlock
                {
                    Text = "Az alkalmazás első indításakor a setup.html oldalon végezhető el az adatbázis-, könyvtár-, admin- és integrációs konfiguráció.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    TextAlignment = Avalonia.Media.TextAlignment.Center,
                    Opacity = 0.78
                }
            }
        };

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        Grid.SetColumn(launchCheckBox, 0);
        Grid.SetColumn(closeButton, 1);
        footer.Children.Add(launchCheckBox);
        footer.Children.Add(closeButton);

        var root = new Grid
        {
            Margin = new Thickness(28),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 20
        };
        Grid.SetRow(body, 0);
        Grid.SetRow(footer, 1);
        root.Children.Add(body);
        root.Children.Add(footer);
        finishWindow.Content = root;

        var completion = new TaskCompletionSource<(bool CloseButtonPressed, bool LaunchRequested)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeButtonPressed = false;
        var launchRequested = false;

        closeButton.Click += (_, _) =>
        {
            closeButtonPressed = true;
            launchRequested = launchCheckBox.IsChecked == true;
            finishWindow.Close();
        };
        finishWindow.Closed += (_, _) =>
            completion.TrySetResult((closeButtonPressed, launchRequested));

        _log.Write("A befejező ablak átvétele főablakként az UI szálon.");
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = finishWindow;

        Hide();
        finishWindow.Opened += (_, _) =>
        {
            _log.Write("A befejező ablak megjelent és az alkalmazás főablaka lett.");
            finishWindow.Activate();
        };
        finishWindow.Show();

        var completionResult = await completion.Task;
        _finishDialogActive = false;

        _log.Write(completionResult.CloseButtonPressed
            ? "A befejező ablak Bezárás gombját a felhasználó megnyomta."
            : "A befejező ablakot a felhasználó a címsor X gombjával zárta be.");

        if (completionResult.CloseButtonPressed && completionResult.LaunchRequested)
            LaunchInstalledApplication(result);

        RequestShutdownAfterFinish();
    }

    private void LaunchInstalledApplication(InstallationResult result)
    {
        try
        {
            Process.Start(new ProcessStartInfo(result.ExecutablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(result.ExecutablePath)!
            });
            _log.Write("A telepített M2M XML EDITOR indítása megtörtént.");
        }
        catch (Exception ex)
        {
            _log.Write("Az alkalmazás indítása nem sikerült: " + ex.Message);
        }
    }

    private void RequestShutdownAfterFinish()
    {
        _userRequestedExit = true;
        _shutdownRequested = true;
        _log.Write("A befejező dialógus lezárása után a bootstrapper szabályosan leáll.");
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && _licenseAccepted && ReleaseCombo.SelectedIndex >= 0;
        LicenseButton.IsEnabled = !busy;
        ReleaseCombo.IsEnabled = !busy;
        CloneH2CheckBox.IsEnabled = !busy && CloneH2CheckBox.IsEnabled;
        if (busy)
        {
            ProgressBar.Value = 0;
            ProgressBar.IsIndeterminate = true;
        }
        else
        {
            ProgressBar.IsIndeterminate = false;
        }
    }

    private void ShowInlineError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.IsVisible = true;
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorBorder.IsVisible = false;
    }
}
