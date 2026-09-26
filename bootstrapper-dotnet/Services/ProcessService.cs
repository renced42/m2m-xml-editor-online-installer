using System.Diagnostics;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class ProcessService
{
    private readonly LogService _log;
    public ProcessService(LogService log) => _log = log;

    public async Task RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IDictionary<string,string>? environment,
        Action<string>? lineCallback,
        CancellationToken ct,
        string? successfulCompletionLine = null,
        TimeSpan? completionGracePeriod = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        if (environment is not null) foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;
        _log.Write($"Folyamat: {fileName} {string.Join(' ', arguments)}");

        var completionSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleOutputLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleOutputLine(e.Data);

        if (!process.Start()) throw new InvalidOperationException($"Nem indítható: {fileName}");
        _log.Write($"Gyermekfolyamat elindult: PID={process.Id}, fájl={fileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cancellation = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // A folyamat időközben már befejeződött.
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _log.Write($"A gyermekfolyamat leállítása nem sikerült: {ex.Message}");
            }
        });

        var exitTask = process.WaitForExitAsync(ct);
        var forcedAfterSuccessfulCompletion = false;

        if (string.IsNullOrWhiteSpace(successfulCompletionLine))
        {
            await exitTask;
        }
        else
        {
            var firstCompleted = await Task.WhenAny(exitTask, completionSeen.Task.WaitAsync(ct));
            if (firstCompleted == exitTask)
            {
                await exitTask;
            }
            else
            {
                var grace = completionGracePeriod ?? TimeSpan.FromSeconds(5);
                _log.Write($"A gyermekfolyamat sikeres befejezési jelzést adott. Legfeljebb {grace.TotalSeconds:0} másodpercet várunk a normál kilépésére. PID={process.Id}.");

                var gracefulExit = await Task.WhenAny(exitTask, Task.Delay(grace, ct));
                if (gracefulExit == exitTask)
                {
                    await exitTask;
                }
                else
                {
                    _log.Write($"A gyermekfolyamat a sikeres befejezési jelzés után sem lépett ki. A bent maradt folyamatfa lezárása következik. PID={process.Id}.");
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            forcedAfterSuccessfulCompletion = true;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // A folyamat éppen a Kill előtt fejeződött be.
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"A sikeres befejezési jelzés után bent maradt gyermekfolyamat nem zárható le: {fileName}", ex);
                    }

                    if (!process.HasExited)
                        await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        // Normál kilépésnél a szinkron WaitForExit biztosítja, hogy az átirányított
        // stdout/stderr aszinkron eseményei is teljesen kiürüljenek.
        if (!forcedAfterSuccessfulCompletion)
            process.WaitForExit();

        if (forcedAfterSuccessfulCompletion)
        {
            _log.Write($"Gyermekfolyamat lezárva a sikeres befejezési jelzés után: PID={process.Id}, fájl={fileName}. A telepítés eredményét a post-install ellenőrzés hitelesíti.");
            return;
        }

        _log.Write($"Gyermekfolyamat befejeződött: PID={process.Id}, exitCode={process.ExitCode}, fájl={fileName}");
        if (process.ExitCode != 0) throw new InvalidOperationException($"A folyamat hibával állt le ({process.ExitCode}): {fileName}");

        void HandleOutputLine(string? line)
        {
            if (line is null) return;

            // A terminális folyamatjelzést minden napló/UI callback előtt állítjuk be.
            // Így egy megjelenítési hiba nem akadályozhatja meg a sikeres IzPack
            // befejezés felismerését.
            if (!string.IsNullOrWhiteSpace(successfulCompletionLine) &&
                line.Contains(successfulCompletionLine, StringComparison.Ordinal))
            {
                completionSeen.TrySetResult(true);
            }

            try
            {
                _log.Write(line);
                lineCallback?.Invoke(line);
            }
            catch (Exception ex)
            {
                // A redirected stdout/stderr eseményszálon keletkező callback-hiba
                // nem lehet kezeletlen kivétel, mert az a teljes bootstrappert
                // lezárhatná. A LogService UI eseménye önmagában is izolált, ez
                // a védelem a külső lineCallback implementációkra is vonatkozik.
                try
                {
                    _log.Write($"A gyermekfolyamat kimeneti callbackje hibát adott, a folyamatfigyelés folytatódik: {ex.Message}");
                }
                catch
                {
                    // A folyamatfigyelés fontosabb, mint egy másodlagos naplóhiba.
                }
            }
        }
    }
}
