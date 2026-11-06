using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CznNoVpn;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            return RunSelfTestAsync().GetAwaiter().GetResult();
        }

        using var mutex = new Mutex(true, @"Local\CznNoVpnLauncher_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "CZN No-VPN Launcher is already running! Please check the System Tray icon in the bottom-right corner of your screen.",
                "CZN No-VPN Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => SystemProxyManager.DisableSystemProxy();
        SystemEvents.SessionEnding += (_, _) => SystemProxyManager.DisableSystemProxy();

        ApplicationConfiguration.Initialize();
        bool autoLaunch = args.Any(a =>
            a.Equals("--launch", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--play", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(autoLaunchOnStartup: autoLaunch));
        SystemProxyManager.DisableSystemProxy();
        return 0;
    }

    private static async Task<int> RunSelfTestAsync()
    {
        Console.WriteLine("=== CZN No-VPN Split-Tunnel Self-Test ===");
        string cachePath = Path.Combine(AppContext.BaseDirectory, "proxy_cache.json");
        using var surfEasy = new SurfEasyClient(cachePath, msg => Console.WriteLine($"[SurfEasy] {msg}"));
        using var proxy = new SmartProxyServer(MainForm.ProxyPort, surfEasy, msg => Console.WriteLine($"[Proxy] {msg}"));

        await surfEasy.InitializeAsync("AS", forceRefresh: false);
        proxy.Start();

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{MainForm.ProxyPort}"),
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
        Console.WriteLine($"   Stats -> Tunneled: {proxy.TunneledCount}, Direct: {proxy.DirectCount}");

        return 0;
    }
}
