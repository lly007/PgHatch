using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PGTray.Core;

public sealed record PostgresPackage(string Version, int Revision, string Architecture, string Provider,
    Uri DownloadUrl, string SHA256, Uri? ChecksumSource)
{
    /// <summary>A downloader must apply this guard before each request and to every redirect/final URI.</summary>
    public void ValidateDownloadLocation(Uri location) => PostgresCatalog.ValidateDownloadLocation(location, Version, Revision);
}

public sealed record PostgresCatalog(Uri UpdateUrl, TimeSpan UpdateInterval, IReadOnlyList<PostgresPackage> Packages)
{
    public const int MaxXmlBytes = 1024 * 1024;

    public static PostgresCatalog Parse(byte[] xml)
    {
        try
        {
            if (xml.Length > MaxXmlBytes) throw new InvalidDataException("XML exceeds 1 MiB.");
            using var input = new MemoryStream(xml, false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes
            });
            var document = XDocument.Load(reader);
            var root = document.Root ?? throw new InvalidDataException("Missing PostgreSQLCatalog.");
            Shape(root, "PostgreSQLCatalog", ["schemaVersion"], ["Update", "Packages"]);
            if ((string?)root.Attribute("schemaVersion") != "1") throw new InvalidDataException("Unsupported schemaVersion.");
            var update = One(root, "Update");
            Shape(update, "Update", ["url", "intervalHours"], []);
            var updateUrl = Https(Required(update, "url"));
            if (!int.TryParse(Required(update, "intervalHours"), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours is < 1 or > 720)
                throw new InvalidDataException("intervalHours must be 1–720.");
            var packages = One(root, "Packages");
            Shape(packages, "Packages", [], ["Package"]);
            var entries = packages.Elements().ToArray();
            if (entries.Length is < 1 or > 64) throw new InvalidDataException("Expected 1–64 packages.");
            var result = new List<PostgresPackage>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                Shape(entry, "Package", ["version", "revision", "architecture", "provider"], ["DownloadUrl", "SHA256"]);
                var version = Required(entry, "version");
                if (!Regex.IsMatch(version, @"^(1[4-9]|[2-9][0-9])\.(0|[1-9][0-9]{0,3})$", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("Expected a stable PostgreSQL version (major >= 14).");
                if (!int.TryParse(Required(entry, "revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision is < 1 or > 9999)
                    throw new InvalidDataException("Invalid revision.");
                if (Required(entry, "architecture") != "x64" || Required(entry, "provider") != "EDB")
                    throw new InvalidDataException("Only EDB Windows x64 ZIPs are supported.");
                if (!identities.Add(version + "/" + revision)) throw new InvalidDataException("Duplicate package.");
                var download = One(entry, "DownloadUrl"); Shape(download, "DownloadUrl", [], []);
                var url = Https(download.Value.Trim()); ValidateDownloadLocation(url, version, revision);
                var hash = One(entry, "SHA256"); Shape(hash, "SHA256", ["source"], []);
                var sha256 = hash.Value.Trim();
                if (!Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid SHA256.");
                var source = (string?)hash.Attribute("source");
                result.Add(new(version, revision, "x64", "EDB", url, sha256.ToLowerInvariant(), source is null ? null : Https(source, allowFragment: true)));
            }
            return new(updateUrl, TimeSpan.FromHours(hours), result.AsReadOnly());
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or UriFormatException)
        {
            throw new InvalidDataException(L10n.Format("CatalogInvalid", ex.Message), ex);
        }
    }

    internal static Uri Https(string text, bool allowFragment = false)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || (!allowFragment && uri.Fragment.Length != 0))
            throw new InvalidDataException("Expected an absolute HTTPS URL without credentials or fragment.");
        return uri;
    }

    public static void ValidateDownloadLocation(Uri uri, string version, int revision)
    {
        Https(uri.AbsoluteUri);
        if (!uri.IsDefaultPort || uri.IdnHost != "get.enterprisedb.com" || uri.Query.Length != 0 ||
            uri.AbsolutePath != $"/postgresql/postgresql-{version}-{revision}-windows-x64-binaries.zip")
            throw new InvalidDataException("Download URL must be the matching ZIP on get.enterprisedb.com.");
    }

    private static string Required(XElement element, string name) => (string?)element.Attribute(name) is { Length: > 0 } value
        ? value : throw new InvalidDataException("Missing " + name + ".");
    private static XElement One(XElement parent, string name)
    {
        var children = parent.Elements(name).ToArray();
        return children.Length == 1 ? children[0] : throw new InvalidDataException("Expected exactly one " + name + ".");
    }
    private static void Shape(XElement element, string name, string[] attributes, string[] children)
    {
        if (element.Name != name || element.Attributes().Any(a => !attributes.Contains(a.Name.ToString())) || element.Elements().Any(e => !children.Contains(e.Name.ToString())) ||
            (children.Length != 0 && element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))))
            throw new InvalidDataException("Unexpected XML structure in " + name + ".");
    }
}
