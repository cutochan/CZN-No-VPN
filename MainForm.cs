using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text.Json;
using System.Windows.Forms;

namespace CznNoVpn;

public sealed class MainForm : Form
{
    public const int ProxyPort = 18080;
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
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private bool _isExiting;

    public MainForm(bool autoLaunchOnStartup = false)
    {
        _autoLaunchOnStartup = autoLaunchOnStartup;

        string cachePath = Path.Combine(AppContext.BaseDirectory, "proxy_cache.json");
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
        Icon = SystemIcons.Shield;

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
            Text = "Chỉ định tuyến *.onstove.com qua Tunnel • Tải game & chơi 100% mạng gốc Việt Nam",
            Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
            ForeColor = Color.FromArgb(165, 175, 195),
            AutoSize = true,
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
            Text = "● Đang khởi tạo kết nối Tunnel...",
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 195, 85),
            AutoSize = true,
            Location = new Point(14, 12)
        };

        _nodeInfoLabel = new Label
        {
            Text = "Node: Đang tìm máy chủ nhanh nhất...",
            ForeColor = Color.FromArgb(200, 208, 224),
            AutoSize = true,
            Location = new Point(14, 38)
        };

        _stoveRegionLabel = new Label
        {
            Text = "Vùng STOVE: Đang kiểm tra...",
            ForeColor = Color.FromArgb(180, 225, 195),
            AutoSize = true,
            Location = new Point(14, 60)
        };

        _statsLabel = new Label
        {
            Text = "Lưu lượng: 0 STOVE (Tunnel) | 0 Trực tiếp (Direct)",
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
            Text = "🚀 CHƠI CHAOS ZERO NIGHTMARE",
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
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
            Text = "🎮 Chỉ mở STOVE (Global)",
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
            Text = "🔄 Đổi Node",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
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
            Text = $"Kích hoạt Smart Proxy cho STOVE (127.0.0.1:{ProxyPort})",
            Checked = true,
            AutoSize = true,
            Location = new Point(18, 264),
            ForeColor = Color.FromArgb(210, 218, 232)
        };
        _proxyToggleCheck.CheckedChanged += (_, _) =>
        {
            if (_proxyToggleCheck.Checked)
            {
                SystemProxyManager.EnableSmartSystemProxy(ProxyPort);
                AppendLog("Đã bật Smart Proxy cho STOVE.");
            }
            else
            {
                SystemProxyManager.DisableSystemProxy();
                AppendLog("Đã tắt Smart Proxy hệ thống (khôi phục mặc định).");
            }
        };

        _minimizeToTrayCheck = new CheckBox
        {
            Text = "Thu nhỏ xuống khay hệ thống (Tray) khi đóng",
            Checked = true,
            AutoSize = true,
            Location = new Point(335, 264),
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
        var creditLabel = new Label
        {
            Text = $"Developed by {DeveloperName}  •  GitHub:",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(150, 160, 185),
            AutoSize = true,
            Location = new Point(18, 437)
        };

        var githubLink = new LinkLabel
        {
            Text = "github.com/cutochan/CZN-No-VPN",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular),
            LinkColor = Color.FromArgb(110, 175, 255),
            ActiveLinkColor = Color.FromArgb(160, 210, 255),
            VisitedLinkColor = Color.FromArgb(110, 175, 255),
            AutoSize = true,
            Location = new Point(222, 437)
        };
        githubLink.LinkClicked += (_, _) => OpenGitHubRepo();

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
        Controls.Add(creditLabel);
        Controls.Add(githubLink);
        Controls.Add(githubBtn);

        // System Tray Menu
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở bảng điều khiển", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Chơi Chaos Zero Nightmare", null, async (_, _) => await LaunchGameFlowAsync(true));
        trayMenu.Items.Add("Chỉ mở STOVE Launcher", null, async (_, _) => await LaunchGameFlowAsync(false));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add($"GitHub ({DeveloperName}/CZN-No-VPN)", null, (_, _) => OpenGitHubRepo());
        trayMenu.Items.Add("Thoát hoàn toàn (Tắt Proxy)", null, (_, _) => ExitCleanly());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
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
        AppendLog($"CZN No-VPN Launcher v1.0.0 — Developed by {DeveloperName} ({GitHubRepoUrl})");
        _proxyServer.Start();
        SystemProxyManager.EnableSmartSystemProxy(ProxyPort);

        await RefreshTunnelAsync(forceNew: false);

        if (_autoLaunchOnStartup && _launchCznBtn.Enabled)
        {
            await LaunchGameFlowAsync(autoStartCzn: true);
        }
    }

    private async Task RefreshTunnelAsync(bool forceNew)
    {
        try
        {
            _launchCznBtn.Enabled = false;
            _launchStoveBtn.Enabled = false;
            _refreshNodeBtn.Enabled = false;
            _statusBadgeLabel.Text = "● Đang kết nối máy chủ Tunnel...";
            _statusBadgeLabel.ForeColor = Color.FromArgb(255, 195, 85);

            await Task.Run(() => _surfEasy.InitializeAsync("AS", forceRefresh: forceNew));

            var ep = _surfEasy.CurrentEndpoint;
            if (ep != null)
            {
                _statusBadgeLabel.Text = "● Đã sẵn sàng — Chế độ Split-Tunnel Hoạt Động";
                _statusBadgeLabel.ForeColor = Color.FromArgb(80, 225, 140);
                _nodeInfoLabel.Text = $"Node: {ep.Ip}:{ep.Port} (Quốc gia: {ep.Country}, Độ trễ: ~{ep.LatencyMs}ms)";
            }

            await VerifyStovePolicyThroughLocalProxyAsync();

            _launchCznBtn.Enabled = true;
            _launchStoveBtn.Enabled = true;
            _refreshNodeBtn.Enabled = true;
        }
        catch (Exception ex)
        {
            _statusBadgeLabel.Text = "● Lỗi khởi tạo Tunnel (Bấm Đổi Node để thử lại)";
            _statusBadgeLabel.ForeColor = Color.FromArgb(255, 105, 105);
            AppendLog($"Lỗi: {ex.Message}");
            _refreshNodeBtn.Enabled = true;
        }
    }

    private async Task VerifyStovePolicyThroughLocalProxyAsync()
    {
        try
        {
            _stoveRegionLabel.Text = "Vùng STOVE: Đang xác thực qua 127.0.0.1:18080...";
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{ProxyPort}"),
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
                _stoveRegionLabel.Text = $"Vùng STOVE nhận diện: {nation} (Quốc tế — Mở khóa Chaos Zero Nightmare thành công!)";
                _stoveRegionLabel.ForeColor = Color.FromArgb(100, 235, 160);
                AppendLog($"Xác thực STOVE API thành công: nation={nation} (không bị chuyển vùng VN).");
            }
            else
            {
                _stoveRegionLabel.Text = $"Cảnh báo: Vùng STOVE vẫn trả về {nation}!";
                _stoveRegionLabel.ForeColor = Color.FromArgb(255, 120, 120);
            }
        }
        catch (Exception ex)
        {
            _stoveRegionLabel.Text = $"Vùng STOVE: Chưa kiểm tra được ({ex.Message})";
        }
    }

    private async Task LaunchGameFlowAsync(bool autoStartCzn)
    {
        try
        {
            if (!_proxyToggleCheck.Checked)
            {
                _proxyToggleCheck.Checked = true;
            }
            else
            {
                SystemProxyManager.EnableSmartSystemProxy(ProxyPort);
            }

            if (SystemProxyManager.IsStoveRunning() && !SystemProxyManager.IsCznRunning())
            {
                AppendLog("Phát hiện STOVE đang chạy trước đó — đang khởi động lại STOVE để nhận vùng Quốc Tế...");
                await Task.Run(() => SystemProxyManager.StopStoveProcesses(AppendLog));
                await Task.Delay(800);
            }

            SystemProxyManager.LaunchStoveAndCzn(ProxyPort, autoLaunchGame: autoStartCzn, AppendLog);
            AppendLog("Đã gửi lệnh khởi chạy! Giữ ứng dụng này chạy ngầm (System Tray) trong lúc mở STOVE.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không thể khởi chạy STOVE: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateLiveCounters()
    {
        bool stoveUp = SystemProxyManager.IsStoveRunning();
        bool cznUp = SystemProxyManager.IsCznRunning();
        string procState = cznUp ? "CZN Đang Chạy" : (stoveUp ? "STOVE Đang Chạy" : "STOVE Đang Tắt");

        _statsLabel.Text =
            $"Kết nối: {_proxyServer.TunneledCount} STOVE (Tunnel) | {_proxyServer.DirectCount} Trực tiếp (Direct) • [{procState}]";
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
                "CZN No-VPN đang chạy ngầm",
                "Ứng dụng vẫn đang giữ vùng Quốc Tế cho STOVE. Nhấp đúp vào biểu tượng ở khay hệ thống để mở lại.",
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
