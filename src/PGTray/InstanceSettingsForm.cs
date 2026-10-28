using PGTray.Core;
using System.Diagnostics;

namespace PGTray;

internal sealed class InstanceSettingsForm : LayoutForm
{
    private readonly TextBox name = new();
    public string InstanceName => name.Text;
    private sealed record Choice(Guid Id, string Label);
    public InstanceSettingsForm(InstanceConfig instance, InstanceManager manager) : base(L10n.Get("InstanceSettingsRepairBinaries"))
    {
        Table.Dock = DockStyle.Top; AutoScroll = true;
        Table.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, 0);
        ClientSize = new(780, 440); MinimumSize = new(680, 420); name.Text = instance.Name; Row(L10n.Get("DisplayName"), name);
        var path = new TextBox { Text = instance.DataDirectory, ReadOnly = true }; Row("PGDATA", path);
        var binaries = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(Choice.Label) };
        if (manager.FindBinary(instance) is null) binaries.Items.Add(new Choice(instance.BinaryId, L10n.Get("NotLinkedChooseCompatibleBinaries")));
        foreach (var b in manager.Config.Binaries) binaries.Items.Add(new Choice(b.Id, $"{(manager.BinaryProblems.ContainsKey(b.Id) ? L10n.Get("Unavailable2") : "")}{b.Version} — {b.BinDirectory}"));
        binaries.SelectedIndex = Enumerable.Range(0, binaries.Items.Count).FirstOrDefault(n => (binaries.Items[n] as Choice)?.Id == instance.BinaryId);
        Row(L10n.Get("LinkedBinaries"), binaries);
        var open = new Button { Text = L10n.Get("OpenPostgresqlConf"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        open.Click += (_, _) => {
            try
            {
                var config = Path.Combine(instance.DataDirectory, "postgresql.conf"); if (!File.Exists(config)) throw new IOException(L10n.Get("ConfigurationFileNotFound"));
                Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { config } });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "PgHatch"); }
        };
        Row(L10n.Get("OriginalConfiguration"), open);
        Footer(L10n.Get("RelinkingChecksPGVERSIONAgainstTheBinary"), async () => {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException(L10n.Get("TheNameCannotBeEmpty"));
            if (binaries.SelectedItem is not Choice choice) throw new InvalidOperationException(L10n.Get("ChoosePostgreSQLBinaries"));
            if (choice.Id == instance.BinaryId) await manager.RenameAsync(instance, name.Text);
            else
            {
                var linked = manager.Config.Instances.Where(i => i.BinaryId == instance.BinaryId || i.BinaryId == choice.Id).Select(i => i.Name + " — " + i.DataDirectory);
                var target = manager.Config.Binaries.Single(b => b.Id == choice.Id);
                if (MessageBox.Show(this, L10n.Format("BinaryRepairConfirm", target.Version + " — " + target.BinDirectory, string.Join("\r\n", linked)), L10n.Get("LinkedBinaries"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) throw new OperationCanceledException();
                await manager.ReassociateAsync(instance, choice.Id, name.Text);
            }
        });
    }
}


