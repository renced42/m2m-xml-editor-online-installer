using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using M2M.XmlEditor.Bootstrapper.Models;
using M2M.XmlEditor.Bootstrapper.Services;
using M2M.XmlEditor.Bootstrapper.Views;

namespace M2M.XmlEditor.Bootstrapper;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var args = desktop.Args ?? Array.Empty<string>();
            var resumedAllUsers = args.Any(a => string.Equals(a, "--all-users", StringComparison.OrdinalIgnoreCase));
            var backupAcknowledged = args.Any(a => string.Equals(a, "--backup-acknowledged", StringComparison.OrdinalIgnoreCase));

            if (resumedAllUsers && backupAcknowledged)
            {
                if (!IsSupportedWindows() || !IsAdministrator())
                {
                    desktop.Shutdown(1);
                }
                else
                {
                    ShowProxySettings(desktop, InstallationScope.AllUsers);
                }
            }
            else
            {
                ShowBackupWarning(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowBackupWarning(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var warning = new BackupWarningWindow();
        desktop.MainWindow = warning;

        warning.Accepted += async (_, _) =>
        {
            if (!IsSupportedWindows())
            {
                await ShowUnsupportedSystemAsync(warning);
                desktop.Shutdown(1);
                return;
            }

            ShowScopeSelection(desktop, warning);
        };

        warning.Rejected += (_, _) => desktop.Shutdown();
        warning.Closed += (_, _) =>
        {
            if (desktop.MainWindow == warning) desktop.Shutdown();
        };
    }

    private static void ShowScopeSelection(IClassicDesktopStyleApplicationLifetime desktop, Window previousWindow)
    {
        var scopeWindow = new ScopeSelectionWindow();
        desktop.MainWindow = scopeWindow;
        scopeWindow.Show();
        previousWindow.Close();

        scopeWindow.Accepted += async scope =>
        {
            if (scope == InstallationScope.AllUsers && !IsAdministrator())
            {
                try
                {
                    RelaunchElevatedForAllUsers();
                    desktop.Shutdown();
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    await ShowMessageAsync(scopeWindow,
                        "A rendszergazdai jóváhagyás elmaradt. A 'Minden felhasználónak' telepítéshez UAC-jóváhagyás szükséges.",
                        "Rendszergazdai jogosultság szükséges");
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync(scopeWindow,
                        "A rendszergazdai indítás nem sikerült.\n\n" + ex.Message,
                        "A telepítés nem folytatható");
                }
                return;
            }

            ShowProxySettings(desktop, scope);
            scopeWindow.Close();
        };

        scopeWindow.Rejected += (_, _) => desktop.Shutdown();
        scopeWindow.Closed += (_, _) =>
        {
            if (desktop.MainWindow == scopeWindow) desktop.Shutdown();
        };
    }

    private static void ShowProxySettings(IClassicDesktopStyleApplicationLifetime desktop, InstallationScope scope)
    {
        var proxyWindow = new ProxySettingsWindow(ConfigService.Load());
        desktop.MainWindow = proxyWindow;

        proxyWindow.Closed += async (_, _) =>
        {
            if (!proxyWindow.WasAccepted || proxyWindow.AcceptedSettings is null)
            {
                desktop.Shutdown();
                return;
            }

            try
            {
                ShowMainWindow(desktop, scope, proxyWindow.AcceptedSettings);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(
                    proxyWindow,
                    "A telepítő főablaka nem nyitható meg.\n\n" + ex.Message,
                    "A telepítés nem folytatható");
                desktop.Shutdown(1);
            }
        };

        proxyWindow.Show();
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, InstallationScope scope, ProxySettings proxySettings)
    {
        var mainWindow = new MainWindow(scope, proxySettings);
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevatedForAllUsers()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("A bootstrapper futtatható állománya nem határozható meg.");

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--all-users --backup-acknowledged",
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private static bool IsSupportedWindows()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0)
               && Environment.Is64BitOperatingSystem
               && Environment.Is64BitProcess;
    }

    private static async Task ShowUnsupportedSystemAsync(Window owner)
    {
        await ShowMessageAsync(owner,
            "Az M2M XML EDITOR Online Setup jelenlegi kiadása 64 bites Windows 10 vagy Windows 11 rendszeren fut. " +
            "32 bites Windows, illetve Windows 7/8/8.1 nem támogatott.\n\n" +
            $"Észlelt környezet: {Environment.OSVersion.VersionString}; 64 bites operációs rendszer: {(Environment.Is64BitOperatingSystem ? "igen" : "nem")}",
            "Nem támogatott Windows rendszer");
    }

    private static async Task ShowMessageAsync(Window owner, string message, string title)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 580,
            Height = 270,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var close = new Button
        {
            Content = "Rendben",
            MinWidth = 110,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        close.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(26),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                close
            }
        };

        await dialog.ShowDialog(owner);
    }
}
