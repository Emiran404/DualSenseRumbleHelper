using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DualSenseRumbleHelper.Services;

/// <summary>Which XInput DLL the game binary loads and how we found out.</summary>
public sealed record ProxyAnalysis(string DllName, string Method, IReadOnlyList<string> AllReferences);

public sealed record ProxyInstallState(bool Installed, string? DllName, bool OriginalBackedUp, string Note);

/// <summary>Mode 3: installs / removes the embedded XInput proxy DLL in the game's Binaries\Win64 folder.</summary>
public static class ProxyInstaller
{
    public const string MarkerFileName = "DualSenseRumbleHelper.proxy.json";
    private const string DefaultDllName = "xinput1_3.dll";

    private sealed class Marker
    {
        public string DllName { get; set; } = "";
        public string? OriginalBackupName { get; set; }
        public string? OriginalSha256 { get; set; }
        public string ProxySha256 { get; set; } = "";
        public DateTime InstalledAt { get; set; }
    }

    private static byte[]? _proxyBytes;
    private static string? _proxySha;

    public static byte[] GetEmbeddedProxy()
    {
        if (_proxyBytes != null) return _proxyBytes;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("xinput_proxy.dll")
                      ?? throw new InvalidOperationException(T("proxy.err.embedded"));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        _proxyBytes = ms.ToArray();
        return _proxyBytes;
    }

    public static string ProxySha256 => _proxySha ??= Sha256(GetEmbeddedProxy());

    /// <summary>Reads the PE import, delay-load and string tables of the game exe to decide the proxy's file name.</summary>
    public static ProxyAnalysis Analyze(string exePath)
    {
        var refs = new List<(string Name, string Method)>();
        byte[] b = File.ReadAllBytes(exePath);
        try
        {
            int pe = BitConverter.ToInt32(b, 0x3C);
            int nsec = BitConverter.ToUInt16(b, pe + 6);
            int optsz = BitConverter.ToUInt16(b, pe + 20);
            int opt = pe + 24;
            ushort magic = BitConverter.ToUInt16(b, opt);
            int dd = magic == 0x20B ? opt + 112 : opt + 96;
            int secTable = opt + optsz;
            var secs = new List<(uint va, uint vsz, uint raw)>();
            for (int i = 0; i < nsec; i++)
            {
                int s = secTable + i * 40;
                secs.Add((BitConverter.ToUInt32(b, s + 12), BitConverter.ToUInt32(b, s + 8), BitConverter.ToUInt32(b, s + 20)));
            }
            int RvaToOffset(uint rva)
            {
                foreach (var s in secs) if (rva >= s.va && rva < s.va + Math.Max(s.vsz, 1)) return (int)(rva - s.va + s.raw);
                return -1;
            }
            string ReadAscii(int off)
            {
                var sb = new StringBuilder();
                while (off >= 0 && off < b.Length && b[off] != 0 && sb.Length < 260) sb.Append((char)b[off++]);
                return sb.ToString();
            }

            // Import directory (index 1)
            uint impRva = BitConverter.ToUInt32(b, dd + 8);
            if (impRva != 0)
            {
                int off = RvaToOffset(impRva);
                while (off > 0 && off + 20 <= b.Length)
                {
                    uint nameRva = BitConverter.ToUInt32(b, off + 12);
                    if (nameRva == 0) break;
                    refs.Add((ReadAscii(RvaToOffset(nameRva)), "import"));
                    off += 20;
                }
            }
            // Delay-load directory (index 13)
            uint dlRva = BitConverter.ToUInt32(b, dd + 13 * 8);
            if (dlRva != 0)
            {
                int off = RvaToOffset(dlRva);
                while (off > 0 && off + 32 <= b.Length)
                {
                    uint nameRva = BitConverter.ToUInt32(b, off + 4);
                    if (nameRva == 0) break;
                    refs.Add((ReadAscii(RvaToOffset(nameRva)), "delay"));
                    off += 32;
                }
            }
        }
        catch { }

        // Plain strings (LoadLibrary at run time)
        string ascii = Encoding.Latin1.GetString(b);
        foreach (Match m in Regex.Matches(ascii, @"(?i)xinput(1_[1-4]|9_1_0)\.dll")) refs.Add((m.Value, "string"));
        string uni = Encoding.Unicode.GetString(b);
        foreach (Match m in Regex.Matches(uni, @"(?i)xinput(1_[1-4]|9_1_0)\.dll")) refs.Add((m.Value, "string"));

        var xinputRefs = refs.Where(r => r.Name.StartsWith("xinput", StringComparison.OrdinalIgnoreCase)).ToList();
        var all = xinputRefs.Select(r => $"{r.Name.ToLowerInvariant()} [{r.Method}]").Distinct().ToList();
        var pick = xinputRefs.FirstOrDefault(r => r.Method == "import");
        if (pick.Name == null) pick = xinputRefs.FirstOrDefault(r => r.Method == "delay");
        if (pick.Name == null) pick = xinputRefs.FirstOrDefault();
        if (pick.Name == null) return new ProxyAnalysis(DefaultDllName, "default", all);
        return new ProxyAnalysis(pick.Name.ToLowerInvariant(), pick.Method, all);
    }

