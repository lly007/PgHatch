using System.Runtime.InteropServices;
using PGTray.Core;

internal static class NetworkPathTests
{
    internal static void Run(string root, Action<bool, string> check, Action<string> note)
    {
        var link = Path.Combine(root, "symbolic-link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(root, "directory-target"));
            check(PathSafety.Identity(link) == PathSafety.Identity(Path.Combine(root, "directory-target")), "Actual directory symbolic link resolves to its target identity");
            try { PathSafety.ValidateInitializationTarget(Path.Combine(link, "new")); throw new Exception("Symbolic link accepted"); } catch (IOException) { }
            check(true, "Initialization rejects actual directory symbolic link");
        }
        catch (UnauthorizedAccessException) { note("SKIP: Actual symbolic link creation requires an unavailable Windows privilege."); }
        catch (IOException ex) when (!Directory.Exists(link)) { note("SKIP: Actual symbolic link could not be created: " + ex.Message); }

        var drive = new[] { "Z:", "Y:", "X:", "W:" }.FirstOrDefault(d => new DriveInfo(d).DriveType == DriveType.NoRootDirectory);
        if (drive is null) { note("SKIP: No unused drive letter for isolated network mapping."); return; }
        // Map only this new owned directory, using the existing local administrative share.
        // Never create shares, change SMB services or request credentials.
        var remote = @"\\localhost\" + root[0] + "$" + root[2..];
        var resource = new NetResource { Type = 1, Local = drive, Remote = remote };
        var result = WNetAddConnection2(ref resource, null, null, 0);
        if (result != 0) { note("SKIP: Actual isolated SMB mapping unavailable (Windows error " + result + "). DriveType fault injection still ran."); return; }
        try
        {
            var mapped = drive + @"\network-new";
            check(new DriveInfo(drive).DriveType == DriveType.Network, "Actual SMB mapped drive is identified as Network by Windows");
            try { PathSafety.ValidateInitializationTarget(mapped); throw new Exception("Mapped drive accepted"); } catch (IOException) { }
            check(true, "Actual mapped network drive initialization is rejected before creating a directory");
            check(PathSafety.Identity(drive + @"\directory-target") == PathSafety.Identity(Path.Combine(remote, "directory-target")), "Actual mapped drive and its UNC representation have identical resolved identities");
            check(PathSafety.IsNetworkStorage(mapped), "Actual mapped drive import is recognized for a network-storage warning");
        }
        finally
        {
            var removed = WNetCancelConnection2(drive, 0, false);
            if (removed != 0) throw new IOException("Could not remove test mapping " + drive + "; Windows error " + removed);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource { public int Scope, Type, DisplayType, Usage; public string? Local, Remote, Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
