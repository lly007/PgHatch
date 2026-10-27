using System.Diagnostics;
using System.Text;

namespace PGTray.Core;

public sealed class ProcessRunner
{
    public async Task<CommandResult> RunAsync(string exe, IEnumerable<string> arguments, TimeSpan timeout,
        bool killChildrenOnTimeout = false, CancellationToken cancellationToken = default, bool terminateOnTimeout = true)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Do not let the launching shell's PG* settings redirect our local tools.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        var process = new Process { StartInfo = info };
        var drain = new CancellationTokenSource();
        var output = new MemoryStream(); var error = new MemoryStream();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        var transferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Windows CreateProcess (including antivirus/DLL inspection) is synchronous.
            // Offload that call, while keeping process waiting and both pipe reads truly async.
            await Task.Run(() => process.Start(), cancellationToken).ConfigureAwait(false);
            stdout = PumpAsync(process.StandardOutput.BaseStream, output, drain.Token);
            stderr = PumpAsync(process.StandardError.BaseStream, error, drain.Token);
            var timedOut = false;
            using var deadline = new CancellationTokenSource(timeout);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            try { await process.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                timedOut = deadline.IsCancellationRequested;
                // pg_ctl is only a controller: killing its tree could kill a live database.
                if (terminateOnTimeout)
                {
                    try { process.Kill(killChildrenOnTimeout); } catch (InvalidOperationException) { }
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            if (!terminateOnTimeout && !process.HasExited)
            {
                var result = new CommandResult(-1, Snapshot(output), Snapshot(error), timedOut);
                // Transfer handles and pipe readers instead of detaching an unobserved controller.
                // Completion means natural process exit AND bounded output draining, never a kill.
                transferred = true;
                return result with { PendingCompletion = ObserveAsync(timedOut) };
            }
            // On Windows postgres can inherit pipe handles even when pg_ctl used -l.
            // Controller exit is authoritative for completion; never wait indefinitely for EOF.
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { drain.Cancel(); await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.HasExited ? process.ExitCode : -1, Snapshot(output), Snapshot(error), timedOut);
        }
        finally
        {
            if (!transferred) Dispose();
        }

        async Task<CommandResult> ObserveAsync(bool wasTimedOut)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { drain.Cancel(); await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                return new(process.ExitCode, Snapshot(output), Snapshot(error), wasTimedOut);
            }
            finally { Dispose(); }
        }
        void Dispose() { drain.Cancel(); process.Dispose(); output.Dispose(); error.Dispose(); drain.Dispose(); }
    }

    private static string Snapshot(MemoryStream stream) { lock (stream) return TextDecoding.Decode(stream.ToArray()); }

    private static async Task PumpAsync(Stream reader, MemoryStream destination, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                lock (destination) if (destination.Length < 1024 * 1024) destination.Write(buffer, 0, count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