    /// <summary>Human-readable name of a reference kind ("import", "delay", "string", "default").</summary>
    public static string DescribeMethod(string method) => T("proxy.method." + method);

    /// <summary>"xinput1_3.dll [delay]" → "xinput1_3.dll [delay-load]" in the UI language.</summary>
    public static string DescribeReference(string reference)
    {
        int i = reference.LastIndexOf(" [", StringComparison.Ordinal);
        return i > 0 && reference.EndsWith(']') ? $"{reference[..i]} [{DescribeMethod(reference[(i + 2)..^1])}]" : reference;
    }

    public static string BackupNameFor(string dllName) => Path.GetFileNameWithoutExtension(dllName) + "_original.dll";

    public static ProxyInstallState GetState(string binDir, string expectedDllName)
    {
        string markerPath = Path.Combine(binDir, MarkerFileName);
        if (File.Exists(markerPath))
        {
            try
            {
                var m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath));
                if (m != null)
                {
                    string target = Path.Combine(binDir, m.DllName);
                    bool ours = File.Exists(target) && Sha256(File.ReadAllBytes(target)) == ProxySha256;
                    string note = ours ? T("proxy.st.installed") : T("proxy.st.changed");
                    return new ProxyInstallState(true, m.DllName, m.OriginalBackupName != null, note);
                }
            }
            catch { }
        }
        string t = Path.Combine(binDir, expectedDllName);
        if (File.Exists(t))
        {
            try
            {
                if (Sha256(File.ReadAllBytes(t)) == ProxySha256)
                    return new ProxyInstallState(true, expectedDllName, File.Exists(Path.Combine(binDir, BackupNameFor(expectedDllName))), T("proxy.st.installedNoMarker"));
            }
            catch { }
        }
        return new ProxyInstallState(false, null, false, T("common.notInstalled"));
    }

    public static bool IsDirectoryWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".rumblehelper_write_test_" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Install in-process (requires write access to the folder).</summary>
    public static string InstallCore(string binDir, string dllName)
    {
        if (!Directory.Exists(binDir)) throw new DirectoryNotFoundException(binDir);
        string target = Path.Combine(binDir, dllName);
        string markerPath = Path.Combine(binDir, MarkerFileName);
        if (File.Exists(markerPath)) throw new InvalidOperationException(T("proxy.err.already"));

        var marker = new Marker { DllName = dllName, ProxySha256 = ProxySha256, InstalledAt = DateTime.Now };
        if (File.Exists(target))
        {
            byte[] existing = File.ReadAllBytes(target);
            string sha = Sha256(existing);
            if (sha != ProxySha256)
            {
                string backup = Path.Combine(binDir, BackupNameFor(dllName));
                if (File.Exists(backup))
                {
                    if (Sha256(File.ReadAllBytes(backup)) != sha)
                        throw new InvalidOperationException(F("proxy.err.backupDiffers", Path.GetFileName(backup)));
                    File.Delete(target);
                }
                else
                {
                    File.Move(target, backup);
                }
                marker.OriginalBackupName = Path.GetFileName(backup);
                marker.OriginalSha256 = sha;
            }
            else
            {
                File.Delete(target);
            }
        }
        File.WriteAllBytes(target, GetEmbeddedProxy());
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        return marker.OriginalBackupName != null
            ? F("proxy.installedBackup", dllName, marker.OriginalBackupName)
            : F("proxy.installedNoOriginal", dllName);
    }

    /// <summary>Uninstall in-process, restoring the original file if we backed one up.</summary>
    public static string UninstallCore(string binDir)
    {
        string markerPath = Path.Combine(binDir, MarkerFileName);
        Marker? marker = null;
        if (File.Exists(markerPath))
        {
            try { marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath)); } catch { }
        }
        string dllName = marker?.DllName ?? DefaultDllName;
        string target = Path.Combine(binDir, dllName);
        string backup = Path.Combine(binDir, marker?.OriginalBackupName ?? BackupNameFor(dllName));
        var notes = new List<string>();

        bool targetIsOurs = File.Exists(target) && Sha256(File.ReadAllBytes(target)) == ProxySha256;
        if (targetIsOurs) { File.Delete(target); notes.Add(F("proxy.un.deleted", dllName)); }
        else if (File.Exists(target)) notes.Add(F("proxy.un.notOurs", dllName));

        if (File.Exists(backup))
        {
            if (!File.Exists(target))
            {
                File.Move(backup, target);
                notes.Add(F("proxy.un.restored", dllName));
            }
            else
            {
                // Game files were re-verified meanwhile; the backup is a duplicate of the original.
                bool dup = marker?.OriginalSha256 == null || Sha256(File.ReadAllBytes(backup)) == marker.OriginalSha256;
                if (dup) { File.Delete(backup); notes.Add(T("proxy.un.dupDeleted")); }
                else notes.Add(F("proxy.un.backupKept", Path.GetFileName(backup)));
            }
        }
        if (File.Exists(markerPath)) File.Delete(markerPath);
        if (notes.Count == 0) notes.Add(T("proxy.un.nothing"));
        return string.Join(" ", notes);
    }

    /// <summary>Runs install/uninstall, elevating via UAC only when the folder is not writable.</summary>
    public static string Install(string binDir, string dllName) => RunMaybeElevated(true, binDir, dllName);
    public static string Uninstall(string binDir) => RunMaybeElevated(false, binDir, "");

    private static string RunMaybeElevated(bool install, string binDir, string dllName)
    {
        if (IsDirectoryWritable(binDir))
            return install ? InstallCore(binDir, dllName) : UninstallCore(binDir);

        string resultFile = Path.Combine(Path.GetTempPath(), "DualSenseRumbleHelper_" + Guid.NewGuid().ToString("N") + ".txt");
        string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            ArgumentList = { install ? "--proxy-install" : "--proxy-uninstall", binDir, dllName, resultFile },
        };
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException(T("proxy.err.elevate")); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException(T("proxy.err.uacDenied")); }
        p.WaitForExit();
        string msg = File.Exists(resultFile) ? File.ReadAllText(resultFile) : T("proxy.err.noResult");
        try { File.Delete(resultFile); } catch { }
        if (p.ExitCode != 0) throw new InvalidOperationException(msg);
        return msg;
    }

    /// <summary>Entry point of the elevated helper process.</summary>
    public static int WorkerMain(string[] args)
    {
        // args: --proxy-install|--proxy-uninstall <binDir> <dllName> <resultFile>
        string resultFile = args.Length > 3 ? args[3] : "";
        try
        {
            if (args.Length < 4) throw new ArgumentException("Missing arguments.");
            string msg = args[0] == "--proxy-install" ? InstallCore(args[1], args[2]) : UninstallCore(args[1]);
            File.WriteAllText(resultFile, msg);
            return 0;
        }
        catch (Exception ex)
        {
            try { if (resultFile.Length > 0) File.WriteAllText(resultFile, ex.Message); } catch { }
            return 1;
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
