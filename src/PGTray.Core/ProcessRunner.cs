using System.Diagnostics;
using System.Text;

namespace PGTray.Core;

public sealed class ProcessRunner
{
    internal Action<bool, int>? OutputReadCheckpoint { get; set; }
    internal Func<Process, Task>? PendingExitWait { get; set; }
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
            stdout = PumpAsync(process.StandardOutput.BaseStream, output, false, drain.Token);
            stderr = PumpAsync(process.StandardError.BaseStream, error, true, drain.Token);
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
            var outputError = await CollectOutputAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode, Snapshot(output), Snapshot(error), timedOut) { ProcessExitConfirmed = true, OutputReadError = outputError };
        }
        finally
        {
            if (!transferred) await DisposeAsync().ConfigureAwait(false);
        }

        async Task<CommandResult> ObserveAsync(bool wasTimedOut)
        {
            try
            {
                await (PendingExitWait?.Invoke(process) ?? process.WaitForExitAsync()).ConfigureAwait(false);
                // Output capture has its own outcome; it cannot revoke an observed OS exit.
                var outputError = await CollectOutputAsync().ConfigureAwait(false);
                return new(process.ExitCode, Snapshot(output), Snapshot(error), wasTimedOut) { ProcessExitConfirmed = true, OutputReadError = outputError };
            }
            finally { await DisposeAsync().ConfigureAwait(false); }
        }
        async Task<string?> CollectOutputAsync()
        {
            var readers = Task.WhenAll(stdout, stderr);
            var incomplete = false;
            try { await readers.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                incomplete = true;
                drain.Cancel();
                try { await readers.ConfigureAwait(false); } catch (Exception) when (readers.IsFaulted || readers.IsCanceled) { }
            }
            catch (Exception) when (readers.IsFaulted || readers.IsCanceled) { }
            return readers.Exception is { } failures
                ? string.Join("\r\n", failures.Flatten().InnerExceptions.Select(ex => ex.Message))
                : incomplete ? new TimeoutException().Message : null;
        }
        async Task DisposeAsync()
        {
            drain.Cancel();
            // Observe both readers even when start, exit observation or capture failed.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (Exception) { }
            process.Dispose(); output.Dispose(); error.Dispose(); drain.Dispose();
        }
    }

    private static string Snapshot(MemoryStream stream) { lock (stream) return TextDecoding.Decode(stream.ToArray()); }

    private async Task PumpAsync(Stream reader, MemoryStream destination, bool isError, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            int count;
            do
            {
                count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count > 0) lock (destination) if (destination.Length < 1024 * 1024) destination.Write(buffer, 0, count);
                OutputReadCheckpoint?.Invoke(isError, count);
            } while (count > 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
