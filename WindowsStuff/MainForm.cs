using System.Drawing;
using System.Text;
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
    private readonly TabControl _workspaceTabs;
    private readonly RichTextBox _log = new();
    private readonly Label _status = new();
    private readonly Label _dashboardPackageCount = new();
    private readonly Label _dashboardQueueCount = new();
    private readonly Label _queueStatus = new();
    private readonly TextBox _search = new();
    private readonly TextBox _uiFilter = new();
    private readonly TextBox _backgroundFilter = new();
    private readonly TextBox _profileName = new();
    private readonly ComboBox _profilePicker = new();
    private readonly CheckBox _restorePoint = new();
    private readonly CheckBox _forceMode = new();
    private readonly Button _scanButton = new();
    private readonly Button _removeButton = new();
    private readonly NotifyIcon _trayIcon;
    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windows-Stuff");
    private UserSettings _settings = new();
    private InstalledPackage[] _packages = [];
    private bool _exitRequested;
    private bool _busy;
    private bool _syncingChecks;
    private bool _historyWriteFailed;

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
        _settings = LoadSettings();
        _trayIcon = CreateTrayIcon();
        _workspaceTabs = BuildTabs();
        BuildInterface();
        RefreshProfilePicker();
        LoadHistory();
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
            RowCount = 3,
            BackColor = BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.Controls.Add(BuildHeader(), 0, 0);
        layout.Controls.Add(_workspaceTabs, 0, 1);
        layout.Controls.Add(BuildActionBar(), 0, 2);
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

    private TabControl BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F) };
        tabs.TabPages.Add(BuildDashboardPage());

        var packagePage = new TabPage("Packages") { BackColor = Color.White, Padding = new Padding(6) };
        var packageTabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F) };
        packageTabs.TabPages.Add(BuildPackagePage("UI & Widgets", _uiPackages,
            "Matching installed packages only. Windows settings and shell integrations are not changed."));
        packageTabs.TabPages.Add(BuildPackagePage("Telemetry & Services", _backgroundPackages,
            "Matching app packages only. Windows services and telemetry policies are not changed."));
        packageTabs.TabPages.Add(BuildPackagePage("All Installed Apps", _allPackages,
            "Inventory of installed AppX packages across users. Every row is selectable; Windows may still refuse protected package removal."));
        packagePage.Controls.Add(packageTabs);
        tabs.TabPages.Add(packagePage);
        tabs.TabPages.Add(BuildActivityPage());
        return tabs;
    }

    private TabPage BuildDashboardPage()
    {
        var page = new TabPage("Dashboard") { BackColor = Color.FromArgb(246, 248, 246), Padding = new Padding(24) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        var heading = new Label
        {
            Text = "Package overview",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 19F, FontStyle.Bold),
            ForeColor = Ink,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _dashboardPackageCount.Text = "Installed packages: not scanned";
        _dashboardPackageCount.Dock = DockStyle.Fill;
        _dashboardPackageCount.Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold);
        _dashboardQueueCount.Text = "Queued for review: 0";
        _dashboardQueueCount.Dock = DockStyle.Fill;
        _dashboardQueueCount.ForeColor = Accent;
        var note = new Label
        {
            Text = "Choose packages in the Packages view. Protected packages may be selected, but Windows can refuse their removal.",
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var browseButton = new Button { Text = "Browse packages", Width = 130, Height = 34 };
        browseButton.Click += (_, _) => _workspaceTabs.SelectedIndex = 1;
        var activityButton = new Button { Text = "View activity", Width = 110, Height = 34 };
        activityButton.Click += (_, _) => _workspaceTabs.SelectedIndex = 2;
        var exportCsvButton = new Button { Text = "Export CSV", Width = 100, Height = 34 };
        exportCsvButton.Click += (_, _) => ExportInventory("csv");
        var exportJsonButton = new Button { Text = "Export JSON", Width = 100, Height = 34 };
        exportJsonButton.Click += (_, _) => ExportInventory("json");
        actions.Controls.Add(browseButton);
        actions.Controls.Add(activityButton);
        actions.Controls.Add(exportCsvButton);
        actions.Controls.Add(exportJsonButton);

        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 3, 0, 0)
        };
        _uiFilter.Width = 240;
        _uiFilter.PlaceholderText = "UI category keywords, comma separated";
        _uiFilter.Text = string.Join(", ", GetCategoryFilters("ui", UiTerms));
        _backgroundFilter.Width = 240;
        _backgroundFilter.PlaceholderText = "Background category keywords";
        _backgroundFilter.Text = string.Join(", ", GetCategoryFilters("background", BackgroundTerms));
        var saveFiltersButton = new Button { Text = "Save category filters", Width = 140, Height = 32 };
        saveFiltersButton.Click += (_, _) => SaveCategoryFilters();
        filterPanel.Controls.Add(new Label { Text = "Category filters", AutoSize = true, Margin = new Padding(2, 8, 8, 0) });
        filterPanel.Controls.Add(_uiFilter);
        filterPanel.Controls.Add(_backgroundFilter);
        filterPanel.Controls.Add(saveFiltersButton);

        var profilePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 3, 0, 0)
        };
        _profileName.Width = 170;
        _profileName.PlaceholderText = "Selection profile name";
        _profilePicker.Width = 190;
        _profilePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        var saveProfileButton = new Button { Text = "Save queue", Width = 95, Height = 32 };
        saveProfileButton.Click += (_, _) => SaveSelectionProfile();
        var loadProfileButton = new Button { Text = "Load queue", Width = 95, Height = 32 };
        loadProfileButton.Click += (_, _) => LoadSelectionProfile();
        profilePanel.Controls.Add(new Label { Text = "Profiles", AutoSize = true, Margin = new Padding(2, 8, 8, 0) });
        profilePanel.Controls.Add(_profileName);
        profilePanel.Controls.Add(saveProfileButton);
        profilePanel.Controls.Add(_profilePicker);
        profilePanel.Controls.Add(loadProfileButton);

        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(_dashboardPackageCount, 0, 1);
        layout.Controls.Add(_dashboardQueueCount, 0, 2);
        layout.Controls.Add(note, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        layout.Controls.Add(filterPanel, 0, 5);
        layout.Controls.Add(profilePanel, 0, 6);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildActivityPage()
    {
        var page = new TabPage("Activity") { BackColor = Color.White, Padding = new Padding(12) };
        page.Controls.Add(BuildLogPanel());
        return page;
    }

    private TabPage BuildPackagePage(string title, ListView list, string description)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(12) };
        var hasSearch = title == "All Installed Apps";
        var listRow = hasSearch ? 2 : 1;
        var detailsRow = listRow + 1;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = detailsRow + 1,
            BackColor = Color.White
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        if (hasSearch)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));

        var header = new Panel { Dock = DockStyle.Fill };
        var descriptionLabel = new Label
        {
            Text = description,
            Dock = DockStyle.Left,
            Width = 600,
            ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var selectVisibleButton = new Button
        {
            Text = "Select visible",
            Width = 105,
            Height = 28,
            Dock = DockStyle.Right
        };
        selectVisibleButton.Click += (_, _) =>
        {
            foreach (ListViewItem item in list.Items)
            {
                _selectedPackages.Add(((InstalledPackage)item.Tag!).PackageFullName);
            }
            SynchronizeCheckedRows();
            UpdateSelectionCount();
        };
        header.Controls.Add(descriptionLabel);
        header.Controls.Add(selectVisibleButton);
        layout.Controls.Add(header, 0, 0);
        if (hasSearch)
        {
            _search.Dock = DockStyle.Left;
            _search.Width = 320;
            _search.PlaceholderText = "Search package names";
            _search.TextChanged += (_, _) => PopulateList(_allPackages, _packages, null, _search.Text);
            layout.Controls.Add(_search, 0, 1);
        }

        var details = new Label
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8),
            ForeColor = Muted,
            Text = "Select a package to view publisher, version, install location, user scope, and servicing state.",
            AutoEllipsis = true
        };
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is InstalledPackage package)
            {
                details.Text = $"{package.Name} | {package.State}\nPublisher: {package.Publisher} | Version: {package.Version} | {package.UserScope}\nInstall location: {package.InstallLocation}";
            }
        };
        list.Dock = DockStyle.Fill;
        layout.Controls.Add(list, 0, listRow);
        layout.Controls.Add(details, 0, detailsRow);
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
        var clearQueueButton = new Button { Text = "Clear queue", Width = 94, Height = 36 };
        clearQueueButton.Click += (_, _) =>
        {
            _selectedPackages.Clear();
            SynchronizeCheckedRows();
            UpdateSelectionCount();
        };
        _scanButton.Text = "Scan packages";
        _scanButton.Width = 118;
        _scanButton.Height = 36;
        _scanButton.Margin = new Padding(0, 0, 8, 0);
        _scanButton.Click += async (_, _) => await ScanAsync();
        buttons.Controls.Add(_removeButton);
        buttons.Controls.Add(clearQueueButton);
        buttons.Controls.Add(_scanButton);
        _queueStatus.AutoSize = true;
        _queueStatus.ForeColor = Accent;
        _queueStatus.Margin = new Padding(0, 10, 12, 0);
        options.Controls.Add(_queueStatus);
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
        list.Columns.Add("State", 155);
        list.Columns.Add("Version", 105);
        list.Columns.Add("Package full name", 650);
        list.ItemCheck += (_, e) =>
        {
            if (_syncingChecks)
            {
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
            BeginInvoke((Action)UpdateSelectionCount);
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
            PopulateList(_uiPackages, _packages, GetCategoryFilters("ui", UiTerms), string.Empty);
            PopulateList(_backgroundPackages, _packages, GetCategoryFilters("background", BackgroundTerms), string.Empty);
            AppendLog($"Found {_packages.Length} unique installed package identities.");
            _status.Text = $"{_packages.Length} packages scanned";
            UpdateSelectionCount();
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
        PopulateList(_uiPackages, _packages, GetCategoryFilters("ui", UiTerms), string.Empty);
        PopulateList(_backgroundPackages, _packages, GetCategoryFilters("background", BackgroundTerms), string.Empty);
        _status.Text = $"{_packages.Length} packages scanned";
        UpdateSelectionCount();
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
            item.SubItems.Add(package.State);
            item.SubItems.Add(package.Version);
            item.SubItems.Add(package.PackageFullName);
            item.Checked = _selectedPackages.Contains(package.PackageFullName);
            if (package.NonRemovable || package.IsFramework)
            {
                item.ToolTipText = package.NonRemovable
                    ? "Windows marks this package as non-removable. Selection is allowed, but Windows may refuse removal."
                    : "Framework package. Selection is allowed, but removal may affect dependent apps or be refused.";
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

    private void UpdateSelectionCount()
    {
        var text = $"Queued: {_selectedPackages.Count}";
        _queueStatus.Text = text;
        _dashboardQueueCount.Text = $"{text} for review";
        var installedCount = _packages.Count(package => package.IsInstalled);
        var provisionedOnlyCount = _packages.Length - installedCount;
        _dashboardPackageCount.Text = $"Installed: {installedCount} | provisioned only: {provisionedOnlyCount}";
    }

    private string[] GetCategoryFilters(string category, string[] defaults)
    {
        return _settings.CategoryFilters.TryGetValue(category, out var filters) && filters.Length > 0
            ? filters
            : defaults;
    }

    private void SaveCategoryFilters()
    {
        _settings.CategoryFilters["ui"] = ParseFilterTerms(_uiFilter.Text);
        _settings.CategoryFilters["background"] = ParseFilterTerms(_backgroundFilter.Text);
        SaveSettings();
        PopulateList(_uiPackages, _packages, GetCategoryFilters("ui", UiTerms), string.Empty);
        PopulateList(_backgroundPackages, _packages, GetCategoryFilters("background", BackgroundTerms), string.Empty);
        AppendLog("Saved custom package category filters.");
    }

    private static string[] ParseFilterTerms(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SaveSelectionProfile()
    {
        var name = _profileName.Text.Trim();
        if (name.Length == 0 || _selectedPackages.Count == 0)
        {
            MessageBox.Show(this, "Enter a profile name and queue at least one package.", "Cannot save profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _settings.SelectionProfiles[name] = _selectedPackages.Order(StringComparer.Ordinal).ToArray();
        SaveSettings();
        RefreshProfilePicker(name);
        AppendLog($"Saved selection profile '{name}'.");
    }

    private void LoadSelectionProfile()
    {
        if (_profilePicker.SelectedItem is not string name || !_settings.SelectionProfiles.TryGetValue(name, out var packages))
        {
            return;
        }

        var inventory = _packages.Select(package => package.PackageFullName).ToHashSet(StringComparer.Ordinal);
        _selectedPackages.Clear();
        _selectedPackages.UnionWith(packages.Where(inventory.Contains));
        SynchronizeCheckedRows();
        UpdateSelectionCount();
        AppendLog($"Loaded selection profile '{name}' ({_selectedPackages.Count} matching packages).");
    }

    private void RefreshProfilePicker(string? selectedName = null)
    {
        _profilePicker.Items.Clear();
        foreach (var name in _settings.SelectionProfiles.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            _profilePicker.Items.Add(name);
        }
        if (_profilePicker.Items.Count == 0)
        {
            return;
        }

        _profilePicker.SelectedItem = selectedName;
        if (_profilePicker.SelectedIndex < 0)
        {
            _profilePicker.SelectedIndex = 0;
        }
    }

    private void ExportInventory(string format)
    {
        if (_packages.Length == 0)
        {
            MessageBox.Show(this, "Scan the package inventory before exporting.", "No inventory", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extension = format == "csv" ? "csv" : "json";
        using var dialog = new SaveFileDialog
        {
            FileName = $"WindowsStuff-inventory.{extension}",
            Filter = format == "csv" ? "CSV files (*.csv)|*.csv" : "JSON files (*.json)|*.json",
            AddExtension = true,
            DefaultExt = extension
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var contents = format == "json" ? ExportJson() : ExportCsv();
        File.WriteAllText(dialog.FileName, contents, Encoding.UTF8);
        AppendLog($"Exported {_packages.Length} package records to {dialog.FileName}.");
    }

    private string ExportJson()
    {
        return JsonSerializer.Serialize(_packages, new JsonSerializerOptions { WriteIndented = true });
    }

    private string ExportCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Name,PackageFullName,PackageFamilyName,Publisher,Version,InstallLocation,UserScope,State,IsInstalled,IsProvisioned,IsFramework,NonRemovable");
        foreach (var package in _packages)
        {
            var fields = new[]
            {
                package.Name,
                package.PackageFullName,
                package.PackageFamilyName,
                package.Publisher,
                package.Version,
                package.InstallLocation,
                package.UserScope,
                package.State,
                package.IsInstalled.ToString(),
                package.IsProvisioned.ToString(),
                package.IsFramework.ToString(),
                package.NonRemovable.ToString()
            };
            builder.AppendLine(string.Join(',', fields.Select(EscapeCsvField)));
        }
        return builder.ToString();
    }

    private static string EscapeCsvField(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private UserSettings LoadSettings()
    {
        var path = Path.Combine(_dataDirectory, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new UserSettings();
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        return new UserSettings();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var path = Path.Combine(_dataDirectory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException exception)
        {
            AppendLog($"Could not save settings: {exception.Message}");
        }
    }

    private void LoadHistory()
    {
        var path = Path.Combine(_dataDirectory, "history.jsonl");
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var line in File.ReadLines(path).TakeLast(500))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                    if (entry is not null)
                    {
                        AppendLogDisplay(entry.Timestamp, entry.Message);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        var timestamp = DateTimeOffset.Now;
        AppendLogDisplay(timestamp, message);
        if (_historyWriteFailed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var path = Path.Combine(_dataDirectory, "history.jsonl");
            var file = new FileInfo(path);
            if (file.Exists && file.Length > 5 * 1024 * 1024)
            {
                File.Move(path, path + ".1", true);
            }
            File.AppendAllText(path, JsonSerializer.Serialize(new HistoryEntry(timestamp, message)) + Environment.NewLine);
        }
        catch (IOException exception)
        {
            _historyWriteFailed = true;
            AppendLogDisplay(DateTimeOffset.Now, $"Persistent history disabled: {exception.Message}");
        }
    }

    private void AppendLogDisplay(DateTimeOffset timestamp, string message)
    {
        _log.AppendText($"[{timestamp:HH:mm:ss}] {message}{Environment.NewLine}");
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