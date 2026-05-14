using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using WslAutoCompact.Models;
using WslAutoCompact.Services;

namespace WslAutoCompact;

internal sealed class MainForm : Form
{
    private readonly BindingList<DistributionRow> _rows = [];
    private readonly ProcessRunner _processRunner = new();
    private readonly WslDistributionService _distributionService;
    private readonly CompactOrchestrator _orchestrator;
    private readonly OptimizeVhdCompactBackend _optimizeVhdBackend;
    private readonly string _logDirectory;

    private DataGridView _grid = null!;
    private TextBox _logTextBox = null!;
    private Button _refreshButton = null!;
    private Button _selectAllButton = null!;
    private Button _runButton = null!;
    private Button _cancelButton = null!;
    private Button _openLogButton = null!;
    private ComboBox _backendComboBox = null!;
    private Label _summaryLabel = null!;
    private string _currentLogFile = "";
    private CancellationTokenSource? _runCts;

    public MainForm()
    {
        _distributionService = new WslDistributionService(_processRunner);
        var virtDiskBackend = new VirtDiskCompactBackend();
        var diskPartBackend = new DiskPartCompactBackend(_processRunner);
        _optimizeVhdBackend = new OptimizeVhdCompactBackend(_processRunner);
        _orchestrator = new CompactOrchestrator(_processRunner, virtDiskBackend, diskPartBackend, _optimizeVhdBackend);
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSL Auto Compact",
            "Logs");

        InitializeComponent();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Directory.CreateDirectory(_logDirectory);
        _currentLogFile = CreateLogFilePath();
        Log($"WSL Auto Compact started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        if (!IsRunningAsAdministrator())
        {
            Log("Warning: The app is not running as administrator. Published builds request UAC elevation automatically.");
        }

        await RefreshDistributionsAsync();
        await RefreshBackendOptionsAsync();
    }

    private void InitializeComponent()
    {
        Text = "WSL Auto Compact";
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        Controls.Add(root);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            Text = "WSL Auto Compact"
        };
        root.Controls.Add(title, 0, 0);

