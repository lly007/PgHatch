using PGTray.Core;

namespace PGTray;

/// <summary>Metadata preview only; no package download or registration actions.</summary>
internal sealed class CatalogForm : Form
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly PostgresCatalogService service;
    private readonly Button refresh = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly ListView packages = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private bool resourcesDisposed;

    public CatalogForm(ConfigStore store)
    {
        service = new(Path.Combine(AppContext.BaseDirectory, "postgresql-catalog.xml"), store);
        Text = L10n.Get("CatalogTitle"); Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(780, 480); MinimumSize = new(620, 400); StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 55));
        layout.RowStyles.Add(new(SizeType.Percent, 45)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = L10n.Get("CatalogNote"), AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 0, 0, 10) }, 0, 0);
        foreach (var text in new[] { L10n.Get("Version"), L10n.Get("CatalogArchitecture"), L10n.Get("CatalogProvider") }) packages.Columns.Add(text, 180);
        layout.Controls.Add(packages, 0, 1); layout.Controls.Add(details, 0, 2); layout.Controls.Add(status, 0, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = L10n.Get("Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
        refresh.Text = L10n.Get("CatalogRefresh"); actions.Controls.Add(close); actions.Controls.Add(refresh);
        layout.Controls.Add(actions, 0, 4); Controls.Add(layout); CancelButton = close;
        packages.SelectedIndexChanged += (_, _) => {
            if (packages.SelectedItems.Count == 0) { details.Clear(); return; }
            var package = (PostgresPackage)packages.SelectedItems[0].Tag!;
            details.Text = $"{package.DownloadUrl}\r\nSHA-256: {package.SHA256}\r\n{package.ChecksumSource}";
        };
        refresh.Click += async (_, _) => await LoadCatalogAsync(true);
        Shown += async (_, _) => await LoadCatalogAsync(false);
        FormClosing += (_, _) => lifetime.Cancel();
    }

    private async Task LoadCatalogAsync(bool force)
    {
        refresh.Enabled = false; status.Text = L10n.Get("CatalogLoading");
        try
        {
            var result = await service.GetAsync(forceRefresh: force, cancellationToken: lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            packages.Items.Clear(); details.Clear();
            foreach (var package in result.Catalog.Packages)
                packages.Items.Add(new ListViewItem([package.Version + "-" + package.Revision, package.Architecture, package.Provider]) { Tag = package });
            var source = result.Source switch {
                CatalogSource.Online => L10n.Get("CatalogSourceOnline"), CatalogSource.Cache => L10n.Get("CatalogSourceCache"), _ => L10n.Get("CatalogSourceBundled")
            };
            status.Text = L10n.Format("CatalogStatus", source, result.Catalog.Packages.Count) +
                (result.Warning is null ? "" : Environment.NewLine + result.Warning);
            if (packages.Items.Count != 0) packages.Items[0].Selected = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = L10n.Format("CatalogLoadFailed", ex.Message); }
        finally { if (!IsDisposed) refresh.Enabled = true; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; lifetime.Cancel(); service.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
