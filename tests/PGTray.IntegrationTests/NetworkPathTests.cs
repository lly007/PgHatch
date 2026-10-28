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
            var configRoot = Path.Combine(root, "mapped-config"); Directory.CreateDirectory(configRoot);
            var store = new ConfigStore(configRoot);
            var binary = new BinaryInstallation(Guid.NewGuid(), Path.Combine(root, "not-executed-program"), "18.3", "18");
            var legacy = new InstanceConfig(Guid.NewGuid(), "Old mapped registration", binary.Id, drive + @"\directory-target", 55499, Path.Combine(root, "log"));
            store.SaveAsync(new AppConfig(AppConfig.CurrentSchema, [binary], [legacy])).GetAwaiter().GetResult();
            var manager = new InstanceManager(store, new PostgresTools(new ProcessRunner())); manager.LoadAsync().GetAwaiter().GetResult();
            var operationData = manager.EnsureDataPath(manager.Config.Instances.Single());
            check(operationData == PathSafety.Identity(Path.Combine(remote, "directory-target")), "Operation receives the verified UNC identity while the stored drive-letter path is preserved");
            check(manager.Config.Instances.Single().DataDirectory == legacy.DataDirectory, "Manager loads an old mapped-drive registration without changing or rejecting its stable path");
            store.SaveAsync(manager.Config with { Instances = [legacy with { DataDirectory = Path.Combine(remote, "directory-target") }] }).GetAwaiter().GetResult();
            manager.LoadAsync().GetAwaiter().GetResult();
            check(manager.EnsureDataPath(manager.Config.Instances.Single()) == operationData, "Reload accepts a stable real mapped-drive to UNC alias change");
            store.SaveAsync(manager.Config with { Instances = [legacy] }).GetAwaiter().GetResult();
            manager.LoadAsync().GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.Combine(root, "remapped", "directory-target"));
            // Windows can cache metadata handles. This drive maps only the owned test root;
            // disconnecting it never closes or terminates a PostgreSQL server.
            if (WNetCancelConnection2(drive, 0, true) != 0) throw new IOException("Could not release the owned mapping for remap test");
            resource.Remote = Path.Combine(remote, "remapped");
            if (WNetAddConnection2(ref resource, null, null, 0) != 0) throw new IOException("Could not remap the owned test drive");
            try { manager.EnsureDataPath(manager.Config.Instances.Single()); throw new Exception("Changed mapping accepted"); } catch (IOException) { }
            check(true, "Manager rejects changed actual mapping against its loaded identity snapshot");
            try { manager.LoadAsync().GetAwaiter().GetResult(); throw new Exception("Reload accepted changed mapping"); } catch (IOException) { }
            try { manager.EnsureDataPath(manager.Config.Instances.Single()); throw new Exception("Reload reset changed mapping"); } catch (IOException) { }
            check(true, "Reload of the same old registration does not reset its trusted mapping identity");
            manager.RefreshAllAsync().GetAwaiter().GetResult();
            check(manager.Statuses[legacy.Id].State == InstanceState.Unknown, "Read-only refresh cannot report another mapped target as the registered instance");
            manager.ShutdownAsync().GetAwaiter().GetResult();
        }
        finally
        {
            var removed = WNetCancelConnection2(drive, 0, true);
            if (removed != 0) throw new IOException("Could not remove test mapping " + drive + "; Windows error " + removed);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource { public int Scope, Type, DisplayType, Usage; public string? Local, Remote, Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
