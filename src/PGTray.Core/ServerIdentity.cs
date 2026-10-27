using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PGTray.Core;

// Fail closed: PID file, creation time, executable and the server's -D argument must agree.
internal static class ServerIdentity
{
    internal sealed record Snapshot(int Pid, long Started, string Data, string Image);
    internal static bool SamePath(string a, string b) => string.Equals(PathSafety.Identity(a), PathSafety.Identity(b), StringComparison.OrdinalIgnoreCase);

    internal static Snapshot ConfirmRunning(InstanceConfig instance, BinaryInstallation original)
    {
        // Unlike SamePath(path, Normalize(path)), this checks reparse points rather than
        // resolving both sides to the same target. The manager also pins the loaded identity.
        instance = instance with { DataDirectory = PathSafety.UnlinkedDataIdentity(instance.DataDirectory) };
        var lines = ReadPid(instance.DataDirectory);
        if (lines.Length < 4 || !int.TryParse(lines[0], out var pid) || pid <= 0 ||
            !long.TryParse(lines[2], out var started) || !SamePath(lines[1], instance.DataDirectory)) return Fail<Snapshot>();
        using var process = Process.GetProcessById(pid);
        var identity = Inspect(process);
        if (identity.Auxiliary || !SamePath(identity.Image, Path.Combine(original.BinDirectory, "postgres.exe")) ||
            identity.Data is null || !Path.IsPathFullyQualified(identity.Data) || !SamePath(identity.Data, instance.DataDirectory) || Math.Abs(identity.Started - started) > 2) Fail();
        EnsureNoOtherServer(instance.DataDirectory, pid);
        if (!lines.Take(4).SequenceEqual(ReadPid(instance.DataDirectory).Take(4)) || process.HasExited) Fail();
        return new(pid, started, PathSafety.Identity(instance.DataDirectory), identity.Image);
    }

    internal static void ConfirmStopped(string data, Func<string, FileAttributes?>? attributes = null)
    {
        attributes ??= PathSafety.Attributes;
        var directory = attributes(data);
        if (directory is null || !directory.Value.HasFlag(FileAttributes.Directory)) Fail();
        data = PathSafety.UnlinkedDataIdentity(data, attributes: attributes);
        var version = File.ReadAllText(Path.Combine(data, "PG_VERSION")).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+(\.\d+)?$") ||
            attributes(Path.Combine(data, "global", "pg_control")) is not { } control || control.HasFlag(FileAttributes.Directory)) Fail();
        // A remaining PID file is uncertainty, even when pg_ctl reports exit code 3.
        // Attribute lookup distinguishes a missing file from an inaccessible file.
        if (attributes(Path.Combine(data, "postmaster.pid")) is not null) Fail();
        EnsureNoOtherServer(data, null);
    }

    private static string[] ReadPid(string data) => TextDecoding.Decode(File.ReadAllBytes(Path.Combine(data, "postmaster.pid")))
        .Split('\n').Select(s => s.TrimEnd('\r')).ToArray();

    private static void EnsureNoOtherServer(string data, int? allowedPid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        foreach (var process in Process.GetProcessesByName("postgres"))
        {
            using (process)
            {
                if (process.Id == allowedPid) continue;
                try
                {
                    var identity = Inspect(process);
                    if (!identity.Auxiliary && (identity.Data is null || !Path.IsPathFullyQualified(identity.Data) || SamePath(identity.Data, data))) Fail();
                }
                catch (Exception) when (process.HasExited) { }
                // Inaccessible live postgres processes cannot safely be ruled out.
            }
        }
    }

    private static (string Image, string? Data, long Started, bool Auxiliary) Inspect(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var handle = OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid) return Fail<(string, string?, long, bool)>();
        var image = new System.Text.StringBuilder(32768); var size = image.Capacity;
        if (!QueryFullProcessImageName(handle, 0, image, ref size)) return Fail<(string, string?, long, bool)>();
        // ProcessCommandLineInformation is read without injecting code into the target.
        NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1024 * 1024) return Fail<(string, string?, long, bool)>();
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, needed, out _) != 0) return Fail<(string, string?, long, bool)>();
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var command = Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
            var argv = CommandLineToArgvW(command, out var count);
            if (argv == IntPtr.Zero) return Fail<(string, string?, long, bool)>();
            try
            {
                string? data = null;
                var auxiliary = false;
                for (var i = 1; i < count; i++)
                {
                    var arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
                    if (arg.StartsWith("--forkchild=") || arg is "--version" or "-V" or "-C" or "--describe-config") auxiliary = true;
                    if (arg is "-D" or "--pgdata" && i + 1 < count) data = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, ++i * IntPtr.Size));
                    else if (arg.StartsWith("--pgdata=")) data = arg[9..];
                    else if (arg.StartsWith("-D") && arg.Length > 2) data = arg[2..];
                }
                return (image.ToString(), data, new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds(), auxiliary);
            }
            finally { LocalFree(argv); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Fail() => throw new IOException(L10n.Get("EmergencyIdentityUnconfirmed"));
    private static T Fail<T>() { Fail(); return default!; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information, int length, out int needed);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
