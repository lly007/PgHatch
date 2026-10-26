using PGTray.Core;
using System.Diagnostics;
using System.Text;

namespace PGTray;

internal class LayoutForm : Form
{
    protected readonly CancellationTokenSource Lifetime = new();
    private bool lifetimeDisposed;
    protected readonly TableLayoutPanel Table = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new(16), ColumnCount = 3 };
    protected LayoutForm(string title)
    {
        Text = title; Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96); BackColor = Color.White;
        AutoScroll = true;
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        ClientSize = new(680, 360); MinimumSize = new(580, 300);
        Table.ColumnStyles.Add(new(SizeType.AutoSize)); Table.ColumnStyles.Add(new(SizeType.Percent, 100)); Table.ColumnStyles.Add(new(SizeType.AutoSize));
        Controls.Add(Table);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !lifetimeDisposed) { lifetimeDisposed = true; Lifetime.Cancel(); Lifetime.Dispose(); }
        base.Dispose(disposing);
    }
    protected void Row(string label, Control input, Control? trailing = null)
    {
        var row = Table.RowCount++; Table.RowStyles.Add(new(SizeType.AutoSize));
        Table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 6, 14, 10) }, 0, row);
        input.Dock = DockStyle.Fill; input.Margin = new(0, 4, 8, 10); Table.Controls.Add(input, 1, row);
        if (trailing is not null) Table.Controls.Add(trailing, 2, row);
    }
    protected void Footer(string note, Func<Task> accept)
    {
        var row = Table.RowCount++;
        var label = new Label { Text = note, AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new(620, 0), Margin = new(0, 12, 0, 16) };
        Table.RowStyles.Add(new(SizeType.AutoSize)); Table.Controls.Add(label, 0, row); Table.SetColumnSpan(label, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = L10n.Get("OK"), AutoSize = true, Padding = new(12, 4, 12, 4) };
        var cancel = new Button { Text = L10n.Get("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new(12, 4, 12, 4) };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); Table.RowStyles.Add(new(SizeType.AutoSize)); Table.Controls.Add(buttons, 0, Table.RowCount++); Table.SetColumnSpan(buttons, 3);
        AcceptButton = ok; CancelButton = cancel;
        var accepting = false;
        FormClosing += (_, e) => { if (accepting) e.Cancel = true; };
        ok.Click += async (_, _) => {
            accepting = true; ok.Enabled = cancel.Enabled = false;
            try { await accept(); accepting = false; if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
            catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "PgHatch", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { accepting = false; if (!ok.IsDisposed) ok.Enabled = cancel.Enabled = true; }
        };
    }
    protected static Button Browse(TextBox field, bool existing)
    {
        var button = new Button { Text = L10n.Get("Browse"), AutoSize = true, Margin = new(0, 4, 0, 10) };
        button.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = existing ? L10n.Get("SelectAnExistingDirectory") : L10n.Get("SelectAnEmptyDataDirectoryOrChoose"), UseDescriptionForTitle = true, ShowNewFolderButton = !existing }; if (picker.ShowDialog() == DialogResult.OK) field.Text = picker.SelectedPath; };
        return button;
    }
}

