namespace PGTray.Core;

public enum InitializationFailureKind { Unknown, Encoding, ProgramPathEncoding, Timeout }

public sealed class InitializationFailedException : IOException
{
    // Exact UTF8 diagnostics observed in the Windows build and PostgreSQL's localized catalogs.
    private static readonly string[] Utf8EncodingErrors = [
        "invalid byte sequence for encoding \"UTF8\"",
        "无效的 \"UTF8\" 编码字节顺序",
        "编码 \"UTF8\" 的字节序列无效",
        "編碼 \"UTF8\" 的位元組序列無效",
        "符号化方式\"UTF8\"に対する不正なバイト列",
        "\"UTF8\" 인코딩에서 사용할 수 없는 문자가 있음"
    ];

    public string DataDirectory { get; }
    public string Diagnostic { get; }
    public int? ExitCode { get; }
    public bool? TimedOut { get; }
    public InitializationFailureKind Kind { get; }

    private InitializationFailedException(string message, string data, string diagnostic, CommandResult? result, InitializationFailureKind kind)
        : base(message)
    {
        DataDirectory = data; Diagnostic = diagnostic; ExitCode = result?.ExitCode; TimedOut = result?.TimedOut; Kind = kind;
    }

    internal static InitializationFailedException Create(InitializationPaths paths, string data, CommandResult? result,
        string detail, string password, string? cleanup)
    {
        if (result is { ExitCode: 0, TimedOut: false }) throw new ArgumentException("initdb succeeded", nameof(result));
        var output = result is null ? "" : result.Output + "\n" + result.Error;
        var encodingError = Utf8EncodingErrors.Any(signature => output.Contains(signature, StringComparison.OrdinalIgnoreCase));
        var postBootstrap = output.Contains("post-bootstrap", StringComparison.OrdinalIgnoreCase);
        var kind = result?.TimedOut == true ? InitializationFailureKind.Timeout
            : encodingError && paths.HasNonAscii && postBootstrap ? InitializationFailureKind.ProgramPathEncoding
            : encodingError ? InitializationFailureKind.Encoding : InitializationFailureKind.Unknown;
        var reason = L10n.Get(kind switch {
            InitializationFailureKind.Timeout => "InitializationTimeoutMessage",
            InitializationFailureKind.ProgramPathEncoding => "InitializationPathEncodingMessage",
            InitializationFailureKind.Encoding => "InitializationEncodingMessage",
            _ => "InitializationUnknownMessage"
        });
        var message = reason + "\r\n\r\n" + L10n.Get("InitializationFilesPreserved");
        if (cleanup is not null) message += "\r\n\r\n" + cleanup;
        var diagnostic = L10n.Format("InitializationDiagnosticContext", data) + "\r\n" +
            L10n.Format("InitializationPathDetails", paths.BinDirectory, paths.ShareDirectory ?? L10n.Get("SharePathUnconfirmed")) +
            "\r\n\r\n" + (result?.Diagnostic ?? detail);
        if (cleanup is not null) diagnostic += "\r\n\r\n" + cleanup;
        return new(Redact(message, password), Redact(data, password), Redact(diagnostic, password), result, kind);
    }

    internal static string Redact(string text, string password)
    {
        if (password.Length == 0) return text;
        // PostgreSQL may quote a value in SQL diagnostics. Redact the quoted form first.
        return text.Replace(password.Replace("\\", "\\\\").Replace("'", "''"), "[redacted]", StringComparison.Ordinal)
            .Replace(password.Replace("'", "''"), "[redacted]", StringComparison.Ordinal)
            .Replace(password, "[redacted]", StringComparison.Ordinal);
    }
}
