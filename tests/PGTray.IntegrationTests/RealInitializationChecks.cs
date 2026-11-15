using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PGTray.Core;

internal static class RealInitializationChecks
{
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();

    internal static async Task<int> RunAsync(string source, string parent)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.Combine(Path.GetFullPath(parent), "real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".pghatch-test-owned"), "Issue 7 owns only this new root. Source is read-only; never starts a database.");
        var runner = new ProcessRunner(); var tools = new PostgresTools(runner);
        var original = await tools.InspectBinaryAsync(source);
        var sourceRoot = Directory.GetParent(original.BinDirectory)!.FullName;
        var sourceHashes = Hashes(sourceRoot);
        var programs = new[] { FinalReviewTests.CopyProgram(sourceRoot, Path.Combine(root, "program-ascii")), FinalReviewTests.CopyProgram(sourceRoot, Path.Combine(root, "program-中文")) };
        var count = 0;
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); count++; }
        async Task<Exception> Reject(Func<Task> action, string text)
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { Check(true, text); return ex; }
            throw new Exception("Expected rejection: " + text);
        }
        foreach (var bin in programs)
            Check(Equal(sourceHashes, Hashes(Directory.GetParent(bin)!.FullName)), "All bin/lib/share SHA-256 hashes match the read-only source: " + bin);
        var version = await tools.RunAsync(original, "postgres", ["--version"]);
        var cases = new List<object>();
        var password = "issue7-" + Guid.NewGuid().ToString("N");
        var credentialDirectory = Path.Combine(root, "credential");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.CreateDirectory(security, credentialDirectory);
        var secret = Path.Combine(credentialDirectory, "password");
        await File.WriteAllTextAsync(secret, password, new UTF8Encoding(false));
        var retainSecret = false;
        try
        {
            foreach (var test in new[] {
                (Name: "ascii-program-ascii-data", Bin: programs[0], Data: "data-ascii"),
                (Name: "nonascii-program-ascii-data", Bin: programs[1], Data: "data-ascii"),
                (Name: "ascii-program-nonascii-data", Bin: programs[0], Data: "数据目录") })
            {
                var binary = await tools.InspectBinaryAsync(test.Bin);
                var paths = await tools.InspectInitializationPathsAsync(binary);
                Check(paths.ShareDirectory == Path.Combine(Directory.GetParent(test.Bin)!.FullName, "share"), "Real pg_config confirms expanded share without changing execution paths");
                var data = Path.Combine(root, test.Data, test.Name + "-direct");
                var info = new ProcessStartInfo(Path.Combine(binary.BinDirectory, "initdb.exe")) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
                foreach (var arg in new[] { "-D", data, "-U", "issue7_user", "--pwfile", secret, "--auth-local=scram-sha-256", "--auth-host=scram-sha-256", "--encoding=UTF8", "--no-clean" }) info.ArgumentList.Add(arg);
                var captured = await FinalReviewTests.RunOwnedInitializationAsync(info, password, TimeSpan.FromMinutes(2));
                var result = new CommandResult(captured.ExitCode, TextDecoding.Decode(captured.Output), TextDecoding.Decode(captured.Error), false);
                var diagnostic = InitializationFailedException.Redact(result.Diagnostic, password);
                await File.WriteAllTextAsync(Path.Combine(root, test.Name + "-direct.txt"), diagnostic);
                cases.Add(new { test.Name, Invocation = "direct initdb", binary.Version, binary.BinDirectory, paths.ShareDirectory, PGDATA = data, result.ExitCode, result.TimedOut,
                    Success = result.ExitCode == 0, HasPgControl = File.Exists(Path.Combine(data, "global", "pg_control")) });
                Check(result.ExitCode == 0 || result.ExitCode == 1, "Real initdb completed with observed OS exit code " + result.ExitCode);
                var wrappedData = Path.Combine(root, test.Data, test.Name + "-pghatch");
                var failed = false;
                try
                {
                    await tools.InitializeAsync(binary, wrappedData, InitializationDiagnosticsTests.Port(), "issue7_user", password);
                    Check(result.ExitCode == 0, "Normal InitializeAsync succeeds consistently with direct initdb");
                    cases.Add(new { test.Name, Invocation = "PgHatch InitializeAsync", binary.Version, binary.BinDirectory, paths.ShareDirectory, PGDATA = wrappedData, Success = true });
                }
                catch (InitializationFailedException ex)
                {
                    failed = true;
                    Check(result.ExitCode != 0 && ex.ExitCode == result.ExitCode && ex.TimedOut == false && !ex.Diagnostic.Contains(password), "Normal InitializeAsync retains real failure code and redacts diagnostics");
                    Check(ex.Kind == InitializationFailureKind.ProgramPathEncoding && paths.HasNonAscii, "Real post-bootstrap UTF8 error gets path compatibility guidance");
                    await File.WriteAllTextAsync(Path.Combine(root, test.Name + "-pghatch.txt"), ex.Message + "\n\n" + ex.Diagnostic);
                    cases.Add(new { test.Name, Invocation = "PgHatch InitializeAsync", binary.Version, binary.BinDirectory, paths.ShareDirectory, PGDATA = wrappedData, Success = false, ex.ExitCode, ex.TimedOut, Kind = ex.Kind.ToString() });
                }
                Check(Directory.Exists(wrappedData) && !File.Exists(Path.Combine(wrappedData, "postmaster.pid")), "Data directory is retained and no server started; failed=" + failed);
                Console.WriteLine("OBSERVED: " + test.Name + "; direct exit=" + result.ExitCode + "; PgHatch failure=" + failed);
            }
            await ReliabilityTests.InitializationFailures(root, await tools.InspectBinaryAsync(programs[0]), Check, Reject);
            Check(Equal(sourceHashes, Hashes(sourceRoot)), "Read-only source program hashes remain unchanged");
            foreach (var bin in programs)
                Check(Equal(sourceHashes, Hashes(Directory.GetParent(bin)!.FullName)), "Isolated program hashes remain unchanged after experiments");
            Check(!Directory.GetFiles(root, "postmaster.pid", SearchOption.AllDirectories).Any(), "No PostgreSQL server was started in this suite");
            await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(new {
                FullVersion = version.Output.Trim(), original.Version, Source = original.BinDirectory, Windows = Environment.OSVersion.VersionString,
                AnsiCodePage = GetACP(), OemCodePage = GetOEMCP(), ConsoleCodePage = Console.OutputEncoding.CodePage,
                Environment = new[] { "LANG", "LC_ALL", "LC_CTYPE", "LC_MESSAGES" }.ToDictionary(k => k, Environment.GetEnvironmentVariable),
                VerifiedProgramFiles = sourceHashes.Count, SourceHashes = sourceHashes, Cases = cases
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{count} PASS; real PostgreSQL matrix plus explicitly injected post-initdb/configuration/cleanup failures. {root}"); return 0;
        }
        catch (FinalReviewTests.OwnedInitializationException ex)
        {
            retainSecret = !ex.CleanupCompleted; throw;
        }
        finally
        {
            if (!retainSecret) { File.Delete(secret); Directory.Delete(credentialDirectory); }
            else Console.Error.WriteLine("Owned process cleanup is incomplete. Credential location retained: " + secret);
        }
    }

    private static Dictionary<string, string> Hashes(string program) => new[] { "bin", "lib", "share" }
        .SelectMany(name => Directory.GetFiles(Path.Combine(program, name), "*", SearchOption.AllDirectories))
        .ToDictionary(file => Path.GetRelativePath(program, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

    private static bool Equal(Dictionary<string, string> expected, Dictionary<string, string> actual)
        => expected.Count == actual.Count && expected.All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value);
}
