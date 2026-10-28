using System.Globalization;
using System.Reflection;
using PGTray;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class EmergencyBindingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var culture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                using var wake = new EventWaitHandle(false, EventResetMode.AutoReset);
                using var form = new MainForm(wake);
                object Field(string name) => typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                var manager = (InstanceManager)Field("manager");
                var binary = new BinaryInstallation(Guid.NewGuid(), @"X:\isolated-fixture\bin", "18.3", "18");
                var instance = new InstanceConfig(Guid.NewGuid(), "Fixture", binary.Id, @"X:\isolated-fixture\data", 55499, @"X:\isolated-fixture\log");
                manager.Config.Binaries.Add(binary); manager.Config.Instances.Add(instance);
                ((Dictionary<Guid, ClusterSettings>)Field("settings"))[instance.Id] = new(instance.Port, "127.0.0.1");
                typeof(MainForm).GetField("loaded", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
                var start = (Button)Field("start"); var stop = (Button)Field("stop"); var restart = (Button)Field("restart");
                void Render() => typeof(MainForm).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
                manager.Statuses[instance.Id] = new(InstanceState.Running, "fixture", ServerRunning: true);
                Render(); check(!start.Enabled && stop.Enabled && restart.Enabled && stop.Text == L10n.Get("Stop"), language + ": Healthy running instance keeps normal stop and restart; no emergency label");
                manager.BinaryProblems[binary.Id] = "Changed version";
                Render(); check(!start.Enabled && !stop.Enabled && !restart.Enabled, language + ": Abnormal programs without verified candidates disable all lifecycle actions");
                manager.EmergencyCandidates[instance.Id] = [binary];
                Render(); check(!start.Enabled && stop.Enabled && !restart.Enabled && stop.Text == L10n.Get("EmergencyStop"), language + ": Verified emergency eligibility enables only restricted stop");
                ((HashSet<Guid>)Field("operating")).Add(instance.Id);
                Render(); check(!start.Enabled && !stop.Enabled && !restart.Enabled, language + ": In-flight operation also disables emergency stop");
                using var choice = new EmergencyToolForm([binary, binary with { Id = Guid.NewGuid(), BinDirectory = @"Y:\independent\bin" }]);
                var combo = Descendants(choice).OfType<ComboBox>().Single();
                check(combo.SelectedIndex == -1 && combo.Items.Cast<object>().All(i => i.ToString()!.Contains("18.3") && i.ToString()!.Contains("\\bin")), language + ": Backup tool selection has no default and displays complete versions and paths");
            }
        }
        finally { CultureInfo.CurrentUICulture = culture; }
    }
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
}
