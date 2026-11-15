using System.IO.Compression;
using System.Text;

internal static class RegistrationFixture
{
    internal static void Write(string archive)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archive))!);
        using var output = new FileStream(archive, FileMode.CreateNew);
        output.Write(DownloadTests.FixtureZip()); output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Update);
        zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "PGTray.IntegrationTests.exe"), "pgsql/bin/pg_config.exe");
        using (var writer = new StreamWriter(zip.CreateEntry("pgsql/bin/issue7.mode").Open(), Encoding.UTF8)) writer.Write("success");
        using (var writer = new StreamWriter(zip.CreateEntry("pgsql/share/postgres.bki").Open(), Encoding.UTF8)) writer.Write("Registration test fixture only");
    }
}
