using Avalonia;

namespace M2M.XmlEditor.Bootstrapper;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(
        args,
        Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
