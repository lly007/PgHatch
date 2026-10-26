using System.Diagnostics;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class CreationChecks
{
    public static int Run(string binaries, string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "creation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".pghatch-test-owned"), "Real initdb responsiveness test; never starts a server.");
        var manager = new InstanceManager(new ConfigStore(root), new PostgresTools(new ProcessRunner()));
        using var form = new Form { Text = "PgHatch real initdb responsiveness check", ClientSize = new(640, 220), StartPosition = FormStartPosition.CenterScreen };
        var label = new Label { Dock = DockStyle.Fill, AutoSize = false }; form.Controls.Add(label);
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 20 };
        var clock = new Stopwatch(); long last = 0, maxGap = 0; var ticks = 0; var result = 1;
        heartbeat.Tick += (_, _) => { var now = clock.ElapsedMilliseconds; maxGap = Math.Max(maxGap, now - last); last = now; ticks++; label.Text = "initdb running; UI heartbeat " + ticks; };
        form.Shown += async (_, _) => {
            try
            {
                await manager.LoadAsync(); var binary = await manager.AddBinaryAsync(binaries);
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                clock.Start(); heartbeat.Start();
                var creating = manager.AddInstanceAsync("UI heartbeat", binary, Path.Combine(root, "data", "heartbeat"), port, "pgtray_test", "test-" + Guid.NewGuid().ToString("N"));
                var prefix = clock.ElapsedMilliseconds;
                var shutdown = manager.ShutdownAsync();
                if (shutdown.IsCompleted) throw new Exception("Shutdown did not wait for active initialization.");
                var instance = await creating;
                await shutdown;
                maxGap = Math.Max(maxGap, clock.ElapsedMilliseconds - last); heartbeat.Stop();
                Console.WriteLine($"REAL INITDB: synchronous prefix={prefix}ms; duration={clock.ElapsedMilliseconds}ms; UI ticks={ticks}; maximum heartbeat gap={maxGap}ms");
                if (prefix > 500 || maxGap > 1000 || (clock.ElapsedMilliseconds >= 300 && ticks < 3)) throw new Exception("UI heartbeat was blocked during real initialization.");
                if (!File.Exists(Path.Combine(instance.DataDirectory, "PG_VERSION")) || manager.Config.Instances.Count != 1 || File.Exists(Path.Combine(instance.DataDirectory, "postmaster.pid"))) throw new Exception("Initialization/registration mismatch or unexpected server.");
                Console.WriteLine("PASS: Real initdb and registration succeeded; server was never started. " + root);
                Console.WriteLine("PASS: Shutdown waited for initialization and configuration commit without interrupting initdb.");
                var runner = new ProcessRunner();
                var pressure = await runner.RunAsync(Environment.ProcessPath!, ["--pipe-burst"], TimeSpan.FromSeconds(15), killChildrenOnTimeout: true);
                if (pressure.ExitCode != 7 || pressure.Output.Length < 1000000 || pressure.Error.Length < 1000000 || pressure.TimedOut) throw new Exception("Concurrent pipe draining lost output or deadlocked.");
                Console.WriteLine("PASS: Concurrent stdout/stderr exceed pipe capacity; diagnostics and exit code are retained.");
                var timed = await runner.RunAsync(Environment.ProcessPath!, ["--delay-helper"], TimeSpan.FromMilliseconds(200), killChildrenOnTimeout: true);
                if (!timed.TimedOut || timed.ExitCode == 0) throw new Exception("Timeout did not stop and reap the child.");
                Console.WriteLine("PASS: Timed out process was stopped and awaited safely.");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { heartbeat.Stop(); await manager.ShutdownAsync(); form.Close(); }
        };
        Application.Run(form); return result;
    }
}
