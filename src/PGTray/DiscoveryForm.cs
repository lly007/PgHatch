using PGTray.Core;

namespace PGTray;

internal sealed class DiscoveryForm : Form
{
    private sealed record BinaryChoice(Guid Id, string Label);
    private readonly InstanceManager manager;
    private readonly InstanceDiscovery discovery;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBox root = new() { Dock = DockStyle.Fill };
    private readonly Label note = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly Button scan = new() { Text = L10n.Get("ScanReadOnly"), AutoSize = true };
    private readonly Button import = new() { Text = L10n.Get("ImportSelected"), AutoSize = true, Enabled = false };
    private bool working, importing, allowClose;
    private List<DiscoveredInstance> candidates = [];

    public DiscoveryForm(InstanceManager manager, PostgresTools tools)
    {
        this.manager = manager; discovery = new(tools);
        Text = L10n.Get("ScanLocalPostgreSQLInstances"); Font = new Font("Segoe UI", 9F); BackColor = Color.White;
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; ClientSize = new(1000, 590); MinimumSize = new(850, 490);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new(0, 0, 0, 14) };
        top.ColumnStyles.Add(new(SizeType.Percent, 100)); top.ColumnStyles.Add(new(SizeType.AutoSize)); top.ColumnStyles.Add(new(SizeType.AutoSize));
        root.Text = AppContext.BaseDirectory;
        var browse = new Button { Text = L10n.Get("ChooseLocation"), AutoSize = true };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = L10n.Get("ChooseALimitedSearchDirectory"), UseDescriptionForTitle = true, ShowNewFolderButton = false }; if (picker.ShowDialog(this) == DialogResult.OK) root.Text = picker.SelectedPath; };
        top.Controls.Add(root); top.Controls.Add(browse); top.Controls.Add(scan);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = L10n.Get("Import"), FillWeight = 8, Name = "Selected" });
        foreach (var (title, weight) in new[] { (L10n.Get("DataDirectory"), 30), (L10n.Get("Version"), 8), (L10n.Get("Port"), 8), (L10n.Get("ListenAddress"), 13) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, FillWeight = weight, ReadOnly = true });
        grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = L10n.Get("UseBinaries"), FillWeight = 25, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L10n.Get("Notes"), FillWeight = 30, ReadOnly = true });
        grid.DataError += (_, e) => { e.ThrowException = false; note.Text = L10n.Get("ChooseCompatiblePostgreSQLBinariesForThisRow"); };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) => {
            if (e.RowIndex < 0 || e.RowIndex >= candidates.Count) return;
            if (!candidates[e.RowIndex].CanImport && grid.Rows[e.RowIndex].Cells[0].Value is true) grid.Rows[e.RowIndex].Cells[0].Value = false;
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new(0, 12, 0, 0) };
        var close = new Button { Text = L10n.Get("Close"), AutoSize = true }; close.Click += (_, _) => Close(); buttons.Controls.Add(close); buttons.Controls.Add(import);
        note.Text = L10n.Get("NothingIsSelectedByDefaultScanningFinds");
        note.Margin = new(0, 12, 0, 0); note.MaximumSize = new(1100, 0);
        layout.Controls.Add(top); layout.Controls.Add(grid); layout.Controls.Add(note); layout.Controls.Add(buttons); Controls.Add(layout);
        scan.Click += async (_, _) => await ScanAsync(); import.Click += async (_, _) => await ImportAsync();
        FormClosing += (_, e) => {
            if (allowClose) return;
            if (importing) { e.Cancel = true; note.Text = L10n.Get("SavingImportedInstancesPleaseWaitBeforeClosing"); return; }
            if (working) { e.Cancel = true; lifetime.Cancel(); note.Text = L10n.Get("CancelingTheReadOnlyScan"); }
        };
        FormClosed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }
    private async Task ScanAsync()
    {
        if (working || importing) return;
        working = true; scan.Enabled = import.Enabled = false; root.Enabled = false; note.Text = L10n.Get("ScanningReadOnly");
        try
        {
            var report = await discovery.ScanAsync(root.Text, manager.Config,
                includeNearby: string.Equals(Path.GetFullPath(root.Text), Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase), lifetime.Token);
            candidates = report.Instances; grid.Rows.Clear();
            foreach (var candidate in candidates)
            {
                var row = grid.Rows[grid.Rows.Add(false, candidate.DataDirectory, candidate.Major, candidate.Settings?.Port.ToString() ?? "—", candidate.Settings?.ListenAddresses ?? "—", null!, candidate.Note)];
                var choices = new List<BinaryChoice> { new(Guid.Empty, L10n.Get("ChooseBinaries")) };
                choices.AddRange(candidate.CompatibleBinaries.Select(b => new BinaryChoice(b.Id, $"{b.Version} · {b.BinDirectory}")));
                var combo = (DataGridViewComboBoxCell)row.Cells[5]; combo.DataSource = choices; combo.ValueMember = nameof(BinaryChoice.Id); combo.DisplayMember = nameof(BinaryChoice.Label);
                combo.Value = candidate.CompatibleBinaries.Count == 1 ? candidate.CompatibleBinaries[0].Id : Guid.Empty;
                if (!candidate.CanImport) { row.ReadOnly = true; row.DefaultCellStyle.ForeColor = SystemColors.GrayText; }
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = candidate.Note + "\r\n" + candidate.DataDirectory;
            }
            note.Text = L10n.Format("CandidatesAlreadyRegisteredRN", candidates.Count, candidates.Count(i => i.AlreadyRegistered)) + string.Join("\r\n", report.Notes.TakeLast(4));
            import.Enabled = candidates.Any(c => c.CanImport);
        }
        catch (OperationCanceledException) { allowClose = true; Close(); }
        catch (Exception ex) { note.Text = ex.Message; }
        finally { working = false; if (!IsDisposed) { scan.Enabled = true; root.Enabled = true; } }
    }
    private async Task ImportAsync()
    {
        grid.EndEdit();
        var selected = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).ToArray();
        if (selected.Length == 0) { note.Text = L10n.Get("SelectTheInstancesYouWantToImport"); return; }
        if (selected.Any(r => r.Cells[5].Value is not Guid id || id == Guid.Empty)) { note.Text = L10n.Get("ChooseCompatibleBinariesForEverySelectedRow"); return; }
        importing = true; scan.Enabled = import.Enabled = grid.Enabled = false;
        var errors = new List<string>(); var count = 0;
        try
        {
            foreach (var row in selected)
            {
                var candidate = candidates[row.Index];
                if (!candidate.CanImport) continue;
                try
                {
                    var binary = candidate.CompatibleBinaries.Single(b => b.Id == (Guid)row.Cells[5].Value!);
                    binary = await manager.AddBinaryAsync(binary.BinDirectory);
                    await manager.AddInstanceAsync(Path.GetFileName(candidate.DataDirectory), binary, candidate.DataDirectory, candidate.Settings!.Port);
                    row.Cells[0].Value = false; row.ReadOnly = true; row.Cells[6].Value = L10n.Get("ImportedNotStarted"); count++;
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(candidate.DataDirectory) + "：" + ex.Message); }
            }
            note.Text = L10n.Format("ImportedAllDatabasesKeepTheirCurrentState", count) + (errors.Count == 0 ? "" : "\r\n" + string.Join("\r\n", errors));
        }
        finally { importing = false; scan.Enabled = grid.Enabled = true; import.Enabled = true; }
    }
}

