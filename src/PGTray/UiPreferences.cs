using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PGTray.Core;

namespace PGTray;

/// <summary>Portable UI preferences, independent of PostgreSQL registration and recovery.</summary>
internal sealed class UiPreferences(string root)
{
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private JsonObject document = new();
    private string? diskText;
    private bool readFailed;
    public string FilePath { get; } = Path.Combine(root, "pghatch.ui.json");
    public string? Language { get; private set; }
    public string? LoadError { get; private set; }

    public static string ResolveLanguage(string? selected, CultureInfo system)
    {
        if (selected is "zh-CN" or "en-US" or "zh-TW") return selected;
        if (system.TwoLetterISOLanguageName != "zh") return "en-US";
        return system.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
            system.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
            system.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
            system.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : "zh-CN";
    }

    public async Task LoadAsync()
    {
        try
        {
            diskText = File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath).ConfigureAwait(false) : null;
            document = diskText is null ? new() : JsonNode.Parse(diskText) as JsonObject ?? throw new InvalidDataException(L10n.Get("PreferenceInvalid"));
            Language = document["Language"]?.GetValue<string>();
            if (Language is not (null or "zh-CN" or "en-US" or "zh-TW")) throw new InvalidDataException(L10n.Get("PreferenceInvalid"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { readFailed = true; LoadError = ex.Message; Language = null; }
    }

    public async Task SaveLanguageAsync(string? language)
    {
        if (language is not (null or "zh-CN" or "en-US" or "zh-TW")) throw new ArgumentException(nameof(language));
        await saveGate.WaitAsync().ConfigureAwait(false);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (readFailed) throw new IOException(L10n.Format("PreferenceBlocked", FilePath));
            var current = File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath).ConfigureAwait(false) : null;
            if (current != diskText) throw new IOException(L10n.Get("PreferenceConflict"));
            var next = (JsonObject)document.DeepClone(); next["Language"] = language;
            var text = next.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporary, text).ConfigureAwait(false);
            // Recheck after asynchronous I/O so an external edit is not silently replaced.
            current = File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath).ConfigureAwait(false) : null;
            if (current != diskText) throw new IOException(L10n.Get("PreferenceConflict"));
            if (diskText is null) File.Move(temporary, FilePath); else File.Replace(temporary, FilePath, null);
            document = next; diskText = text; Language = language;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); saveGate.Release(); }
    }
}
