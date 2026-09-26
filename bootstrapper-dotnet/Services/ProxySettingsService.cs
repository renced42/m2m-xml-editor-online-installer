using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Services;

public sealed class ProxySettingsService
{
    private const uint CryptProtectUiForbidden = 0x1;
    private readonly string _settingsDirectory;
    private readonly string _settingsFile;
    private readonly string _secretFile;
    private readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public ProxySettingsService()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsDirectory = Path.Combine(local, "M2M-XML-EDITOR-Online-Setup");
        _settingsFile = Path.Combine(_settingsDirectory, "settings.json");
        _secretFile = Path.Combine(_settingsDirectory, "proxy.secret");
    }

    public ProxySettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return new ProxySettings();
            var persisted = JsonSerializer.Deserialize<PersistedProxySettings>(File.ReadAllText(_settingsFile), _jsonOptions);
            if (persisted is null) return new ProxySettings();

            var legacyProxy = persisted.ProxyUrl ?? string.Empty;
            var httpProxy = persisted.HttpProxyUrl ?? legacyProxy;
            var sameProxy = persisted.UseSameProxyForHttps ?? string.IsNullOrWhiteSpace(persisted.HttpsProxyUrl);
            var httpsProxy = sameProxy ? httpProxy : persisted.HttpsProxyUrl ?? httpProxy;

            return new ProxySettings
            {
                Mode = persisted.ProxyMode,
                HttpProxyUrl = httpProxy,
                HttpsProxyUrl = httpsProxy,
                UseSameProxyForHttps = sameProxy,
                NonProxyHosts = persisted.NonProxyHosts ?? string.Empty,
                Username = persisted.ProxyUsername ?? string.Empty,
                Password = LoadPassword(),
                Remember = persisted.RememberProxy
            };
        }
        catch
        {
            return new ProxySettings();
        }
    }

    public void Save(ProxySettings settings)
    {
        if (!settings.Remember)
        {
            Clear();
            return;
        }

        Directory.CreateDirectory(_settingsDirectory);
        var persisted = new PersistedProxySettings
        {
            ProxyMode = settings.Mode,
            HttpProxyUrl = settings.HttpProxyUrl.Trim(),
            HttpsProxyUrl = settings.EffectiveHttpsProxyUrl.Trim(),
            UseSameProxyForHttps = settings.UseSameProxyForHttps,
            NonProxyHosts = settings.NonProxyHosts.Trim(),
            ProxyUsername = settings.Username.Trim(),
            RememberProxy = true
        };

        var temp = _settingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(persisted, _jsonOptions), new UTF8Encoding(false));
        File.Move(temp, _settingsFile, true);

        if (settings.Mode == ProxyMode.Custom && !string.IsNullOrEmpty(settings.Password))
        {
            var protectedBytes = ProtectForCurrentUser(Encoding.UTF8.GetBytes(settings.Password));
            File.WriteAllBytes(_secretFile, protectedBytes);
        }
        else if (File.Exists(_secretFile))
        {
            File.Delete(_secretFile);
        }
    }

    public void Clear()
    {
        if (File.Exists(_settingsFile)) File.Delete(_settingsFile);
        if (File.Exists(_secretFile)) File.Delete(_secretFile);
    }

    private string LoadPassword()
    {
        try
        {
            if (!File.Exists(_secretFile)) return string.Empty;
            var clear = UnprotectForCurrentUser(File.ReadAllBytes(_secretFile));
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] ProtectForCurrentUser(byte[] clear)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI only works on Windows.");
        using var input = DataBlob.FromBytes(clear);
        var inputValue = input.Value;
        if (!CryptProtectData(ref inputValue, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return output.ToBytes(); }
        finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
    }

    private static byte[] UnprotectForCurrentUser(byte[] encrypted)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI only works on Windows.");
        using var input = DataBlob.FromBytes(encrypted);
        var inputValue = input.Value;
        if (!CryptUnprotectData(ref inputValue, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return output.ToBytes(); }
        finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
    }

    private sealed class PersistedProxySettings
    {
        public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
        public string? HttpProxyUrl { get; set; }
        public string? HttpsProxyUrl { get; set; }
        public bool? UseSameProxyForHttps { get; set; }
        public string? NonProxyHosts { get; set; }
        public string? ProxyUsername { get; set; }
        public bool RememberProxy { get; set; } = true;

        // R29.22-R29.27 kompatibilitás: a régi egycímes beállítás automatikusan migrálódik.
        public string? ProxyUrl { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDataBlob
    {
        public int cbData;
        public IntPtr pbData;

        public byte[] ToBytes()
        {
            if (cbData <= 0 || pbData == IntPtr.Zero) return Array.Empty<byte>();
            var bytes = new byte[cbData];
            Marshal.Copy(pbData, bytes, 0, cbData);
            return bytes;
        }
    }

    private sealed class DataBlob : IDisposable
    {
        public NativeDataBlob Value;

        private DataBlob(byte[] bytes)
        {
            Value.cbData = bytes.Length;
            Value.pbData = Marshal.AllocHGlobal(bytes.Length);
            if (bytes.Length > 0) Marshal.Copy(bytes, 0, Value.pbData, bytes.Length);
        }

        public static DataBlob FromBytes(byte[] bytes) => new(bytes);

        public void Dispose()
        {
            if (Value.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Value.pbData);
                Value.pbData = IntPtr.Zero;
                Value.cbData = 0;
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref NativeDataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out NativeDataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref NativeDataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out NativeDataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
