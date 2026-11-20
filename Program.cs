using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CznNoVpn;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    private static Mutex? _instanceMutex;

    public static string GetCacheFilePath()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CznNoVpn");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "proxy_cache.json");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "czn_novpn_cache.json");
        }
    }

    public static void ReleaseSingleInstanceMutex()
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch
        {
            // Ignore if not owned or already released
        }
        try
        {
            _instanceMutex?.Dispose();
        }
        catch
        {
            // Ignore dispose errors
        }
        finally
        {
            _instanceMutex = null;
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            return RunSelfTestAsync().GetAwaiter().GetResult();
        }

        // Brief retry avoids race conditions during Admin restart handoff
        bool createdNew = false;
        Mutex? mutex = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            mutex = new Mutex(true, @"Local\CznNoVpnLauncher_SingleInstance", out createdNew);
            if (createdNew)
                break;

            mutex.Dispose();
            mutex = null;
            Thread.Sleep(150);
        }

        if (!createdNew || mutex == null)
        {
            MessageBox.Show(
                "CZN No-VPN Launcher is already running! Please check the System Tray icon in the bottom-right corner of your screen.",
                "CZN No-VPN Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        _instanceMutex = mutex;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => SystemProxyManager.DisableSystemProxy();
        SystemEvents.SessionEnding += (_, _) => SystemProxyManager.DisableSystemProxy();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred: {e.Exception.Message}\n\nThe application will attempt to continue safely.",
                "CZN No-VPN Launcher Notice",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            SystemProxyManager.DisableSystemProxy();
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    $"Fatal error: {ex.Message}",
                    "CZN No-VPN Launcher Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };

        ApplicationConfiguration.Initialize();
        bool autoLaunch = args.Any(a =>
            a.Equals("--launch", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--play", StringComparison.OrdinalIgnoreCase));

        try
        {
            Application.Run(new MainForm(autoLaunchOnStartup: autoLaunch));
        }
        finally
        {
            SystemProxyManager.DisableSystemProxy();
            ReleaseSingleInstanceMutex();
        }

        return 0;
    }

    private static async Task<int> RunSelfTestAsync()
    {
        Console.WriteLine("=== CZN No-VPN Split-Tunnel Self-Test ===");
        string cachePath = GetCacheFilePath();
        Console.WriteLine($"Cache path: {cachePath}");

        using var surfEasy = new SurfEasyClient(cachePath, msg => Console.WriteLine($"[SurfEasy] {msg}"));
        using var proxy = new SmartProxyServer(MainForm.ProxyPort, surfEasy, msg => Console.WriteLine($"[Proxy] {msg}"));

        await surfEasy.InitializeAsync("AS", forceRefresh: false);
        proxy.Start();

        Console.WriteLine("\n0) Testing System Proxy enable / verify / disable cycle...");
        try
        {
            bool enabledOk = SystemProxyManager.EnableSmartSystemProxy(proxy.Port, out string? proxyErr);
            bool verifiedEnabled = SystemProxyManager.IsSystemProxyEnabled(proxy.Port);
            Console.WriteLine($"   EnableSmartSystemProxy: {enabledOk} (error={proxyErr ?? "none"}), IsSystemProxyEnabled: {verifiedEnabled}");
        }
        finally
        {
            SystemProxyManager.DisableSystemProxy();
        }
        Console.WriteLine($"   After DisableSystemProxy -> IsSystemProxyEnabled: {SystemProxyManager.IsSystemProxyEnabled(proxy.Port)}");

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.Port}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        Console.WriteLine("\n1) Testing STOVE Region Policy API (api.onstove.com)...");
        string policyBody = await client.GetStringAsync(
            "https://api.onstove.com/ngds/v1.1/client/policy/total?client_lang=en&device_nation=VN&include_coverages=true&policy_grp=launcher&type=SC");
        Console.WriteLine($"   Response: {policyBody}");

        Console.WriteLine("\n2) Testing STOVE Store CZN Page (wic.onstove.com/game/STOVE_CHAOSZERO)...");
        using var wicResp = await client.GetAsync("https://wic.onstove.com/game/STOVE_CHAOSZERO");
        Console.WriteLine($"   Status: {(int)wicResp.StatusCode} {wicResp.ReasonPhrase}");
        if (wicResp.Headers.Location != null)
            Console.WriteLine($"   Redirect Location: {wicResp.Headers.Location}");

        Console.WriteLine("\n3) Testing DIRECT passthrough for non-STOVE domain (api.ipify.org)...");
        string directIp = await client.GetStringAsync("https://api.ipify.org");
        Console.WriteLine($"   Non-STOVE Public IP (should be native IP): {directIp}");

        Console.WriteLine("\n4) Testing split-tunnel domain routing rules...");
        Console.WriteLine($"   api.onstove.com tunneled: {SmartProxyServer.ShouldRouteViaTunnel("api.onstove.com")}");
        Console.WriteLine($"   czn-live-down.game.playstove.com tunneled: {SmartProxyServer.ShouldRouteViaTunnel("czn-live-down.game.playstove.com")}");
        Console.WriteLine($"   dl.onstove.com tunneled: {SmartProxyServer.ShouldRouteViaTunnel("dl.onstove.com")}");
        Console.WriteLine($"   Stats -> Tunneled: {proxy.TunneledCount}, Direct: {proxy.DirectCount}");

        return 0;
    }
}
