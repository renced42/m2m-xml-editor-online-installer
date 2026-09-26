namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class LogService
{
    private readonly string _file;
    public event Action<string>? LineWritten;
    public string FilePath => _file;

    public LogService(AppPaths paths)
    {
        _file = Path.Combine(paths.Logs, $"bootstrapper-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        File.AppendAllText(_file, line + Environment.NewLine, new System.Text.UTF8Encoding(false));

        // A naplófájl az elsődleges diagnosztikai csatorna. Egy UI-feliratkozó
        // hibája nem szakíthatja meg a build/telepítési folyamatot és nem
        // állíthatja le a bootstrappert.
        var handlers = LineWritten?.GetInvocationList();
        if (handlers is null) return;

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<string>)handler)(message);
            }
            catch
            {
                // Szándékosan nem naplózunk rekurzívan: a fájlba írás már
                // sikeresen megtörtént, az UI-frissítés pedig best effort.
            }
        }
    }
}
