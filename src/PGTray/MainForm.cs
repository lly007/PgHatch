using PGTray.Core;
using System.Diagnostics;

namespace PGTray;

public sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(30, 41, 59), Muted = Color.FromArgb(100, 116, 139), Accent = Color.FromArgb(35, 92, 180);
    private readonly ConfigStore store = new(AppContext.BaseDirectory);
    private readonly PostgresTools tools = new(new ProcessRunner());
    private readonly InstanceManager manager;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly UiIcons uiIcons = new();
    private readonly ApplicationIcons applicationIcons = new();
    private readonly UiPreferences preferences;
    private readonly PictureBox brandLogo = new() { SizeMode = PictureBoxSizeMode.Zoom, TabStop = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 4000 };
    private readonly CancellationTokenSource lifetime = new();
    private bool sessionEnding;
    private CancellationTokenSource? detailCancellation;
    private readonly HashSet<Task> actions = [];
    private readonly HashSet<Guid> operating = [];
    private readonly List<Button> configurationButtons = [];
    private readonly List<ToolStripItem> configurationMenuItems = [], instanceMenuItems = [];
    private readonly List<Control> instanceControls = [];
    private readonly List<ContextMenuStrip> dropDownMenus = [];
    private readonly Dictionary<Guid, ClusterSettings> settings = [];
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        EnableHeadersVisualStyles = false, GridColor = Color.FromArgb(236, 240, 245), AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None };
    private readonly Label summary = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label status = new() { AutoSize = true, ForeColor = Muted, Dock = DockStyle.Fill };
    private readonly Label heading = new() { AutoSize = true, ForeColor = Ink };
    private readonly Label badge = new() { AutoSize = true, Padding = new(8, 4, 8, 4), Margin = new(0, 4, 0, 8) };
    private readonly Panel detailHost = new() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new(16), AutoScroll = true };
    private readonly TableLayoutPanel detailCard = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 5 };
    private readonly Panel emptyCard = new() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new(24) };
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly FlowLayoutPanel recovery = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false, BackColor = Color.FromArgb(255, 247, 224), Padding = new(12) };
    private readonly Label recoveryText = new() { AutoSize = true, MaximumSize = new(900, 0), ForeColor = Color.FromArgb(130, 76, 14) };
    private readonly Button start, stop, restart;
    private RegisteredWaitHandle? restoreWait;
    private bool loaded, configuring, refreshing, exiting, closing, importProgramHintShown;
    private InstanceConfig? Selected => grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as InstanceConfig : null;

    public MainForm(EventWaitHandle showWindow) : this(showWindow, new UiPreferences(AppContext.BaseDirectory)) { }
    internal MainForm(EventWaitHandle showWindow, UiPreferences preferences)
    {
        this.preferences = preferences;
        manager = new(store, tools);
        Text = L10n.Get("PgHatchPostgreSQLInstanceManager");
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F); BackColor = Color.FromArgb(244, 246, 249); ForeColor = Ink;
        ClientSize = new(1080, 700); MinimumSize = new(900, 520); StartPosition = FormStartPosition.CenterScreen; Icon = applicationIcons.Window(DeviceDpi);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var headerHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new(0, 0, 0, 12) };
        headerHost.ColumnStyles.Add(new(SizeType.Percent, 100)); headerHost.ColumnStyles.Add(new(SizeType.Absolute, 116));
        headerHost.RowStyles.Add(new(SizeType.AutoSize));
        var header = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1, Margin = Padding.Empty };
        header.RowStyles.Add(new(SizeType.AutoSize)); header.RowStyles.Add(new(SizeType.AutoSize)); header.RowStyles.Add(new(SizeType.AutoSize));
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("PgHatch.Brand.Header.png")!)
        using (var image = Image.FromStream(stream)) brandLogo.Image = new Bitmap(image);
        brandLogo.AccessibleName = L10n.Get("PgHatchLogo");
        headerHost.Controls.Add(header, 0, 0); headerHost.Controls.Add(brandLogo, 1, 0);
        void ApplyHeaderHeight()
        {
            // Minimized windows have a zero client area. Measuring the temporarily wrapped
            // toolbar there can change this row while its parent is still restoring layout.
            if (WindowState == FormWindowState.Minimized || header.Width <= 0) return;
            // Nested percent columns can measure a wrapped toolbar at the wrong width.
            // Use the actual left-column width and keep only the occupied content/logo height.
            var height = Math.Max(header.GetPreferredSize(new(header.Width, 0)).Height, headerHost.ColumnStyles[1].Width > 0 ? brandLogo.Height : 0);
            var rowHeight = height + headerHost.Margin.Vertical;
            if (layout.RowStyles[0].Height != rowHeight) layout.RowStyles[0].Height = rowHeight;
        }
        var headerHeightPending = false;
        void FitHeaderHeight()
        {
            if (IsDisposed || Disposing || WindowState == FormWindowState.Minimized) return;
            if (!IsHandleCreated) { ApplyHeaderHeight(); return; }
            if (headerHeightPending) return;
            headerHeightPending = true;
            // Child Layout/SizeChanged can run inside the outer table's layout pass.
            // Measure after that pass so its cached row sizes cannot overwrite the result.
            BeginInvoke(() => {
                headerHeightPending = false;
                if (!IsDisposed && !Disposing) ApplyHeaderHeight();
            });
        }
        header.Layout += (_, _) => FitHeaderHeight();
        header.SizeChanged += (_, _) => FitHeaderHeight();
        void FitLogo()
        {
            if (WindowState == FormWindowState.Minimized || headerHost.ClientSize.Width <= 0) return;
            var scale = DeviceDpi / 96F;
            var logicalWidth = headerHost.ClientSize.Width / scale;
            var size = logicalWidth < 680 ? 80 : 100;
            var showLogo = logicalWidth >= 620;
            brandLogo.Visible = showLogo;
            var width = showLogo ? (size + 16) * scale : 0;
            if (headerHost.ColumnStyles[1].Width != width) headerHost.ColumnStyles[1].Width = width;
            brandLogo.Size = new((int)(size * scale), (int)(size * scale));
            brandLogo.Margin = new((int)(16 * scale), 0, 0, 0);
            FitHeaderHeight();
        }
        headerHost.SizeChanged += (_, _) => FitLogo();
        header.Controls.Add(new Label { Text = "PgHatch", AutoSize = true, Font = new Font(Font.FontFamily, 22F, FontStyle.Bold), ForeColor = Ink, Margin = new(0, 0, 0, 2) });
        summary.Text = L10n.Format("LocalPostgreSQLInstancesV", AppVersion.Current); summary.Margin = new(0, 0, 0, 14); header.Controls.Add(summary);
        var toolbar = Flow();
        // Allow the toolbar to shrink again after wrapping at a narrower width.
        toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        toolbar.Dock = DockStyle.Top;
        var add = DropDownButton(L10n.Get("AddInstance"), UiGlyph.Plus,
            MenuItem(L10n.Get("NewInstance"), () => AddAsync(true), configure: true, glyph: UiGlyph.Plus),
            MenuItem(L10n.Get("ImportInstance"), () => AddAsync(false), configure: true, glyph: UiGlyph.Database),
            new ToolStripSeparator(), MenuItem(L10n.Get("ScanLocalInstances"), ScanAsync, configure: true));
        toolbar.Controls.Add(add); configurationButtons.Add(add);
        AddConfigurationButton(toolbar, L10n.Get("PostgreSQLBinaries3"), BinariesAsync, UiGlyph.Database);
        var refresh = Button(L10n.Get("Refresh"), RefreshVersionsAsync, glyph: UiGlyph.Refresh); toolbar.Controls.Add(refresh); configurationButtons.Add(refresh);
        var quit = new ToolStripMenuItem(L10n.Get("ExitPgHatchKeepDatabasesRunning"));
        // Exit must not run inside RunAsync: the unified exit path waits for its tracked actions.
        quit.Click += async (_, _) => await RequestExitAsync(false);
        toolbar.Controls.Add(DropDownButton(L10n.Get("More"), UiGlyph.Ellipsis,
            MenuItem(L10n.Get("OpenConfigurationFolder"), () => { OpenFolder(store.Root); return Task.CompletedTask; }, requiresConfiguration: false, glyph: UiGlyph.FolderOpen),
            MenuItem(L10n.Get("ReloadConfiguration"), LoadAsync, requiresConfiguration: false, glyph: UiGlyph.Refresh), LanguageMenu(), new ToolStripSeparator(),
            MenuItem(L10n.Get("AboutPgHatch"), () => { using var dialog = new AboutForm(); dialog.ShowDialog(this); return Task.CompletedTask; }, requiresConfiguration: false),
            new ToolStripSeparator(), quit));
        header.Controls.Add(toolbar); layout.Controls.Add(headerHost, 0, 0);
        recovery.Controls.Add(recoveryText);
        recovery.Controls.Add(Button(L10n.Get("OpenConfigurationFile"), () => {
            if (!File.Exists(store.RecoveryFilePath)) throw new IOException(L10n.Get("TheConfigurationFileHasNotBeenCreated"));
            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { store.RecoveryFilePath } }); return Task.CompletedTask;
        }, requiresConfiguration: false, glyph: UiGlyph.FileText));
        recovery.Controls.Add(Button(L10n.Get("Reload"), LoadAsync, requiresConfiguration: false, glyph: UiGlyph.Refresh));
        recovery.Controls.Add(Button(L10n.Get("RestoreBak"), () => RecoverAsync(true), requiresConfiguration: false));
        recovery.Controls.Add(Button(L10n.Get("PreserveFileAndStartFresh"), () => RecoverAsync(false), requiresConfiguration: false));
        layout.Controls.Add(recovery, 0, 1);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new(0, 0, 0, 14) };
        content.ColumnStyles.Add(new(SizeType.Percent, 42)); content.ColumnStyles.Add(new(SizeType.Percent, 58));
        var listCard = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new(12), ColumnCount = 1, RowCount = 2, Margin = new(0, 0, 12, 0) };
        listCard.RowStyles.Add(new(SizeType.AutoSize)); listCard.RowStyles.Add(new(SizeType.Percent, 100));
        listCard.Controls.Add(new Label { Text = L10n.Get("Instances"), AutoSize = true, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), Margin = new(0, 0, 0, 12) });
        foreach (var (name, weight) in new[] { (L10n.Get("Name"), 34F), (L10n.Get("Version"), 17F), (L10n.Get("Status"), 28F), (L10n.Get("Port"), 21F) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(244, 247, 251), ForeColor = Muted, Padding = new(4), WrapMode = DataGridViewTriState.True };
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle = new() { SelectionBackColor = Color.FromArgb(225, 237, 255), SelectionForeColor = Ink, Padding = new(4), ForeColor = Ink, BackColor = Color.White };
        listCard.Controls.Add(grid, 0, 1); content.Controls.Add(listCard, 0, 0);
        detailHost.Controls.Add(detailCard); detailHost.Controls.Add(emptyCard); content.Controls.Add(detailHost, 1, 0); layout.Controls.Add(content, 0, 2);
        heading.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold); heading.MaximumSize = new(450, 0);
        detailCard.RowStyles.Add(new(SizeType.AutoSize)); detailCard.RowStyles.Add(new(SizeType.AutoSize)); detailCard.RowStyles.Add(new(SizeType.AutoSize));
        detailCard.RowStyles.Add(new(SizeType.AutoSize)); detailCard.RowStyles.Add(new(SizeType.AutoSize));
        detailCard.Controls.Add(heading, 0, 0); detailCard.Controls.Add(badge, 0, 1);
        var values = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new(0, 0, 0, 10) };
        values.ColumnStyles.Add(new(SizeType.AutoSize)); values.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var name in new[] { "PostgreSQL", L10n.Get("Binaries"), "PGDATA", L10n.Get("ListenAddress"), L10n.Get("Port"), L10n.Get("Connection") })
        {
            var row = values.RowCount++; values.RowStyles.Add(new(SizeType.AutoSize));
            values.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = Muted, Margin = new(0, 1, 14, 3) }, 0, row);
            var field = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ink, Dock = DockStyle.Fill, Margin = new(0, 1, 0, 3), AccessibleName = name };
            fields.Add(name, field); values.Controls.Add(field, 1, row);
        }
        detailCard.Controls.Add(values, 0, 3);
        var lifecycle = Flow(); lifecycle.Margin = new(0, 0, 0, 10);
        start = NewButton(L10n.Get("Start"), primary: true, glyph: UiGlyph.Play); stop = NewButton(L10n.Get("Stop"), glyph: UiGlyph.Square); restart = NewButton(L10n.Get("Restart"), glyph: UiGlyph.Rotate);
        start.Click += async (_, _) => await OperateAsync("start"); stop.Click += async (_, _) => await OperateAsync("stop"); restart.Click += async (_, _) => await OperateAsync("restart");
        lifecycle.Controls.AddRange([start, stop, restart]); detailCard.Controls.Add(lifecycle, 0, 2);
        var utilities = Flow(); utilities.Margin = new(0, 0, 0, 12);
        var logs = Button(L10n.Get("Logs2"), LogsAsync, glyph: UiGlyph.FileText);
        var dataDirectory = MenuItem(L10n.Get("OpenDataDirectory"), () => { OpenFolder(RequireSelected().DataDirectory); return Task.CompletedTask; }, glyph: UiGlyph.FolderOpen);
        var instanceSettings = MenuItem(L10n.Get("InstanceSettings"), SettingsAsync, glyph: UiGlyph.Settings);
        var remove = MenuItem(L10n.Get("UnregisterInstance"), RemoveAsync);
        instanceMenuItems.AddRange([dataDirectory, instanceSettings, remove]);
        var instanceMore = DropDownButton(L10n.Get("MoreActions"), UiGlyph.Ellipsis, dataDirectory, instanceSettings, new ToolStripSeparator(), remove);
        utilities.Controls.AddRange([logs, instanceMore]); instanceControls.AddRange([logs, instanceMore]); detailCard.Controls.Add(utilities, 0, 4);
        BuildEmptyCard();
        status.Text = L10n.Get("LoadingConfiguration"); status.Padding = new(0, 2, 0, 0); layout.Controls.Add(status, 0, 3); Controls.Add(layout);
        grid.SelectionChanged += async (_, _) => { UpdateDetails(); await LoadSelectedSettingsAsync(); };
        grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await RunAsync(LogsAsync); };
        DpiChanged += (_, e) => {
            foreach (var menu in dropDownMenus) menu.Close();
            uiIcons.UpdateDpi(e.DeviceDpiNew); Icon = applicationIcons.Window(e.DeviceDpiNew);
            if (tray is not null) tray.Icon = applicationIcons.Tray(e.DeviceDpiNew);
            UpdateRowHeight(); BeginInvoke(ClampToWorkingArea);
            FitLogo();
        }; UpdateRowHeight();
        HandleCreated += (_, _) => { Icon = applicationIcons.Window(DeviceDpi); if (tray is not null) tray.Icon = applicationIcons.Tray(DeviceDpi); };
        trayMenu.Items.Add(L10n.Get("OpenManager"), null, (_, _) => RestoreWindow());
        trayMenu.Items.Add(L10n.Get("HideWindow"), null, (_, _) => Hide()); trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(L10n.Get("ExitPgHatchKeepDatabasesRunning"), null, async (_, _) => await RequestExitAsync(false));
        trayMenu.Items.Add(L10n.Get("StopAllRegisteredInstancesAndExit"), null, async (_, _) => await RequestExitAsync(true));
        tray = new NotifyIcon { Icon = applicationIcons.Tray(DeviceDpi), Text = L10n.Get("PgHatchLoadingConfiguration"), Visible = true, ContextMenuStrip = trayMenu };
        tray.DoubleClick += (_, _) => RestoreWindow(); manager.Changed += ManagerChanged;
        timer.Tick += async (_, _) => {
            if (!loaded || closing || refreshing) return; refreshing = true;
            try { await manager.RefreshAllAsync(); } catch (Exception ex) { if (!closing) status.Text = ex.Message; }
            finally { refreshing = false; }
        };
        KeyPreview = true; KeyDown += async (_, e) => { if (e.Control && e.KeyCode == Keys.Q) { e.SuppressKeyPress = true; await RequestExitAsync(false); } };
        FormClosing += async (_, e) => {
            if (exiting) return; e.Cancel = true;
            if (e.CloseReason == CloseReason.UserClosing) Hide(); else await RequestExitAsync(false);
        };
        FormClosed += (_, _) => {
            manager.Changed -= ManagerChanged; restoreWait?.Unregister(null); lifetime.Cancel(); detailCancellation?.Cancel();
            timer.Dispose(); tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); lifetime.Dispose();
            foreach (var menu in dropDownMenus) menu.Dispose();
        };
        Shown += async (_, _) => {
            ClampToWorkingArea(); FitLogo();
            if (preferences.LoadError is { } error) MessageBox.Show(this, L10n.Format("PreferenceReadFailed", error), "PgHatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            restoreWait = ThreadPool.RegisterWaitForSingleObject(showWindow, (_, _) => {
                if (IsHandleCreated && !IsDisposed) try { BeginInvoke(RestoreWindow); } catch (InvalidOperationException) { }
            }, null, Timeout.Infinite, false);
            await RunAsync(LoadAsync, requiresConfiguration: false);
        };
    }
    private void BuildEmptyCard()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        panel.Controls.Add(new Label { Text = L10n.Get("ManagePostgreSQL"), AutoSize = true, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), Margin = new(0, 0, 0, 12) });
        var welcome = new Label { Text = L10n.Get("CreateANewLocalInstanceOrRegister"), Dock = DockStyle.Fill, ForeColor = Muted, Margin = new(0, 0, 0, 22) };
        emptyCard.SizeChanged += (_, _) => {
            var width = emptyCard.ClientSize.Width - emptyCard.Padding.Horizontal;
            if (width < 40) return;
            welcome.Size = new(width, TextRenderer.MeasureText(welcome.Text, welcome.Font, new(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + welcome.Font.Height);
            panel.PerformLayout();
        };
        panel.Controls.Add(welcome);
        var buttons = Flow(); AddConfigurationButton(buttons, L10n.Get("New"), () => AddAsync(true), UiGlyph.Plus); AddConfigurationButton(buttons, L10n.Get("Import"), () => AddAsync(false), UiGlyph.Database); AddConfigurationButton(buttons, L10n.Get("ScanLocalInstances2"), ScanAsync);
        panel.Controls.Add(buttons); emptyCard.Controls.Add(panel);
    }
    private static FlowLayoutPanel Flow() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
    private ToolStripMenuItem LanguageMenu()
    {
        var menu = new ToolStripMenuItem(L10n.Get("Language"));
        foreach (var (code, label) in new (string?, string)[] { (null, L10n.Get("FollowSystem")), ("zh-CN", L10n.Get("LanguageSimplifiedChinese")), ("en-US", L10n.Get("LanguageEnglish")), ("zh-TW", L10n.Get("LanguageTraditionalChinese")) })
        {
            var item = new ToolStripMenuItem(label) { Checked = preferences.Language == code, Tag = code };
            item.Click += async (_, _) => await RunAsync(async () => {
                if (preferences.Language == code) return;
                await preferences.SaveLanguageAsync(code);
                foreach (ToolStripMenuItem option in menu.DropDownItems) option.Checked = (string?)option.Tag == code;
                if (!sessionEnding) MessageBox.Show(this, L10n.Get("LanguageRestart"), "PgHatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }, requiresConfiguration: false);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }
    private Button NewButton(string text, bool primary = false, UiGlyph? glyph = null)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
            Padding = new(glyph is null ? 11 : 7, 6, glyph is null ? 11 : 7, 6), Margin = new(0, 0, 8, 6),
            FlatAppearance = { BorderColor = primary ? Accent : Color.FromArgb(216, 224, 235), BorderSize = 1 } };
        if (glyph is { } icon) uiIcons.Bind(button, icon, light: primary);
        return button;
    }
    private Button Button(string text, Func<Task> action, bool requiresConfiguration = true, UiGlyph? glyph = null)
    {
        var button = NewButton(text, glyph: glyph); button.Click += async (_, _) => await RunAsync(action, requiresConfiguration); return button;
    }
    private ToolStripMenuItem MenuItem(string text, Func<Task> action, bool requiresConfiguration = true, bool configure = false, UiGlyph? glyph = null)
    {
        var item = new ToolStripMenuItem(text);
        if (glyph is { } icon) uiIcons.Bind(item, icon);
        item.Click += async (_, _) => await RunAsync(action, requiresConfiguration, configure);
        if (requiresConfiguration) configurationMenuItems.Add(item);
        return item;
    }
    private Button DropDownButton(string text, UiGlyph glyph, params ToolStripItem[] items)
    {
        var button = NewButton(text, glyph: glyph);
        var menu = new ContextMenuStrip { ShowImageMargin = true, Font = Font };
        menu.Items.AddRange(items); dropDownMenus.Add(menu);
        // Native menus handle keyboard navigation, dismissal and DPI sizing.
        menu.Opening += (_, _) => { UpdateCommandState(); uiIcons.UpdateMenu(menu, DeviceDpi); };
        button.Click += (_, _) => { button.Focus(); menu.Show(button, new Point(0, button.Height)); };
        return button;
    }
    private void AddConfigurationButton(FlowLayoutPanel parent, string text, Func<Task> action, UiGlyph? glyph = null)
    {
        var button = NewButton(text, glyph: glyph); button.Click += async (_, _) => await RunAsync(action, configure: true); parent.Controls.Add(button); configurationButtons.Add(button);
    }
    private void ManagerChanged()
    {
        if (IsHandleCreated && !IsDisposed && !closing) try { BeginInvoke(() => { if (!IsDisposed && !closing) Render(); }); } catch (InvalidOperationException) { }
    }
    private async Task RunAsync(Func<Task> action, bool requiresConfiguration = true, bool configure = false)
    {
        if (closing || (requiresConfiguration && !loaded) || (configure && configuring)) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); actions.Add(completion.Task);
        if (configure) configuring = true; Render(); status.Text = L10n.Get("Working");
        try { await action(); if (!closing) status.Text = L10n.Format("CloseToHideInTrayCtrlQ", L10n.Instances(manager.Config.Instances.Count)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!closing && !IsDisposed) { status.Text = ex.Message; if (ex is ConfigurationConflictException) { recoveryText.Text = ex.Message; recovery.Visible = true; } MessageBox.Show(this, ex.Message, "PgHatch", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
        finally { if (configure) configuring = false; actions.Remove(completion.Task); completion.SetResult(); if (!closing && !IsDisposed) Render(); }
    }
    private async Task LoadAsync()
    {
        if (configuring || operating.Count > 0) { status.Text = L10n.Get("WaitForTheCurrentOperationToFinish"); return; }
        timer.Stop(); loaded = false; detailCancellation?.Cancel(); settings.Clear();
        try { await manager.LoadAsync(); }
        catch (Exception ex) { recoveryText.Text = L10n.Get("CouldNotLoadConfigurationTheOriginalFile") + ex.Message; recovery.Visible = true; Render(); return; }
        recovery.Visible = false; loaded = true;
        // Discovery is a user-confirmed action in program management; startup never registers programs.
        await manager.RefreshAllAsync(); Render(); timer.Start();
    }
    private async Task RecoverAsync(bool fromBackup)
    {
        var text = fromBackup ? L10n.Format("ValidateAndRestoreBakTheCurrentFile", Path.GetFileName(store.RecoveryFilePath)) : L10n.Get("PreserveTheCurrentFileAndCreateAn");
        if (MessageBox.Show(this, text, L10n.Get("RestoreConfiguration"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await manager.RecoverConfigurationAsync(fromBackup); await LoadAsync();
    }
    private void Render()
    {
        foreach (var obsolete in grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is InstanceConfig i && !manager.Config.Instances.Any(x => x.Id == i.Id)).ToArray()) grid.Rows.Remove(obsolete);
        foreach (var instance in manager.Config.Instances)
        {
            var live = manager.Statuses.GetValueOrDefault(instance.Id); var state = live?.State ?? InstanceState.Unknown;
            var values = new[] { instance.Name, manager.FindBinary(instance)?.Version ?? L10n.Get("NotLinked"), manager.BinaryProblem(instance) is null ? StateText(state) : L10n.Get("BinariesUnavailable"), (live?.ActualPort ?? settings.GetValueOrDefault(instance.Id)?.Port ?? instance.Port).ToString() };
            var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as InstanceConfig)?.Id == instance.Id);
            if (row is null) { row = grid.Rows[grid.Rows.Add(values)]; row.Tag = instance; }
            for (var col = 0; col < values.Length; col++) if (!Equals(row.Cells[col].Value, values[col])) row.Cells[col].Value = values[col];
            row.Tag = instance; row.Cells[2].Style.ForeColor = row.Cells[2].Style.SelectionForeColor = StateColor(state);
        }
        if (grid.SelectedRows.Count == 0 && grid.Rows.Count > 0) grid.Rows[0].Selected = true;
        var count = manager.Statuses.Values.Count(s => s.ServerRunning == true);
        summary.Text = L10n.Format("RunningV", L10n.Instances(manager.Config.Instances.Count), L10n.Running(count), AppVersion.Current);
        if (tray is not null) tray.Text = L10n.Format("PgHatchRunning", L10n.Instances(count));
        UpdateCommandState();
        UpdateDetails();
        if (Selected is { } current && !settings.ContainsKey(current.Id) && detailCancellation is null && loaded && !closing) _ = LoadSelectedSettingsAsync();
    }
    private static string StateText(InstanceState state) => state switch { InstanceState.Running => L10n.Get("Running"), InstanceState.Stopped => L10n.Get("Stopped"), InstanceState.Starting => L10n.Get("Starting"), InstanceState.Stopping => L10n.Get("Stopping"), InstanceState.Error => L10n.Get("Error"), _ => L10n.Get("Unknown") };
    private static Color StateColor(InstanceState state) => state switch { InstanceState.Running => Color.FromArgb(28, 122, 81), InstanceState.Starting or InstanceState.Stopping => Color.FromArgb(159, 99, 16), InstanceState.Error => Color.FromArgb(181, 54, 54), _ => Muted };
    private void UpdateRowHeight()
    {
        grid.RowTemplate.Height = Math.Max(28, (int)Math.Ceiling(Font.GetHeight() + 16 * DeviceDpi / 96F));
        foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
        foreach (DataGridViewColumn column in grid.Columns)
            column.MinimumWidth = TextRenderer.MeasureText(column.HeaderText, Font).Width + (int)(18 * DeviceDpi / 96F);
    }
    private void ClampToWorkingArea()
    {
        var work = Screen.FromControl(this).WorkingArea;
        MinimumSize = new(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
        if (Width > work.Width || Height > work.Height) Size = new(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
    }
    private void UpdateDetails()
    {
        var instance = Selected; detailCard.Visible = instance is not null; emptyCard.Visible = instance is null;
        UpdateCommandState();
        if (instance is null) { start.Enabled = stop.Enabled = restart.Enabled = false; return; }
        var binary = manager.FindBinary(instance); var problem = manager.BinaryProblem(instance); var live = manager.Statuses.GetValueOrDefault(instance.Id); var configured = settings.GetValueOrDefault(instance.Id);
        heading.Text = instance.Name; badge.Text = StateText(live?.State ?? InstanceState.Unknown); badge.ForeColor = StateColor(live?.State ?? InstanceState.Unknown); badge.BackColor = Color.FromArgb(243, 246, 250);
        fields["PostgreSQL"].Text = binary?.Version ?? L10n.Get("NotLinked"); fields[L10n.Get("Binaries")].Text = binary?.BinDirectory ?? L10n.Get("InstanceSettingsChooseCompatibleBinaries"); fields["PGDATA"].Text = instance.DataDirectory;
        if (problem is not null) badge.Text = L10n.Get("BinariesUnavailableRepairInInstanceSettings");
        fields[L10n.Get("ListenAddress")].Text = live?.ListenAddress ?? (configured is null ? L10n.Get("ReadingOriginalConfiguration") : configured.ListenAddresses.Length == 0 ? L10n.Get("TCPListeningIsDisabled") : configured.ListenAddresses);
        if (problem is not null) fields[L10n.Get("ListenAddress")].Text = L10n.Get("UnknownBinariesUnavailable");
        fields[L10n.Get("Port")].Text = (live?.ActualPort ?? configured?.Port ?? instance.Port).ToString();
        fields[L10n.Get("Connection")].Text = live?.Connection switch { ConnectionState.Ready => L10n.Get("ReadyAcceptingConnectionsPIDVerified"), ConnectionState.NotReady => L10n.Get("NotReadyNotAcceptingConnectionsYet"), _ => live?.State == InstanceState.Stopped ? L10n.Get("ServerProcessIsStopped") : L10n.Get("UnknownReadinessNotConfirmed") };
        if (problem is not null) fields[L10n.Get("Connection")].Text = L10n.Get("UnknownBinariesNotLinkedOrUnavailable");
        var available = loaded && !closing && !operating.Contains(instance.Id) && problem is null;
        start.Enabled = available && (live?.State is InstanceState.Stopped or InstanceState.Error or InstanceState.Unknown);
        stop.Enabled = restart.Enabled = available && live?.ServerRunning == true;
        stop.Text = L10n.Get(problem is null ? "Stop" : "EmergencyStop");
        if (problem is not null) stop.Enabled = loaded && !closing && !operating.Contains(instance.Id) && manager.EmergencyCandidates.ContainsKey(instance.Id);
    }
    private void UpdateCommandState()
    {
        var configurable = loaded && !configuring && !closing;
        foreach (var button in configurationButtons) button.Enabled = configurable;
        foreach (var item in configurationMenuItems) item.Enabled = configurable;
        var selected = Selected;
        foreach (var control in instanceControls) control.Enabled = loaded && !closing && selected is not null;
        foreach (var item in instanceMenuItems) item.Enabled = configurable && selected is not null && !operating.Contains(selected.Id);
    }
    private async Task LoadSelectedSettingsAsync()
    {
        var instance = Selected; detailCancellation?.Cancel();
        if (instance is null || !loaded || closing || settings.ContainsKey(instance.Id) || manager.BinaryProblem(instance) is not null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); detailCancellation = cancellation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); actions.Add(completion.Task);
        try { var result = await tools.ReadSettingsAsync(manager.Binary(instance), instance.DataDirectory, cancellation.Token); if (!closing) { settings[instance.Id] = result; UpdateDetails(); } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing && Selected?.Id == instance.Id) { fields[L10n.Get("ListenAddress")].Text = L10n.Get("CouldNotReadConfigurationSeeStatusDetails"); status.Text = ex.Message; } }
        finally { if (ReferenceEquals(detailCancellation, cancellation)) detailCancellation = null; cancellation.Dispose(); actions.Remove(completion.Task); completion.SetResult(); }
    }
    private InstanceConfig RequireSelected() => Selected ?? throw new InvalidOperationException(L10n.Get("SelectAnInstanceFirst"));
    private async Task OperateAsync(string operation)
    {
        var instance = Selected; if (instance is null || operating.Contains(instance.Id)) return;
        operating.Add(instance.Id);
        try
        {
            await RunAsync(async () => {
                if (operation != "stop" || manager.BinaryProblem(instance) is null) { await manager.OperateAsync(instance, operation); return; }
                var candidates = await manager.GetEmergencyCandidatesAsync(instance);
                Guid chosen;
                if (candidates.Count == 1 && candidates[0].Id == instance.BinaryId) chosen = candidates[0].Id;
                else
                {
                    using var dialog = new EmergencyToolForm(candidates);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    chosen = dialog.SelectedBinaryId;
                }
                var binary = candidates.Single(b => b.Id == chosen);
                if (MessageBox.Show(this, L10n.Format("EmergencyStopConfirm", instance.Name, instance.DataDirectory, binary.Version, binary.BinDirectory),
                    L10n.Get("EmergencyStop"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                await manager.EmergencyStopAsync(instance, chosen);
                MessageBox.Show(this, L10n.Get("EmergencyStoppedRepairRequired"), "PgHatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }
        finally { operating.Remove(instance.Id); if (!closing) Render(); }
    }
    private async Task RefreshVersionsAsync()
    {
        settings.Clear(); await manager.RefreshAllAsync();
        var abnormal = manager.Config.Binaries.Where(b => manager.BinaryProblems.ContainsKey(b.Id)).ToArray();
        if (abnormal.Length > 0)
        {
            var paths = string.Join("\r\n", abnormal.Select(b => b.BinDirectory));
            var linked = manager.Config.Instances.Where(i => abnormal.Any(b => b.Id == i.BinaryId)).Select(i => i.Name + " — " + i.DataDirectory);
            if (MessageBox.Show(this, L10n.Format("BinaryRepairConfirm", paths, string.Join("\r\n", linked)), L10n.Get("Refresh"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        }
        await manager.RefreshAllAsync(refreshVersions: true); await LoadSelectedSettingsAsync();
    }
    private Task BinariesAsync() { using var dialog = new BinaryForm(manager, store); dialog.ShowDialog(this); return Task.CompletedTask; }
    private Task ScanAsync() { using var dialog = new DiscoveryForm(manager, tools); dialog.ShowDialog(this); return Task.CompletedTask; }
    private async Task AddAsync(bool create)
    {
        if (manager.Config.Binaries.Count == 0)
        {
            if (!create && !importProgramHintShown)
            {
                importProgramHintShown = true;
                MessageBox.Show(this, L10n.Get("NoPostgreSQLBinaryDirectoryIsConfiguredYet"), L10n.Get("ImportInstance2"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            await BinariesAsync(); if (manager.Config.Binaries.Count == 0) return;
        }
        using var dialog = new InstanceForm(manager.Config.Binaries, tools, create);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            status.Text = create ? L10n.Get("InitializingDataDirectory") : L10n.Get("Working");
            if (!create && PathSafety.IsNetworkStorage(dialog.DataDirectory) && MessageBox.Show(this, L10n.Get("NetworkImportWarning"), "PgHatch", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            await manager.AddInstanceAsync(dialog.InstanceName, dialog.Binary, dialog.DataDirectory, dialog.Port, create ? dialog.Username : null, create ? dialog.Password : null);
        }
    }
    private async Task SettingsAsync()
    {
        var instance = RequireSelected(); using var dialog = new InstanceSettingsForm(instance, manager);
        dialog.ShowDialog(this);
        settings.Remove(instance.Id); await LoadSelectedSettingsAsync();
    }
    private async Task RemoveAsync()
    {
        var instance = RequireSelected();
        if (MessageBox.Show(this, L10n.Format("UnregisterRNDataAndLogsAreKept", instance.Name), L10n.Get("UnregisterInstance2"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
        { await manager.RemoveAsync(instance); settings.Remove(instance.Id); }
    }
    private async Task LogsAsync()
    {
        var instance = RequireSelected(); using var dialog = new LogForm(instance, manager.LastOperationDiagnostics.GetValueOrDefault(instance.Id) + "\r\n" + manager.Statuses.GetValueOrDefault(instance.Id)?.Detail);
        await dialog.LoadLogAsync(); dialog.ShowDialog(this);
    }
    internal static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new IOException(L10n.Format("DirectoryMissing", path));
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { path } });
    }
    private async Task RequestExitAsync(bool stopAll)
    {
        if (closing) return;
        if (stopAll && MessageBox.Show(this, L10n.Get("StopAllRunningRegisteredInstancesInFast"), L10n.Get("StopInstancesAndExit"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        closing = true; timer.Stop(); detailCancellation?.Cancel(); status.Text = L10n.Get("WaitingForCurrentOperationsBeforeExiting"); Render();
        try
        {
            await Task.WhenAll(actions.ToArray());
            if (sessionEnding) return;
            if (stopAll)
                foreach (var instance in manager.Config.Instances.ToArray()) await manager.OperateAsync(instance, "stop");
            lifetime.Cancel(); await manager.ShutdownAsync();
            if (sessionEnding) return;
            exiting = true; Close();
        }
        catch (Exception ex) { if (sessionEnding) return; closing = false; status.Text = ex.Message; MessageBox.Show(this, ex.Message, L10n.Get("CouldNotExit"), MessageBoxButtons.OK, MessageBoxIcon.Error); timer.Start(); Render(); }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WindowsSessionShutdown.QueryEndSession)
        {
            // Consent without closing or hiding. Another application may still cancel shutdown.
            var logoff = (message.LParam.ToInt64() & WindowsSessionShutdown.Logoff) != 0;
            WindowsSessionShutdown.RegisterReason(Handle, L10n.Get(logoff ? "WaitingForCurrentOperationsBeforeExiting" : "SystemShutdownStopping"));
            message.Result = new IntPtr(1); return;
        }
        if (message.Msg == WindowsSessionShutdown.EndSession)
        {
            if (message.WParam == IntPtr.Zero)
            {
                WindowsSessionShutdown.ClearReason(Handle); message.Result = IntPtr.Zero; return;
            }
            if (sessionEnding) { message.Result = IntPtr.Zero; return; }
            sessionEnding = WindowsSessionShutdown.IsEnding = true;
            closing = true; timer.Stop(); detailCancellation?.Cancel();
            foreach (Form window in Application.OpenForms.Cast<Form>().ToArray()) window.Enabled = false;
            var stopInstances = (message.LParam.ToInt64() & WindowsSessionShutdown.Logoff) == 0;
            status.Text = L10n.Get(stopInstances ? "SystemShutdownStopping" : "WaitingForCurrentOperationsBeforeExiting"); Render();
            try
            {
                var cleanup = manager.ShutdownAsync(stopInstances);
                var completed = WindowsSessionShutdown.CompleteWithin(cleanup, TimeSpan.FromSeconds(30));
                var result = !completed ? "Session cleanup deadline exceeded; shutdown could not be confirmed."
                    : cleanup.IsFaulted ? cleanup.Exception!.ToString()
                    : cleanup.IsCanceled ? "Session cleanup was cancelled; shutdown could not be confirmed." : "Session cleanup completed.";
                if (!completed) _ = cleanup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                WriteSessionShutdownLog(result + Environment.NewLine + string.Join(Environment.NewLine,
                    manager.Config.Instances.Select(i => $"{i.Name}: {manager.Statuses.GetValueOrDefault(i.Id)}\n{manager.LastOperationDiagnostics.GetValueOrDefault(i.Id)}")));
            }
            catch (Exception ex) { WriteSessionShutdownLog(ex.ToString()); }
            finally { WindowsSessionShutdown.ClearReason(Handle); exiting = true; }
            // Let WinForms complete the confirmed session end; never route it through tray hiding.
        }
        base.WndProc(ref message);
    }
    private void WriteSessionShutdownLog(string text)
    {
        try
        {
            var directory = Path.Combine(store.Root, "logs"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "pghatch-session-end.log"), $"{DateTimeOffset.Now:O}\n{text}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }
    private void RestoreWindow() { if (IsDisposed) return; Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) tray?.Dispose();
        base.Dispose(disposing);
        if (disposing) { brandLogo.Image?.Dispose(); uiIcons.Dispose(); applicationIcons.Dispose(); }
    }
}


