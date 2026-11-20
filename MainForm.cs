using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text.Json;
using System.Windows.Forms;

namespace CznNoVpn;

public sealed class MainForm : Form
{
    public const int ProxyPort = 18080;
    public const string AppVersion = "1.0.1";
    public const string DeveloperName = "CutoChan";
    public const string GitHubRepoUrl = "https://github.com/cutochan/CZN-No-VPN";

    private readonly SurfEasyClient _surfEasy;
    private readonly SmartProxyServer _proxyServer;
    private readonly bool _autoLaunchOnStartup;

    private readonly Label _statusBadgeLabel;
    private readonly Label _nodeInfoLabel;
    private readonly Label _stoveRegionLabel;
    private readonly Label _statsLabel;
    private readonly TextBox _logBox;
    private readonly Button _launchCznBtn;
    private readonly Button _launchStoveBtn;
    private readonly Button _refreshNodeBtn;
    private readonly CheckBox _proxyToggleCheck;
    private readonly CheckBox _minimizeToTrayCheck;
    private readonly Label _creditLabel;
    private readonly LinkLabel _githubLink;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private bool _isExiting;
    private bool _suppressProxyToggleEvent;
    private bool _systemProxyBlocked;

    public MainForm(bool autoLaunchOnStartup = false)
    {
        _autoLaunchOnStartup = autoLaunchOnStartup;

        string cachePath = Program.GetCacheFilePath();
        _surfEasy = new SurfEasyClient(cachePath, AppendLog);
        _proxyServer = new SmartProxyServer(ProxyPort, _surfEasy, AppendLog);

        Text = $"CZN No-VPN Launcher • Dev: {DeveloperName}";
        Size = new Size(620, 510);
        MinimumSize = new Size(580, 470);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 28);
        ForeColor = Color.FromArgb(235, 238, 245);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield;
        Icon = appIcon;