        var warning = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 48,
            Text = "Running compact stops WSL and may interrupt Docker Desktop, VS Code Remote, and other WSL-based processes. Format prompts are monitored and closed during compact operations.",
            ForeColor = Color.FromArgb(130, 60, 0),
            Padding = new Padding(0, 6, 0, 6)
        };
        root.Controls.Add(warning, 0, 1);

        _grid = BuildGrid();
        root.Controls.Add(_grid, 0, 2);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 8)
        };

        _refreshButton = BuildButton("Refresh", RefreshButtonClick);
        _selectAllButton = BuildButton("Select All", SelectAllButtonClick);
        _runButton = BuildButton("Run", RunButtonClick);
        _cancelButton = BuildButton("Cancel", CancelButtonClick);
        _openLogButton = BuildButton("Open Log", OpenLogButtonClick);
        _cancelButton.Enabled = false;

        _backendComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 280,
            Margin = new Padding(12, 3, 6, 3)
        };
        _backendComboBox.Items.Add(new BackendOption("VirtDisk API (fallback: DiskPart)", BackendMode.VirtDisk));
        _backendComboBox.SelectedIndex = 0;

        _summaryLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
            Text = "Waiting for scan"
        };

        toolbar.Controls.AddRange([
            _refreshButton,
            _selectAllButton,
            _runButton,
            _cancelButton,
            _openLogButton,
            _backendComboBox,
            _summaryLabel
        ]);
        root.Controls.Add(toolbar, 0, 3);

        _logTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(22, 27, 34),
            ForeColor = Color.FromArgb(210, 220, 230),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point)
        };
        root.Controls.Add(_logTextBox, 0, 4);
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            DataSource = _rows
        };

        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", DataPropertyName = nameof(DistributionRow.Selected), Width = 42 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Distro", DataPropertyName = nameof(DistributionRow.Name), Width = 140, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "State", DataPropertyName = nameof(DistributionRow.State), Width = 90, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "VHDX", DataPropertyName = nameof(DistributionRow.VhdPath), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Before", DataPropertyName = nameof(DistributionRow.BeforeText), Width = 100, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "After", DataPropertyName = nameof(DistributionRow.AfterText), Width = 100, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Saved", DataPropertyName = nameof(DistributionRow.SavedText), Width = 100, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Backend", DataPropertyName = nameof(DistributionRow.Backend), Width = 100, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(DistributionRow.Status), Width = 130, ReadOnly = true });

        return grid;
    }

    private static Button BuildButton(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(0, 3, 8, 3)
        };
        button.Click += handler;
        return button;
    }

    private async void RefreshButtonClick(object? sender, EventArgs e)
    {
        await RefreshDistributionsAsync();
        await RefreshBackendOptionsAsync();
    }

    private void SelectAllButtonClick(object? sender, EventArgs e)
    {
        var anyUnselected = _rows.Any(row => !row.Selected);
        foreach (var row in _rows)
        {
            row.Selected = anyUnselected;
        }

        _selectAllButton.Text = anyUnselected ? "Clear All" : "Select All";
    }

    private async void RunButtonClick(object? sender, EventArgs e)
    {
        _grid.EndEdit();
        var selectedRows = _rows.Where(row => row.Selected).ToList();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show(this, "Select at least one distro to compact.", "WSL Auto Compact", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "This will stop WSL and compact the selected ext4.vhdx files. Docker Desktop and VS Code Remote may also be interrupted. Continue?",
            "WSL Auto Compact",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirmation != DialogResult.OK)
        {
            return;
        }

        _runCts = new CancellationTokenSource();
        SetRunningState(true);
        var progress = new Progress<string>(Log);
        var backendMode = ((BackendOption)_backendComboBox.SelectedItem!).Mode;

        try
        {
            Log("");
            Log($"Run started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            await _orchestrator.RunAsync(selectedRows, backendMode, progress, _runCts.Token);
            Log("Run completed.");
        }
        catch (OperationCanceledException)
        {
            Log("Canceled.");
            foreach (var row in selectedRows.Where(row => row.Status != "Done"))
            {
                row.Status = "Canceled";
            }
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "WSL Auto Compact", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetRunningState(false);
        }
    }

    private void CancelButtonClick(object? sender, EventArgs e)
    {
        _cancelButton.Enabled = false;
        _runCts?.Cancel();
    }

    private void OpenLogButtonClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentLogFile) || !File.Exists(_currentLogFile))
        {
            MessageBox.Show(this, "No log file has been created yet.", "WSL Auto Compact", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(_currentLogFile) { UseShellExecute = true });
    }

    private async Task RefreshDistributionsAsync()
    {
        SetBusy(true);
        try
        {
            Log("Scanning WSL distros...");
            _rows.Clear();

            var distributions = await _distributionService.GetDistributionsAsync(CancellationToken.None);
            foreach (var distribution in distributions)
            {
                _rows.Add(new DistributionRow(distribution));
            }

            _summaryLabel.Text = distributions.Count == 0
                ? "No WSL2 ext4.vhdx files found"
                : $"{distributions.Count} distro(s) found";
            _selectAllButton.Text = distributions.Count == 0 ? "Select All" : "Clear All";
            Log(_summaryLabel.Text);
        }
        catch (Exception ex)
        {
            Log($"Scan error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "WSL Auto Compact", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshBackendOptionsAsync()
    {
        var hasOptimizeVhd = await _optimizeVhdBackend.IsAvailableAsync(CancellationToken.None);
        var existingOptimizeOption = _backendComboBox.Items
            .OfType<BackendOption>()
            .FirstOrDefault(option => option.Mode == BackendMode.OptimizeVhd);

        if (hasOptimizeVhd && existingOptimizeOption is null)
        {
            _backendComboBox.Items.Add(new BackendOption("Optimize-VHD (available, fallback: DiskPart)", BackendMode.OptimizeVhd));
            Log("Optimize-VHD: available");
        }
        else if (!hasOptimizeVhd && existingOptimizeOption is not null)
        {
            _backendComboBox.Items.Remove(existingOptimizeOption);
            _backendComboBox.SelectedIndex = 0;
            Log("Optimize-VHD: unavailable");
        }
        else
        {
            Log(hasOptimizeVhd ? "Optimize-VHD: available" : "Optimize-VHD: unavailable");
        }
    }

    private void SetRunningState(bool isRunning)
    {
        _refreshButton.Enabled = !isRunning;
        _selectAllButton.Enabled = !isRunning;
        _runButton.Enabled = !isRunning;
        _backendComboBox.Enabled = !isRunning;
        _cancelButton.Enabled = isRunning;
        _grid.ReadOnly = isRunning;
    }

    private void SetBusy(bool isBusy)
    {
        _refreshButton.Enabled = !isBusy;
        _runButton.Enabled = !isBusy;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _logTextBox.AppendText(line);

        if (!string.IsNullOrWhiteSpace(_currentLogFile))
        {
            File.AppendAllText(_currentLogFile, line);
        }
    }

    private string CreateLogFilePath()
        => Path.Combine(_logDirectory, $"wsl-auto-compact-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record BackendOption(string Label, BackendMode Mode)
    {
        public override string ToString() => Label;
    }
}