internal sealed class BinaryForm : LayoutForm
{
    private sealed record Choice(BinaryInstallation Binary, string Label) { public override string ToString() => Label; }
    public BinaryForm(InstanceManager manager, ConfigStore? store = null) : base(L10n.Get("PostgreSQLBinaries"))
    {
        ClientSize = new(820, 510); MinimumSize = new(720, 470);
        var list = new ListBox { Height = 170, HorizontalScrollbar = true };
        var path = new TextBox(); var busy = false;
        void RefreshList()
        {
            var selected = (list.SelectedItem as Choice)?.Binary.Id;
            list.Items.Clear();
            foreach (var binary in manager.Config.Binaries)
            {
                var unavailable = manager.BinaryProblems.ContainsKey(binary.Id);
                var index = list.Items.Add(new Choice(binary, $"{(unavailable ? L10n.Get("Unavailable") : L10n.Get("Available"))} · PostgreSQL {binary.Version} — {binary.BinDirectory}"));
                if (binary.Id == selected) list.SelectedIndex = index;
            }
        }
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItem is Choice c) path.Text = c.Binary.BinDirectory; };
        Row(L10n.Get("RegisteredBinaries"), list); Row(L10n.Get("RootDirectoryBin"), path, Browse(path, true));
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        async Task Run(Func<Task> action)
        {
            if (busy) return; busy = true; actions.Enabled = list.Enabled = path.Enabled = false;
            try { await action(); await manager.RefreshAllAsync(); RefreshList(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "PgHatch", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { busy = false; actions.Enabled = list.Enabled = path.Enabled = true; }
        }
        var add = new Button { Text = L10n.Get("RegisterBinaries"), AutoSize = true }; add.Click += async (_, _) => await Run(async () => { await manager.AddBinaryAsync(path.Text); });
        var repair = new Button { Text = L10n.Get("RepairSelectedPath"), AutoSize = true }; repair.Click += async (_, _) => await Run(async () => {
            var c = list.SelectedItem as Choice ?? throw new InvalidOperationException(L10n.Get("SelectRegisteredBinariesFirstThenEnterThe"));
            await manager.RepairBinaryAsync(c.Binary.Id, path.Text);
        });
        var remove = new Button { Text = L10n.Get("UnregisterBinaries"), AutoSize = true }; remove.Click += async (_, _) => {
            if (list.SelectedItem is not Choice c) return;
            if (MessageBox.Show(this, L10n.Get("ThisOnlyRemovesTheBinaryRegistrationFrom"), L10n.Get("UnregisterBinaries2"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                await Run(() => manager.UnregisterBinaryAsync(c.Binary.Id));
        };
        var discover = new Button { Text = L10n.Get("FindNearbyBinaries"), AutoSize = true }; discover.Click += async (_, _) => {
            await Run(async () => {
                var candidates = await Task.Run(() => InstanceManager.DiscoverCandidates(AppContext.BaseDirectory).ToArray());
                if (candidates.Length == 0) { MessageBox.Show(this, L10n.Get("NoBinariesFoundNearbyBrowseToA"), "PgHatch"); return; }
                var menu = new ContextMenuStrip(); menu.Closed += (_, _) => menu.Dispose();
                foreach (var candidate in candidates) menu.Items.Add(candidate, null, (_, _) => path.Text = candidate);
                menu.Show(discover, new Point(0, discover.Height));
            });
        };
        actions.Controls.AddRange([add, repair, remove, discover]); Row(L10n.Get("Registration"), actions);
        var catalog = new Button { Text = L10n.Get("CatalogTitle"), AutoSize = true };
        catalog.Click += (_, _) => { using var dialog = new CatalogForm(store ?? new ConfigStore(AppContext.BaseDirectory)); dialog.ShowDialog(this); };
        var download = new Button { Text = L10n.Get("DownloadTitle"), AutoSize = true };
        download.Click += async (_, _) => { using var dialog = new DownloadForm(manager, store ?? new ConfigStore(AppContext.BaseDirectory)); dialog.ShowDialog(this); if (dialog.Installed) { await manager.RefreshAllAsync(); RefreshList(); } };
        var online = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        online.Controls.AddRange([catalog, download]); Row(L10n.Get("CatalogMetadata"), online);
        RefreshList();
        Footer(L10n.Get("DiscoveryListsCandidatesOnlyRegisterSavesThem"), () => Task.CompletedTask);
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
    }
}
internal sealed class InstanceForm : LayoutForm
{
    private readonly TextBox name = new(), data = new(), username = new() { Text = "postgres" }, password = new() { UseSystemPasswordChar = true };
    private readonly ComboBox binaries = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Label" };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly List<BinaryInstallation> installations;
    public string InstanceName => name.Text;
    public string DataDirectory => data.Text;
    public BinaryInstallation Binary => installations[binaries.SelectedIndex];
    public int Port => (int)port.Value;
    public string Username => username.Text;
    public string Password => password.Text;
    public InstanceForm(List<BinaryInstallation> installations, PostgresTools tools, bool create) : base(create ? L10n.Get("NewPostgreSQLInstance") : L10n.Get("ImportPostgreSQLInstance"))
    {
        this.installations = installations;
        ClientSize = new(720, create ? 460 : 390); MinimumSize = new(640, create ? 470 : 400);
        foreach (var b in installations) binaries.Items.Add(new { Label = $"{b.Version} — {b.BinDirectory}" });
        binaries.SelectedIndex = 0;
        Row(L10n.Get("InstanceName"), name); Row(L10n.Get("PostgreSQLBinaries2"), binaries); Row(L10n.Get("PGDATADirectory"), data, Browse(data, !create));
        if (!create)
        {
            var read = new Button { Text = L10n.Get("ReadPort"), AutoSize = true };
            read.Click += async (_, _) => { read.Enabled = false; try { var result = await tools.ReadSettingsAsync(Binary, data.Text, Lifetime.Token); if (!IsDisposed) port.Value = result.Port; } catch (OperationCanceledException) { } catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, L10n.Get("CouldNotReadConfiguration")); } finally { if (!read.IsDisposed) read.Enabled = true; } };
            Row(L10n.Get("Port"), port, read);
        }
        else { Row(L10n.Get("Port"), port); Row(L10n.Get("Superuser"), username); Row(L10n.Get("InitialPassword"), password); }
        Footer(create ? L10n.Get("OnlyAnEmptyDirectoryWillBeInitialized") :
            L10n.Get("ImportChecksTheClusterVersionAndConfigured"), async () => {
                if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(data.Text)) throw new ArgumentException(L10n.Get("EnterAnInstanceNameAndDataDirectory"));
                if (create && (string.IsNullOrWhiteSpace(username.Text) || password.Text.Length == 0)) throw new ArgumentException(L10n.Get("EnterASuperuserNameAndInitialPassword"));
                if (!create) port.Value = (await tools.ReadSettingsAsync(Binary, data.Text, Lifetime.Token)).Port;
            });
        if (!create) port.ReadOnly = true;
    }
    protected override void Dispose(bool disposing) { if (disposing && !password.IsDisposed) password.Clear(); base.Dispose(disposing); }
}

