using System.Runtime.InteropServices;
using System.Text;

namespace PGTray.Core;

public static class TextDecoding
{
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    // PostgreSQL on Windows may emit the system ANSI codepage, including Chinese logs.
    public static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(OperatingSystem.IsWindows() ? (int)GetACP() : 65001).GetString(bytes);
        }
    }
}