        // Header Panel
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Color.FromArgb(26, 29, 42),
            Padding = new Padding(18, 12, 18, 12)
        };

        var titleLabel = new Label
        {
            Text = "CHAOS ZERO NIGHTMARE — NO-VPN LAUNCHER",
            Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(130, 170, 255),
            AutoSize = true,
            Location = new Point(18, 13)
        };

        var subTitleLabel = new Label
        {
            Text = "Routes only *.onstove.com via Tunnel • Game patches & gameplay stay 100% Direct",
            Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
            ForeColor = Color.FromArgb(165, 175, 195),
            AutoSize = true,
            UseMnemonic = false,
            Location = new Point(18, 42)
        };

        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subTitleLabel);

        // Status Card
        var statusCard = new Panel
        {
            Location = new Point(18, 90),
            Size = new Size(568, 108),
            BackColor = Color.FromArgb(25, 28, 38)
        };

        _statusBadgeLabel = new Label
        {
            Text = "● Initializing Tunnel connection...",
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 195, 85),
            AutoSize = true,
            Location = new Point(14, 12)
        };

        _nodeInfoLabel = new Label
        {
            Text = "Node: Finding fastest upstream server...",
            ForeColor = Color.FromArgb(200, 208, 224),
            AutoSize = true,
            Location = new Point(14, 38)
        };

        _stoveRegionLabel = new Label
        {
            Text = "STOVE Region: Checking...",
            ForeColor = Color.FromArgb(180, 225, 195),
            AutoSize = true,
            Location = new Point(14, 60)
        };

        _statsLabel = new Label
        {
            Text = "Traffic: 0 STOVE (Tunneled) | 0 Direct",
            ForeColor = Color.FromArgb(145, 155, 178),
            AutoSize = true,
            Location = new Point(14, 82)
        };

        statusCard.Controls.Add(_statusBadgeLabel);
        statusCard.Controls.Add(_nodeInfoLabel);
        statusCard.Controls.Add(_stoveRegionLabel);
        statusCard.Controls.Add(_statsLabel);

        // Action Buttons
        _launchCznBtn = new Button
        {
            Text = "🚀 PLAY CHAOS ZERO NIGHTMARE",
            Font = new Font("Segoe UI Semibold", 10.2f, FontStyle.Bold),
            Location = new Point(18, 210),
            Size = new Size(280, 44),
            BackColor = Color.FromArgb(58, 118, 240),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _launchCznBtn.FlatAppearance.BorderSize = 0;
        _launchCznBtn.Click += async (_, _) => await LaunchGameFlowAsync(autoStartCzn: true);

        _launchStoveBtn = new Button
        {
            Text = "🎮 Open STOVE (Global)",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            Location = new Point(308, 210),
            Size = new Size(175, 44),
            BackColor = Color.FromArgb(42, 48, 66),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _launchStoveBtn.FlatAppearance.BorderSize = 0;
        _launchStoveBtn.Click += async (_, _) => await LaunchGameFlowAsync(autoStartCzn: false);

        _refreshNodeBtn = new Button
        {
            Text = "🔄 Switch Node",
            Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
            Location = new Point(491, 210),
            Size = new Size(95, 44),
            BackColor = Color.FromArgb(36, 40, 56),
            ForeColor = Color.FromArgb(210, 215, 230),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _refreshNodeBtn.FlatAppearance.BorderSize = 0;
        _refreshNodeBtn.Click += async (_, _) => await RefreshTunnelAsync(forceNew: true);

        // Options Row
        _proxyToggleCheck = new CheckBox
        {
            Text = $"Enable Smart Proxy for STOVE (127.0.0.1:{ProxyPort})",
            Checked = true,
            AutoSize = true,
            Location = new Point(18, 264),
            ForeColor = Color.FromArgb(210, 218, 232)
        };
        _proxyToggleCheck.CheckedChanged += OnProxyToggleCheckedChanged;

        _minimizeToTrayCheck = new CheckBox
        {
            Text = "Minimize to System Tray on close",
            Checked = true,
            AutoSize = true,
            Location = new Point(355, 264),
            ForeColor = Color.FromArgb(175, 185, 205)
        };

        // Log Box
        _logBox = new TextBox
        {
            Location = new Point(18, 294),
            Size = new Size(568, 132),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(12, 14, 20),
            ForeColor = Color.FromArgb(170, 185, 210),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.8f, FontStyle.Regular)
        };

        // Footer Credit Bar (Dev: CutoChan + GitHub Link)
        _creditLabel = new Label
        {
            Text = $"Developed by {DeveloperName}  •  GitHub:",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(150, 160, 185),
            AutoSize = true,
            Location = new Point(18, 437)
        };

        _githubLink = new LinkLabel
        {
            Text = "github.com/cutochan/CZN-No-VPN",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular),
            LinkColor = Color.FromArgb(110, 175, 255),
            ActiveLinkColor = Color.FromArgb(160, 210, 255),
            VisitedLinkColor = Color.FromArgb(110, 175, 255),
            AutoSize = true,
            Location = new Point(_creditLabel.Left + _creditLabel.PreferredWidth + 4, 437)
        };
        _githubLink.LinkClicked += (_, _) => OpenGitHubRepo();

        var githubBtn = new Button
        {
            Text = "⭐ GitHub",
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            Location = new Point(496, 432),
            Size = new Size(90, 28),
            BackColor = Color.FromArgb(34, 40, 58),
            ForeColor = Color.FromArgb(140, 190, 255),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        githubBtn.FlatAppearance.BorderSize = 1;
        githubBtn.FlatAppearance.BorderColor = Color.FromArgb(65, 85, 130);
        githubBtn.Click += (_, _) => OpenGitHubRepo();

        Controls.Add(headerPanel);
        Controls.Add(statusCard);
        Controls.Add(_launchCznBtn);
        Controls.Add(_launchStoveBtn);
        Controls.Add(_refreshNodeBtn);
        Controls.Add(_proxyToggleCheck);
        Controls.Add(_minimizeToTrayCheck);
        Controls.Add(_logBox);
        Controls.Add(_creditLabel);
        Controls.Add(_githubLink);
        Controls.Add(githubBtn);

        // Adjust dynamic layout after DPI scaling completes on Load
        Load += (_, _) =>
        {
            _githubLink.Location = new Point(_creditLabel.Right + 4, _creditLabel.Top);
        };

        // System Tray Menu
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Dashboard", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Play Chaos Zero Nightmare", null, async (_, _) => await LaunchGameFlowAsync(true));
        trayMenu.Items.Add("Open STOVE Launcher Only", null, async (_, _) => await LaunchGameFlowAsync(false));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add($"GitHub ({DeveloperName}/CZN-No-VPN)", null, (_, _) => OpenGitHubRepo());
        trayMenu.Items.Add("Exit Completely (Disable Proxy)", null, (_, _) => ExitCleanly());

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Text = $"CZN No-VPN by {DeveloperName}",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => UpdateLiveCounters();
        _uiTimer.Start();

        Shown += async (_, _) => await OnFormShownAsync();
        FormClosing += OnFormClosing;
    }

    private void OnProxyToggleCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressProxyToggleEvent)
            return;

        int activePort = _proxyServer.Port;
        if (_proxyToggleCheck.Checked)
        {
            if (SystemProxyManager.EnableSmartSystemProxy(activePort, out string? err))
            {
                _systemProxyBlocked = false;
                AppendLog($"Smart Proxy enabled for STOVE (127.0.0.1:{activePort}).");
            }
            else
            {
                _systemProxyBlocked = true;
                SetProxyToggleChecked(false);
                AppendLog($"[WARN] Could not enable Windows System Proxy ({err}).");
                MessageBox.Show(
                    this,
                    $"Could not update Windows System Proxy settings ({err}).\n\nPlease try running as Administrator or allow CznNoVpn.exe in your Antivirus.\nYou can still launch STOVE directly using the Play button (process-level proxy mode).",
                    "System Proxy Permission Notice",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        else
        {
            SystemProxyManager.DisableSystemProxy();
            AppendLog("System Proxy disabled (restored default settings).");
        }
    }

    private void SetProxyToggleChecked(bool isChecked)
    {
        _suppressProxyToggleEvent = true;
        try
        {
            _proxyToggleCheck.Checked = isChecked;
        }
        finally
        {
            _suppressProxyToggleEvent = false;
        }
    }

    private static void OpenGitHubRepo()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GitHubRepoUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch errors
        }
    }

    private async Task OnFormShownAsync()
    {
        try
        {
            AppendLog($"CZN No-VPN Launcher v{AppVersion} — Developed by {DeveloperName} ({GitHubRepoUrl})");

            try
            {
                _proxyServer.Start();
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] Local proxy server failed to start: {ex.Message}");
                _statusBadgeLabel.Text = "● Local proxy failed to start";
                _statusBadgeLabel.ForeColor = Color.FromArgb(255, 105, 105);
                return;
            }

            int activePort = _proxyServer.Port;
            _proxyToggleCheck.Text = $"Enable Smart Proxy for STOVE (127.0.0.1:{activePort})";

            if (!SystemProxyManager.EnableSmartSystemProxy(activePort, out string? proxyErr))
            {
                _systemProxyBlocked = true;
                SetProxyToggleChecked(false);
                AppendLog($"[WARN] Could not enable Windows System Proxy ({proxyErr}).");

                if (!SystemProxyManager.IsRunningAsAdministrator())
                {
                    var result = MessageBox.Show(
                        this,
                        $"Windows blocked updating System Proxy settings:\n\"{proxyErr}\"\n\n" +
                        "Would you like to restart CZN No-VPN Launcher as Administrator?\n" +
                        "• Click Yes to restart with Administrator privileges.\n" +
                        "• Click No to continue in Process-Proxy Mode (still launches STOVE).",
                        "Administrator Permission Recommended",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (result == DialogResult.Yes)
                    {
                        string[]? relaunchArgs = _autoLaunchOnStartup ? new[] { "--play" } : null;
                        if (SystemProxyManager.RestartAsAdministrator(relaunchArgs))
                        {
                            Program.ReleaseSingleInstanceMutex();
                            ExitCleanly();
                            return;
                        }

                        AppendLog("[WARN] Administrator elevation was cancelled; continuing in Process-Proxy Mode.");
                    }
                }
                else
                {
                    AppendLog("[TIP] Running as Admin but Windows Proxy is locked. Please whitelist CznNoVpn.exe in your Antivirus or check Windows Proxy Group Policy.");
                }
            }
            else
            {
                _systemProxyBlocked = false;
            }

            await RefreshTunnelAsync(forceNew: false);

            if (_autoLaunchOnStartup && _launchCznBtn.Enabled)
            {
                await LaunchGameFlowAsync(autoStartCzn: true);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Startup error: {ex.Message}");
            _statusBadgeLabel.Text = "● Startup error (see log below)";
            _statusBadgeLabel.ForeColor = Color.FromArgb(255, 105, 105);
            _refreshNodeBtn.Enabled = true;
        }
    }

    private async Task RefreshTunnelAsync(bool forceNew)
    {
        try
        {
            _launchCznBtn.Enabled = false;
            _launchStoveBtn.Enabled = false;
            _refreshNodeBtn.Enabled = false;
            _statusBadgeLabel.Text = "● Connecting to Tunnel server...";
            _statusBadgeLabel.ForeColor = Color.FromArgb(255, 195, 85);

            await Task.Run(() => _surfEasy.InitializeAsync("AS", forceRefresh: forceNew));

            var ep = _surfEasy.CurrentEndpoint;
            if (ep != null)
            {
                _statusBadgeLabel.Text = "● Ready — Split-Tunnel Mode Active";
                _statusBadgeLabel.ForeColor = Color.FromArgb(80, 225, 140);
                _nodeInfoLabel.Text = $"Node: {ep.Ip}:{ep.Port} (Region: {ep.Country}, Latency: ~{ep.LatencyMs}ms)";
            }

            await VerifyStovePolicyThroughLocalProxyAsync();

            _launchCznBtn.Enabled = true;
            _launchStoveBtn.Enabled = true;
            _refreshNodeBtn.Enabled = true;
        }
        catch (Exception ex)
        {
            _statusBadgeLabel.Text = "● Tunnel initialization error (Click Switch Node to retry)";
            _statusBadgeLabel.ForeColor = Color.FromArgb(255, 105, 105);
            AppendLog($"Error: {ex.Message}");
            _refreshNodeBtn.Enabled = true;
        }
    }

    private async Task VerifyStovePolicyThroughLocalProxyAsync()
    {
        try
        {
            int activePort = _proxyServer.Port;
            _stoveRegionLabel.Text = $"STOVE Region: Verifying via 127.0.0.1:{activePort}...";
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{activePort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

            string policyJson = await client.GetStringAsync(
                "https://api.onstove.com/ngds/v1.1/client/policy/total?client_lang=en&device_nation=VN&include_coverages=true&policy_grp=launcher&type=SC");

            using var doc = JsonDocument.Parse(policyJson);
            string nation = doc.RootElement.GetProperty("value").GetProperty("nation").GetString() ?? "??";

            if (!nation.Equals("VN", StringComparison.OrdinalIgnoreCase))
            {
                _stoveRegionLabel.Text = $"STOVE Region Detected: {nation} (Global — Chaos Zero Nightmare Unlocked!)";
                _stoveRegionLabel.ForeColor = Color.FromArgb(100, 235, 160);
                AppendLog($"STOVE API verified: nation={nation} (VN region redirect bypassed).");
            }
            else
            {
                _stoveRegionLabel.Text = $"Warning: STOVE Region still returned {nation}!";
                _stoveRegionLabel.ForeColor = Color.FromArgb(255, 120, 120);
            }
        }
        catch (Exception ex)
        {
            _stoveRegionLabel.Text = $"STOVE Region: Verification pending ({ex.Message})";
        }
    }

    private async Task LaunchGameFlowAsync(bool autoStartCzn)
    {
        try
        {
            int activePort = _proxyServer.Port;
            if (!_systemProxyBlocked && !_proxyToggleCheck.Checked)
            {
                if (SystemProxyManager.EnableSmartSystemProxy(activePort, out _))
                {
                    SetProxyToggleChecked(true);
                }
            }

            if (SystemProxyManager.IsStoveRunning() && !SystemProxyManager.IsCznRunning())
            {
                AppendLog("Detected running STOVE instance — restarting STOVE in Global Region mode...");
                await Task.Run(() => SystemProxyManager.StopStoveProcesses(AppendLog));
                await Task.Delay(800);
            }

            SystemProxyManager.LaunchStoveAndCzn(activePort, autoLaunchGame: autoStartCzn, AppendLog);
            AppendLog("Launch command sent! Keep this app running in the System Tray while STOVE is open.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to launch STOVE: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateLiveCounters()
    {
        bool stoveUp = SystemProxyManager.IsStoveRunning();
        bool cznUp = SystemProxyManager.IsCznRunning();
        string procState = cznUp ? "CZN Running" : (stoveUp ? "STOVE Running" : "STOVE Idle");

        _statsLabel.Text =
            $"Connections: {_proxyServer.TunneledCount} STOVE (Tunneled) | {_proxyServer.DirectCount} Direct • [{procState}]";
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(() => AppendLogCore(line));
        }
        else
        {
            AppendLogCore(line);
        }
    }

    private void AppendLogCore(string line)
    {
        if (_logBox.IsDisposed) return;
        _logBox.AppendText(line);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing && _minimizeToTrayCheck.Checked)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(
                2000,
                "CZN No-VPN is running in the background",
                "Keeping Global Region active for STOVE. Double-click the System Tray icon to reopen.",
                ToolTipIcon.Info);
            return;
        }

        CleanupBeforeExit();
    }

    public void ExitCleanly()
    {
        _isExiting = true;
        CleanupBeforeExit();
        Application.Exit();
    }

    private void CleanupBeforeExit()
    {
        _uiTimer.Stop();
        _trayIcon.Visible = false;
        SystemProxyManager.DisableSystemProxy();
        _proxyServer.Dispose();
        _surfEasy.Dispose();
    }
}