internal sealed class RenameForm : LayoutForm
{
    private readonly TextBox name = new();
    public string Value => name.Text;
    public RenameForm(string current) : base(L10n.Get("RenameInstance"))
    {
        ClientSize = new(580, 240); name.Text = current; Row(L10n.Get("DisplayName"), name);
        Footer(L10n.Get("OnlyTheDisplayNameInPgHatchWill"), () => { if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException(L10n.Get("TheNameCannotBeEmpty")); return Task.CompletedTask; });
    }
}

internal sealed class LogForm : Form
{
    private readonly InstanceConfig instance;
    private readonly string diagnostic;
    private readonly TextBox text = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    public LogForm(InstanceConfig instance, string diagnostic)
    {
        this.instance = instance; this.diagnostic = diagnostic;
        Text = L10n.Format("LogWindowTitle", instance.Name); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(900, 560); MinimumSize = new(600, 350); StartPosition = FormStartPosition.CenterParent;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(8) };
        var refresh = new Button { Text = L10n.Get("Refresh"), AutoSize = true }; refresh.Click += async (_, _) => { try { await LoadLogAsync(); } catch (Exception ex) { text.Text = ex.Message; } };
        var open = new Button { Text = L10n.Get("OpenFullLog"), AutoSize = true }; open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { instance.LogPath } }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        buttons.Controls.Add(refresh); buttons.Controls.Add(open); Controls.Add(text); Controls.Add(buttons);
    }
    public async Task LoadLogAsync()
    {
        string tail = L10n.Get("NoPgHatchStartupLogYetExistingLogging");
        if (File.Exists(instance.LogPath))
        {
            await using var stream = new FileStream(instance.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
            var bytes = new byte[Math.Min(65536, stream.Length)];
            var count = 0;
            while (count < bytes.Length) { var read = await stream.ReadAsync(bytes.AsMemory(count)); if (read == 0) break; count += read; }
            tail = TextDecoding.Decode(bytes[..count]);
        }
        if (!IsDisposed) text.Text = diagnostic + L10n.Get("RNRNPostgreSQLLogLast") + tail;
    }
}


