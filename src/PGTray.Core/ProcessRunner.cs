using System.Diagnostics;
using System.Text;

namespace PGTray.Core;

public sealed class ProcessRunner
{
    internal Action<bool, int>? OutputReadCheckpoint { get; set; }
    internal Func<Process, Task>? PendingExitWait { get; set; }
    internal Func<Stream, bool, Stream>? OutputStream { get; set; }
    internal Action? ResourceCleanupCheckpoint { get; set; }
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
        Stream? outputPipe = null, errorPipe = null;
        StreamReader? outputReader = null, errorReader = null;
        Task? closingPipes = null;
        Task<string?>? cleanup = null;
        var captureProblems = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Windows CreateProcess (including antivirus/DLL inspection) is synchronous.
            // Offload that call, while keeping process waiting and both pipe reads truly async.
            await Task.Run(() => process.Start(), cancellationToken).ConfigureAwait(false);
            outputReader = process.StandardOutput; errorReader = process.StandardError;
            outputPipe = OutputStream?.Invoke(outputReader.BaseStream, false) ?? outputReader.BaseStream;
            errorPipe = OutputStream?.Invoke(errorReader.BaseStream, true) ?? errorReader.BaseStream;
            stdout = PumpAsync(outputPipe, output, false, drain.Token);
            stderr = PumpAsync(errorPipe, error, true, drain.Token);
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
            await CollectOutputAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await CompletedResultAsync(timedOut).ConfigureAwait(false);
        }
        finally
        {
            if (!transferred && cleanup is null)
            {
                try { await BeginCleanup().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { /* The cleanup task owns all buffers until readers complete. */ }
            }
        }

        async Task<CommandResult> ObserveAsync(bool wasTimedOut)
        {
            try
            {
                await (PendingExitWait?.Invoke(process) ?? process.WaitForExitAsync()).ConfigureAwait(false);
                // Output capture has its own outcome; it cannot revoke an observed OS exit.
                await CollectOutputAsync().ConfigureAwait(false);
                return await CompletedResultAsync(wasTimedOut).ConfigureAwait(false);
            }
            finally
            {
                // Exit-observation failure still leaves the manager's lifecycle fence held.
                // Resource cleanup has a separate bounded foreground wait and never kills.
                if (cleanup is null)
                    try { await BeginCleanup().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch (TimeoutException) { }
            }
        }
        async Task<CommandResult> CompletedResultAsync(bool wasTimedOut)
        {
            // Capture before handing buffers to cleanup. No buffer is disposed while a pump uses it.
            var code = process.ExitCode;
            var capturedOutput = Snapshot(output); var capturedError = Snapshot(error);
            var reclaim = BeginCleanup();
            try { await reclaim.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { captureProblems.Enqueue(L10n.Get("OutputCleanupDeferred")); }
            var problems = captureProblems.ToArray();
            return new(code, capturedOutput, capturedError, wasTimedOut) {
                ProcessExitConfirmed = true,
                OutputReadError = problems.Length == 0 ? null : string.Join("\r\n", problems),
                ResourceCleanup = reclaim
            };
        }
        void ClosePipes()
        {
            if (closingPipes is not null) return;
            // Closing our pipe handles interrupts native reads even if descendants retain writers.
            // Never close a destination buffer here. A stuck custom/native reader owns it until exit.
            // Cancellation and each close proceed independently: a stuck callback/close on
            // one pipe must not prevent the other handle from being closed.
            closingPipes = Task.WhenAll(new Action[] { () => drain.Cancel(), () => outputPipe?.Dispose(), () => errorPipe?.Dispose() }
                .Select(close => Task.Run(() => { try { close(); } catch (Exception ex) { captureProblems.Enqueue(ex.Message); } })));
        }
        async Task CollectOutputAsync()
        {
            var readers = Task.WhenAll(stdout, stderr);
            try { await readers.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                captureProblems.Enqueue(new TimeoutException().Message);
                ClosePipes();
                try { await readers.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { captureProblems.Enqueue(L10n.Get("OutputCleanupDeferred")); }
                catch (Exception) when (readers.IsFaulted || readers.IsCanceled) { }
            }
            catch (Exception) when (readers.IsFaulted || readers.IsCanceled) { }
            if (readers.Exception is { } failures)
                foreach (var ex in failures.Flatten().InnerExceptions) captureProblems.Enqueue(ex.Message);
        }
        // Always schedule cleanup off the foreground, including its synchronous Dispose calls.
        Task<string?> BeginCleanup() => cleanup ??= Task.Run(ReclaimAsync);
        async Task<string?> ReclaimAsync()
        {
            ClosePipes();
            // This continuation can outlive RunAsync, but only holds our own process/pipe resources.
            // All exceptions are observed; the result exposes eventual cleanup diagnostics separately.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception ex) { captureProblems.Enqueue(ex.Message); }
            await closingPipes!.ConfigureAwait(false);
            foreach (Action release in new Action[] {
                () => ResourceCleanupCheckpoint?.Invoke(),
                () => outputReader?.Dispose(), () => errorReader?.Dispose(),
                () => process.Dispose(), () => output.Dispose(), () => error.Dispose(), () => drain.Dispose() })
                try { release(); } catch (Exception ex) { captureProblems.Enqueue(ex.Message); }
            var problems = captureProblems.ToArray();
            return problems.Length == 0 ? null : string.Join("\r\n", problems);
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
