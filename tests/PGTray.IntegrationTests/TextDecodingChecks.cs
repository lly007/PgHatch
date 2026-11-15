using System.Runtime.InteropServices;
using System.Text;
using PGTray.Core;

internal static class TextDecodingChecks
{
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    internal static byte[] MixedSample()
    {
        // Known CP936 bytes for Chinese characters, next to a UTF-8 localized line.
        return [.. Encoding.UTF8.GetBytes("UTF8 中文\n"), 0xd6, 0xd0, 0xce, 0xc4, (byte)'\n',
            .. Encoding.ASCII.GetBytes("performing post-bootstrap initialization ...\ninvalid byte sequence for encoding \"UTF8\": 0xd6 0xd0\n")];
    }

    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); count++; }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var acp = (int)GetACP();
        Check(TextDecoding.Decode(Encoding.UTF8.GetBytes("UTF8 中文 日本語 한국어 😀")) == "UTF8 中文 日本語 한국어 😀", "Strict UTF-8 preserves multilingual output");
        byte[] cp936 = [0xd6, 0xd0, 0xce, 0xc4];
        Check(Encoding.GetEncoding(936).GetString(cp936) == "中文", "Known CP936 byte sample is verified independently");
        Check(TextDecoding.Decode(cp936) == Encoding.GetEncoding(acp).GetString(cp936), "Invalid UTF-8 falls back to the actual Windows ACP " + acp);
        if (acp == 936) Check(TextDecoding.Decode(cp936) == "中文", "Windows ACP936 fallback correctly displays Chinese bytes");
        var mixed = TextDecoding.Decode(MixedSample());
        Check(mixed == Encoding.GetEncoding(acp).GetString(MixedSample()) && mixed.Contains("invalid byte sequence for encoding \"UTF8\""), "Mixed stream uses whole-stream ANSI fallback and retains ASCII error signature");
        Check(!mixed.Contains("UTF8 中文\n") || acp == 65001, "Mixed UTF-8/CP936 has a documented localized-text decoding boundary");
        var result = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--mixed-encoding-helper"], TimeSpan.FromSeconds(10));
        Check(result.ExitCode == 1 && !result.TimedOut && result.Output == mixed && result.Error == mixed, "Raw-byte process capture retains both mixed streams and true exit 1");
        var error = InitializationFailedException.Create(new(@"C:\程序\bin", null), @"C:\data", result, "", "fixture-secret", null);
        Check(error.Kind == InitializationFailureKind.ProgramPathEncoding && error.ExitCode == 1 && error.TimedOut == false, "Known encoding failure remains recognizable despite mixed localized text");
        Console.WriteLine($"{count} PASS; known-byte and synthetic child-process checks; ACP={acp}. No PostgreSQL reproduction in this suite."); return 0;
    }
}
