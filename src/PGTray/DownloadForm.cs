using System.Runtime.InteropServices;
using PGTray.Core;

namespace PGTray;

internal sealed class DownloadForm : Form
{
    private sealed record Choice(PostgresPackage Package) { public override string ToString() => $"PostgreSQL {Package.Version}-{Package.Revision} · Windows {Package.Architecture}"; }
    private readonly CancellationTokenSource lifetime = new();
    private readonly PostgresCatalogService catalog;
    private readonly PackageDownloader downloader = new();
    private readonly ConfigStore store;
    private readonly InstanceManager manager;
    private readonly PostgresTools tools = new(new ProcessRunner());
    private readonly ComboBox versions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox target = new() { Dock = DockStyle.Fill };
    private readonly TextBox source = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = true, Height = 72, ScrollBars = ScrollBars.None };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true, Margin = new(0, 10, 0, 12) };
    private readonly TableLayoutPanel layout = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(16), ColumnCount = 3, RowCount = 7 };
    private readonly ToolTip detailsTip = new();
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Height = 22 };
    private readonly Button start = new() { AutoSize = true }, cancel = new() { AutoSize = true }, browse = new() { AutoSize = true };
    private CancellationTokenSource? operation;
    private bool busy, closingRequested, resourcesDisposed, fitting;
    public bool Installed { get; private set; }

    public DownloadForm(InstanceManager manager, ConfigStore store)
    {
        this.manager = manager; this.store = store;
        catalog = new(Path.Combine(AppContext.BaseDirectory, "postgresql-catalog.xml"), store);
        Text = L10n.Get("DownloadTitle"); Font = new Font("Microsoft YaHei UI", 9F); AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; ClientSize = new(780, 360); MinimumSize = new(650, 330); MinimizeBox = false; MaximizeBox = false;
        layout.ColumnStyles.Add(new(SizeType.AutoSize)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.AutoSize));
        void Row(int row, string label, Control field, Control? trailing = null)
        {
            layout.RowStyles.Add(new(SizeType.AutoSize)); layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 6, 12, 8) }, 0, row);
            field.Margin = new(0, 4, 8, 8); layout.Controls.Add(field, 1, row);
            if (trailing is not null) layout.Controls.Add(trailing, 2, row); else layout.SetColumnSpan(field, 2);
        }
        Row(0, L10n.Get("Version"), versions); browse.Text = L10n.Get("Browse"); Row(1, L10n.Get("DeployTarget"), target, browse);
        Row(2, L10n.Get("DownloadSource"), source);
        var note = new Label { Text = L10n.Get("DownloadNote") + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? Environment.NewLine + L10n.Get("DownloadArmNote") : ""), AutoSize = true, Dock = DockStyle.Fill };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.Controls.Add(note, 0, 3); layout.SetColumnSpan(note, 3);
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.Controls.Add(progressBar, 0, 4); layout.SetColumnSpan(progressBar, 3);
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.Controls.Add(status, 0, 5); layout.SetColumnSpan(status, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        start.Text = L10n.Get("DownloadBegin"); cancel.Text = L10n.Get("Close"); actions.Controls.Add(cancel); actions.Controls.Add(start);
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.Controls.Add(actions, 0, 6); layout.SetColumnSpan(actions, 3); Controls.Add(layout);
        start.Enabled = false; AcceptButton = start; CancelButton = cancel;
        versions.SelectedIndexChanged += (_, _) => { if (versions.SelectedItem is Choice c) source.Text = $"{c.Package.Provider} · {c.Package.DownloadUrl}\r\nSHA-256: {c.Package.SHA256}"; };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = L10n.Get("DeployTarget"), UseDescriptionForTitle = true, ShowNewFolderButton = true }; if (picker.ShowDialog(this) == DialogResult.OK) target.Text = picker.SelectedPath; };
        start.Click += async (_, _) => await InstallAsync();
        cancel.Click += (_, _) => { if (busy) { operation?.Cancel(); cancel.Enabled = false; } else Close(); };
        Shown += async (_, _) => { FitContent(); await LoadCatalogAsync(); };
        SizeChanged += (_, _) => FitContent(); source.SizeChanged += (_, _) => FitContent(); source.TextChanged += (_, _) => FitContent(); status.TextChanged += (_, _) => FitContent();
        DpiChanged += (_, _) => BeginInvoke(FitContent);
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; closingRequested = true; operation?.Cancel(); cancel.Enabled = false; } else lifetime.Cancel(); };
    }

    private async Task LoadCatalogAsync()
    {
        status.Text = L10n.Get("CatalogLoading");
        try
        {
            var result = await catalog.GetAsync(cancellationToken: lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            foreach (var package in result.Catalog.Packages) versions.Items.Add(new Choice(package));
            if (versions.Items.Count > 0) versions.SelectedIndex = 0;
            status.Text = result.Warning is null ? L10n.Get("DownloadChooseTarget") : L10n.Get("CatalogOfflineHint");
            detailsTip.SetToolTip(status, result.Warning); start.Enabled = versions.Items.Count > 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) { status.Text = L10n.Get("CatalogUnavailableShort"); MessageBox.Show(this, L10n.Format("CatalogLoadFailed", ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    }
    private async Task InstallAsync()
    {
        if (busy || versions.SelectedItem is not Choice selected) return;
        var destination = target.Text;
        busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        start.Enabled = versions.Enabled = target.Enabled = browse.Enabled = false; cancel.Text = L10n.Get("Cancel");
        var reporter = new Progress<PackageProgress>(UpdateProgress);
        try
        {
            await Task.Run(() => PackageDeployment.ValidateTarget(destination, manager), operation.Token);
            var archive = await downloader.DownloadAsync(selected.Package, Path.Combine(store.Root, "cache"), reporter, operation.Token);
            var result = await new PackageDeployment().DeployAsync(selected.Package, archive, destination, tools, manager, reporter, operation.Token);
            Installed = true; status.Text = L10n.Format("DownloadReady", result.Binary.BinDirectory);
            progressBar.Style = ProgressBarStyle.Blocks; progressBar.Value = 100;
            if (result.CleanupWarning is not null) MessageBox.Show(this, result.CleanupWarning, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException ex) { status.Text = L10n.Get("DownloadCanceled"); if (CleanupNotice(ex) is { Length: > 0 } warning) MessageBox.Show(this, warning, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch (Exception ex) { status.Text = L10n.Get("DownloadFailedShort"); MessageBox.Show(this, L10n.Format("DownloadFailed", ex.Message) + CleanupNotice(ex), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            busy = false; operation.Dispose(); operation = null;
            if (!IsDisposed)
            {
                progressBar.Style = ProgressBarStyle.Blocks;
                cancel.Text = L10n.Get("Close"); cancel.Enabled = true; start.Enabled = !Installed;
                versions.Enabled = target.Enabled = browse.Enabled = !Installed;
                if (closingRequested) Close();
            }
        }
    }
    private void UpdateProgress(PackageProgress value)
    {
        if (IsDisposed || !busy) return;
        cancel.Enabled = value.Stage is not (PackageStage.Registering or PackageStage.Complete);
        status.Text = value.Stage switch {
            PackageStage.Downloading => L10n.Format("DownloadTransferProgress", ByteText(value.Bytes), value.Total is { } total ? ByteText(total) : L10n.Get("Unknown"), ByteText((long)value.BytesPerSecond), value.Total is > 0 ? (value.Bytes * 100d / value.Total.Value).ToString("N1") : "—", value.Attempt),
            PackageStage.Verifying => L10n.Get("DownloadVerifying"), PackageStage.Extracting => L10n.Get("DownloadExtracting"),
            PackageStage.Registering => L10n.Get("DownloadRegistering"), _ => L10n.Get("DownloadFinished")
        };
        progressBar.Style = value.Stage == PackageStage.Downloading && value.Total is > 0 ? ProgressBarStyle.Blocks : ProgressBarStyle.Marquee;
        if (value.Stage == PackageStage.Downloading && value.Total is > 0) progressBar.Value = (int)Math.Clamp(value.Bytes * 100d / value.Total.Value, 0, 100);
    }
    private static string ByteText(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):N1} MiB" : $"{bytes / 1024d:N1} KiB";
    private void FitContent()
    {
        if (fitting || !IsHandleCreated || IsDisposed) return;
        fitting = true;
        try
        {
            var work = Screen.FromControl(this).WorkingArea; var frame = SizeFromClientSize(Size.Empty); var padding = (int)Math.Ceiling(8 * DeviceDpi / 96d);
            MinimumSize = new(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
            if (Width > work.Width) Width = work.Width;
            status.MaximumSize = new(Math.Max(80, ClientSize.Width - layout.Padding.Horizontal), 0);
            source.Height = TextRenderer.MeasureText(source.Text.Length == 0 ? " " : source.Text, source.Font,
                new(Math.Max(80, source.ClientSize.Width - padding), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + padding * 2;
            layout.PerformLayout();
            ClientSize = new(ClientSize.Width, Math.Min(work.Height - frame.Height, Math.Max(MinimumSize.Height - frame.Height, layout.PreferredSize.Height + padding)));
            Location = new(Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width)), Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height)));
        }
        finally { fitting = false; }
    }
    private static string CleanupNotice(Exception ex) => ex.Data["DeploymentCleanupWarning"] is string warning ? Environment.NewLine + warning : "";
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; lifetime.Cancel(); operation?.Cancel(); catalog.Dispose(); downloader.Dispose(); detailsTip.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
