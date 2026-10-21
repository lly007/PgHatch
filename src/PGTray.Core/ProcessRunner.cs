using System.Diagnostics;
using System.Text;

namespace PGTray.Core;

public sealed class ProcessRunner
{
    public async Task<CommandResult> RunAsync(string exe, IEnumerable<string> arguments, TimeSpan timeout,
        bool killChildrenOnTimeout = false, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Do not let the launching shell's PG* settings redirect our local tools.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        using var process = new Process { StartInfo = info };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using var drain = new CancellationTokenSource();
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var stdout = PumpAsync(process.StandardOutput.BaseStream, output, drain.Token);
        var stderr = PumpAsync(process.StandardError.BaseStream, error, drain.Token);
        var timedOut = false;
        using var deadline = new CancellationTokenSource(timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try { await process.WaitForExitAsync(stop.Token); }
        catch (OperationCanceledException)
        {
            timedOut = deadline.IsCancellationRequested;
            // pg_ctl is only a controller: killing its tree could kill a live database.
            try { process.Kill(killChildrenOnTimeout); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
        }
        // On Windows postgres can inherit pipe handles even when pg_ctl used -l.
        // Controller exit is authoritative for completion; never wait indefinitely for EOF.
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { drain.Cancel(); await Task.WhenAll(stdout, stderr); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, TextDecoding.Decode(output.ToArray()), TextDecoding.Decode(error.ToArray()), timedOut);
    }

    private static async Task PumpAsync(Stream reader, MemoryStream destination, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                if (destination.Length < 1024 * 1024) destination.Write(buffer, 0, count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
