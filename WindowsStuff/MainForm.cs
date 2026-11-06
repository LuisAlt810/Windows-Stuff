using System.Drawing;
using System.Text.Json;

namespace WindowsStuff;

internal sealed class MainForm : Form
{
    private static readonly string[] UiTerms = ["copilot", "widget", "webexperience", "bing", "search"];
    private static readonly string[] BackgroundTerms = ["xbox", "feedback", "gethelp", "phone", "clipchamp", "tips"];
    private static readonly Color Ink = Color.FromArgb(34, 43, 48);
    private static readonly Color Muted = Color.FromArgb(92, 106, 112);
    private static readonly Color Accent = Color.FromArgb(0, 115, 105);

    private readonly PowerShellRunner _runner = new();
    private readonly HashSet<string> _selectedPackages = new(StringComparer.Ordinal);
    private readonly ListView _uiPackages;
    private readonly ListView _backgroundPackages;
    private readonly ListView _allPackages;
    private readonly RichTextBox _log = new();
    private readonly Label _status = new();
    private readonly TextBox _search = new();
    private readonly CheckBox _restorePoint = new();
    private readonly CheckBox _forceMode = new();
    private readonly Button _scanButton = new();
    private readonly Button _removeButton = new();
    private readonly NotifyIcon _trayIcon;
    private InstalledPackage[] _packages = [];
    private bool _exitRequested;
    private bool _busy;
    private bool _syncingChecks;

