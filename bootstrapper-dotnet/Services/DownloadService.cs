using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class DownloadService
{
    private const int MaxAttempts = 4;
    private readonly HttpClient _http;
    private readonly LogService _log;

    public DownloadService(LogService log, Models.ProxySettings proxySettings)
    {
        _log = log;
        _http = ProxyNetworkService.CreateHttpClient(proxySettings, TimeSpan.FromMinutes(10));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("M2M-XML-EDITOR-Online-Setup/0.9");
    }


    public async Task<string> ResolveTemurinPackageUrlAsync(int javaMajorVersion, CancellationToken ct)
    {
        var metadataUri = $"https://api.adoptium.net/v3/assets/latest/{javaMajorVersion}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";
        _log.Write($"Temurin JDK {javaMajorVersion} metaadat lekérése: {metadataUri}");

        using var response = await _http.GetAsync(metadataUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidDataException($"A Temurin JDK {javaMajorVersion} metaadat-válasza üres.");

        var first = root[0];
        if (!first.TryGetProperty("binary", out var binary) ||
            !binary.TryGetProperty("package", out var package) ||
            !package.TryGetProperty("link", out var link) ||
            string.IsNullOrWhiteSpace(link.GetString()))
        {
            throw new InvalidDataException($"A Temurin JDK {javaMajorVersion} metaadat-válasza nem tartalmaz közvetlen ZIP letöltési URL-t.");
        }

        var directUrl = link.GetString()!;
        _log.Write($"Temurin JDK {javaMajorVersion} közvetlen ZIP URL: {directUrl}");
        return directUrl;
    }

    public async Task DownloadAsync(string uri, string outputFile, string displayName, IProgress<Models.ProgressInfo>? progress, CancellationToken ct)
    {
        var partialFile = outputFile + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadAttemptAsync(uri, partialFile, displayName, progress, attempt, ct);
                File.Move(partialFile, outputFile, true);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < MaxAttempts)
            {
                lastError = ex;
                _log.Write($"A letöltési kapcsolat megszakadt ({attempt}/{MaxAttempts}). Folytatási kísérlet következik: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        throw new HttpRequestException($"A(z) {displayName} letöltése {MaxAttempts} kísérlet után sem sikerült.", lastError);
    }

    private async Task DownloadAttemptAsync(
        string uri,
        string partialFile,
        string displayName,
        IProgress<Models.ProgressInfo>? progress,
        int attempt,
        CancellationToken ct)
    {
        var existingLength = File.Exists(partialFile) ? new FileInfo(partialFile).Length : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.ConnectionClose = true;
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
            _log.Write($"Letöltés folytatása: {displayName}, meglévő rész: {FormatBytes(existingLength)} (kísérlet {attempt}/{MaxAttempts})");
        }
        else
        {
            _log.Write($"Letöltés: {uri} (kísérlet {attempt}/{MaxAttempts})");
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingLength > 0 && !append)
        {
            _log.Write("A kiszolgáló nem támogatta a megszakadt letöltés folytatását; a letöltés újraindul az elejéről.");
            existingLength = 0;
        }

        var total = ResolveTotalLength(response, existingLength);
        var mode = append ? FileMode.Append : FileMode.Create;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(partialFile, mode, FileAccess.Write, FileShare.None, 64 * 1024, true);
        var buffer = new byte[64 * 1024];
        var readTotal = existingLength;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            var percent = total > 0 ? (int)Math.Min(100, readTotal * 100 / total.Value) : 0;
            progress?.Report(new Models.ProgressInfo($"Letöltés: {displayName}", Format(readTotal, total), percent));
        }
        await target.FlushAsync(ct);

        if (total.HasValue && readTotal != total.Value)
            throw new HttpIOException(HttpRequestError.ResponseEnded, $"A letöltés idő előtt megszakadt. Várt méret: {FormatBytes(total.Value)}, letöltve: {FormatBytes(readTotal)}.");

        _log.Write($"Letöltés kész: {displayName} ({FormatBytes(readTotal)})");
    }

    private static long? ResolveTotalLength(HttpResponseMessage response, long existingLength)
    {
        if (response.Content.Headers.ContentRange?.Length is long completeLength)
            return completeLength;
        if (response.Content.Headers.ContentLength is long contentLength)
            return existingLength + contentLength;
        return null;
    }

    private static bool IsRetryable(Exception ex) => ex is HttpRequestException or HttpIOException or IOException;

    private static string Format(long current, long? total) => total > 0 ? $"{FormatBytes(current)} / {FormatBytes(total.Value)}" : $"{FormatBytes(current)} letöltve";
    private static string FormatBytes(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):N1} GB" : value >= 1L << 20 ? $"{value / (double)(1L << 20):N1} MB" : value >= 1L << 10 ? $"{value / (double)(1L << 10):N1} KB" : $"{value} B";
}