    public MainForm()
    {
        Text = "Windows-Stuff";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 620);
        Size = new Size(1120, 790);
        BackColor = Color.FromArgb(246, 248, 246);
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9F);

        _uiPackages = CreatePackageList();
        _backgroundPackages = CreatePackageList();
        _allPackages = CreatePackageList();
        _trayIcon = CreateTrayIcon();
        BuildInterface();
        FormClosing += OnFormClosing;
        Shown += async (_, _) => await ScanAsync();
    }

    private void BuildInterface()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 12, 18, 14),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
        layout.Controls.Add(BuildHeader(), 0, 0);
        layout.Controls.Add(BuildTabs(), 0, 1);
        layout.Controls.Add(BuildActionBar(), 0, 2);
        layout.Controls.Add(BuildLogPanel(), 0, 3);
        Controls.Add(layout);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = "Windows-Stuff",
            Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(2, 5)
        };
        _status.Text = "Administrator session";
        _status.ForeColor = Accent;
        _status.AutoSize = true;
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _status.Location = new Point(Width - 250, 22);
        panel.Controls.Add(title);
        panel.Controls.Add(_status);
        panel.Resize += (_, _) => _status.Left = panel.ClientSize.Width - _status.Width - 8;
        return panel;
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F) };
        tabs.TabPages.Add(BuildPackagePage("UI & Widgets", _uiPackages,
            "Matching installed packages only. Windows settings and shell integrations are not changed."));
        tabs.TabPages.Add(BuildPackagePage("Telemetry & Services", _backgroundPackages,
            "Matching app packages only. Windows services and telemetry policies are not changed."));
        tabs.TabPages.Add(BuildPackagePage("All Installed Apps", _allPackages,
            "Inventory of installed AppX packages across users. Framework and Windows-protected packages cannot be selected."));
        return tabs;
    }

    private TabPage BuildPackagePage(string title, ListView list, string description)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(12) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = title == "All Installed Apps" ? 3 : 2,
            BackColor = Color.White
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        if (title == "All Installed Apps")
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var descriptionLabel = new Label
        {
            Text = description,
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        layout.Controls.Add(descriptionLabel, 0, 0);
        if (title == "All Installed Apps")
        {
            _search.Dock = DockStyle.Left;
            _search.Width = 320;
            _search.PlaceholderText = "Search package names";
            _search.TextChanged += (_, _) => PopulateList(_allPackages, _packages, null, _search.Text);
            layout.Controls.Add(_search, 0, 1);
        }
        list.Dock = DockStyle.Fill;
        layout.Controls.Add(list, 0, title == "All Installed Apps" ? 2 : 1);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildActionBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 4)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0)
        };
        _restorePoint.Text = "Create restore point before changes";
        _restorePoint.Checked = true;
        _restorePoint.AutoSize = true;
        _restorePoint.ForeColor = Ink;
        _forceMode.Text = "Enable Force Mode";
        _forceMode.AutoSize = true;
        _forceMode.Margin = new Padding(22, 3, 3, 3);
        _forceMode.ForeColor = Color.FromArgb(151, 78, 23);
        options.Controls.Add(_restorePoint);
        options.Controls.Add(_forceMode);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        _removeButton.Text = "Remove selected";
        _removeButton.Width = 142;
        _removeButton.Height = 36;
        _removeButton.BackColor = Accent;
        _removeButton.ForeColor = Color.White;
        _removeButton.FlatStyle = FlatStyle.Flat;
        _removeButton.FlatAppearance.BorderSize = 0;
        _removeButton.Click += async (_, _) => await RemoveSelectedAsync();
        _scanButton.Text = "Scan packages";
        _scanButton.Width = 118;
        _scanButton.Height = 36;
        _scanButton.Margin = new Padding(0, 0, 8, 0);
        _scanButton.Click += async (_, _) => await ScanAsync();
        buttons.Controls.Add(_removeButton);
        buttons.Controls.Add(_scanButton);
        bar.Controls.Add(options, 0, 0);
        bar.Controls.Add(buttons, 1, 0);
        return bar;
    }

    private Control BuildLogPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var label = new Label
        {
            Text = "PowerShell activity",
            Dock = DockStyle.Top,
            Height = 25,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            ForeColor = Ink
        };
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.WordWrap = false;
        _log.BackColor = Color.FromArgb(31, 39, 42);
        _log.ForeColor = Color.FromArgb(221, 231, 227);
        _log.Font = new Font("Consolas", 9F);
        panel.Controls.Add(_log);
        panel.Controls.Add(label);
        return panel;
    }

    private ListView CreatePackageList()
    {
        var list = new ListView
        {
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            BackColor = Color.White,
            ForeColor = Ink
        };
        list.Columns.Add("Package", 255);
        list.Columns.Add("Package full name", 650);
        list.ItemCheck += (_, e) =>
        {
            if (_syncingChecks)
            {
                return;
            }
            if (list.Items[e.Index].Tag is InstalledPackage package && (package.NonRemovable || package.IsFramework))
            {
                e.NewValue = e.CurrentValue;
                return;
            }

            var packageId = ((InstalledPackage)list.Items[e.Index].Tag!).PackageFullName;
            if (e.NewValue == CheckState.Checked)
            {
                _selectedPackages.Add(packageId);
            }
            else
            {
                _selectedPackages.Remove(packageId);
            }
            BeginInvoke((Action)SynchronizeCheckedRows);
        };
        return list;
    }

    private void SynchronizeCheckedRows()
    {
        _syncingChecks = true;
        try
        {
            foreach (var list in new[] { _uiPackages, _backgroundPackages, _allPackages })
            {
                foreach (ListViewItem item in list.Items)
                {
                    var packageId = ((InstalledPackage)item.Tag!).PackageFullName;
                    item.Checked = _selectedPackages.Contains(packageId);
                }
            }
        }
        finally
        {
            _syncingChecks = false;
        }
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Windows-Stuff", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _exitRequested = true;
            Close();
        });
        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Windows-Stuff",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        return icon;
    }

    private async Task ScanAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Scanning packages...");
        AppendLog("Starting read-only package scan.");
        try
        {
            var result = await _runner.RunAsync(
                new WorkerRequest { Action = "scan" },
                AppendLog,
                CancellationToken.None);
            if (result is null)
            {
                throw new InvalidOperationException("The package scan returned no inventory.");
            }

            _packages = JsonSerializer.Deserialize<InstalledPackage[]>(result.Value.GetRawText()) ?? [];
            _selectedPackages.IntersectWith(_packages.Select(package => package.PackageFullName));
            PopulateList(_allPackages, _packages, null, _search.Text);
            PopulateList(_uiPackages, _packages, UiTerms, string.Empty);
            PopulateList(_backgroundPackages, _packages, BackgroundTerms, string.Empty);
            AppendLog($"Found {_packages.Length} unique installed package identities.");
            _status.Text = $"{_packages.Length} packages scanned";
        }
        catch (Exception exception)
        {
            AppendLog($"Scan failed: {exception.Message}");
            _status.Text = "Scan failed";
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    private async Task RemoveSelectedAsync()
    {
        if (_busy)
        {
            return;
        }

        var selected = _selectedPackages.ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Check one or more packages first.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var detail = string.Join(Environment.NewLine, selected.Take(8));
        if (selected.Length > 8)
        {
            detail += $"{Environment.NewLine}... and {selected.Length - 8} more";
        }
        var mode = _forceMode.Checked
            ? "Force Mode will also stop processes running from these package folders."
            : "Standard package servicing will be used.";
        var prompt = $"Remove these {selected.Length} package(s) for all users?\n\n{detail}\n\n{mode}";
        if (MessageBox.Show(this, prompt, "Confirm package removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        SetBusy(true, "Applying selected package changes...");
        try
        {
            await _runner.RunAsync(
                new WorkerRequest
                {
                    Action = "remove",
                    PackageFullNames = selected,
                    ForceMode = _forceMode.Checked,
                    CreateRestorePoint = _restorePoint.Checked
                },
                AppendLog,
                CancellationToken.None);
            _status.Text = "Package operation finished";
            await ScanWhileBusyAsync();
        }
        catch (Exception exception)
        {
            AppendLog($"Removal stopped: {exception.Message}");
            _status.Text = "Package operation stopped";
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    private async Task ScanWhileBusyAsync()
    {
        AppendLog("Refreshing package inventory.");
        var result = await _runner.RunAsync(
            new WorkerRequest { Action = "scan" },
            AppendLog,
            CancellationToken.None);
        if (result is null)
        {
            throw new InvalidOperationException("The package refresh returned no inventory.");
        }

        _packages = JsonSerializer.Deserialize<InstalledPackage[]>(result.Value.GetRawText()) ?? [];
        _selectedPackages.IntersectWith(_packages.Select(package => package.PackageFullName));
        PopulateList(_allPackages, _packages, null, _search.Text);
        PopulateList(_uiPackages, _packages, UiTerms, string.Empty);
        PopulateList(_backgroundPackages, _packages, BackgroundTerms, string.Empty);
        _status.Text = $"{_packages.Length} packages scanned";
    }

    private void PopulateList(ListView list, IEnumerable<InstalledPackage> packages, string[]? terms, string search)
    {
        list.BeginUpdate();
        _syncingChecks = true;
        list.Items.Clear();
        foreach (var package in packages)
        {
            var searchable = $"{package.Name} {package.PackageFamilyName} {package.PackageFullName}";
            if (terms is not null && !terms.Any(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(search) && !searchable.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = new ListViewItem(package.Name) { Tag = package };
            item.SubItems.Add(package.PackageFullName);
            item.Checked = _selectedPackages.Contains(package.PackageFullName);
            if (package.NonRemovable || package.IsFramework)
            {
                item.ForeColor = Muted;
                item.ToolTipText = package.NonRemovable ? "Windows marks this package as non-removable." : "Framework packages are not selectable.";
            }
            list.Items.Add(item);
        }
        _syncingChecks = false;
        list.EndUpdate();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _scanButton.Enabled = !busy;
        _removeButton.Enabled = !busy;
        _restorePoint.Enabled = !busy;
        _forceMode.Enabled = !busy;
        _uiPackages.Enabled = !busy;
        _backgroundPackages.Enabled = !busy;
        _allPackages.Enabled = !busy;
        _status.Text = status;
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitRequested || e.CloseReason == CloseReason.WindowsShutDown)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
        _trayIcon.ShowBalloonTip(1200, "Windows-Stuff is still running", "Use the tray icon to reopen or exit.", ToolTipIcon.Info);
    }
}