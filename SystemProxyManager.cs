using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace CznNoVpn;

public static class SystemProxyManager
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    public const string DefaultStoveExePath = @"C:\ProgramData\Smilegate\STOVE\STOVE.exe";
    public const string DefaultCznLoaderPath = @"C:\ProgramData\Smilegate\Games\ChaosZeroNightmare\bin\ucldr_ChaosZeroNightmare_GL_loader_x64.exe";
    public const string CznSgupUri = "sgup://run/STOVE_CHAOSZERO?auto_action=PrepareAndLaunch";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static void EnableSmartSystemProxy(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true);
        if (key == null)
            return;

        string proxyServer = $"127.0.0.1:{port}";
        // Bypass common high-bandwidth domains at the OS level so browsers/updaters don't even touch localhost,
        // while STOVE's CprConnector (WinHttpGetIEProxyConfigForCurrentUser) still routes *.onstove.com to 127.0.0.1:port!
        string proxyOverride =
            "localhost;127.*;10.*;172.16.*;192.168.*;*.local;" +
            "*.cloudfront.net;*.akamaized.net;*.fastly.net;" +
            "*.googlevideo.com;*.youtube.com;*.google.com;*.gstatic.com;" +
            "*.discord.com;*.discordapp.com;*.discord.gg;" +
            "*.steampowered.com;*.steamcontent.com;" +
            "*.microsoft.com;*.windowsupdate.com;*.fbcdn.net;*.facebook.com;<local>";

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", proxyOverride, RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);

        NotifyWinInet();
    }

    public static void DisableSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true);
            if (key == null)
                return;

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            NotifyWinInet();
        }
        catch
        {
            // Ignore errors during shutdown
        }
    }

    public static bool IsSystemProxyEnabled(int expectedPort)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: false);
            if (key == null)
                return false;

            int enable = Convert.ToInt32(key.GetValue("ProxyEnable", 0));
            string server = Convert.ToString(key.GetValue("ProxyServer", "")) ?? "";
            return enable == 1 && server.Contains($"127.0.0.1:{expectedPort}", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void NotifyWinInet()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    public static bool IsStoveRunning()
    {
        return Process.GetProcessesByName("STOVE").Length > 0;
    }

    public static bool IsCznRunning()
    {
        return Process.GetProcessesByName("ucldr_ChaosZeroNightmare_GL_loader_x64").Length > 0 ||
               Process.GetProcessesByName("ChaosZeroNightmare").Length > 0 ||
               Process.GetProcessesByName("ssr-stove-shield").Length > 0;
    }

    public static void StopStoveProcesses(Action<string>? logger = null)
    {
        string[] processNames = { "STOVE", "StoveSubProcess", "sgup_svc" };
        foreach (string name in processNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    logger?.Invoke($"Closing existing {proc.ProcessName} (PID {proc.Id})...");
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch
                {
                    // Ignore if already exited or elevated service
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }

    public static string? ResolveStoveExePath()
    {
        if (File.Exists(DefaultStoveExePath))
            return DefaultStoveExePath;

        try
        {
            using var sgupKey = Registry.ClassesRoot.OpenSubKey(@"sgup\shell\open\command");
            string? cmd = sgupKey?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                int firstQuote = cmd.IndexOf('"');
                if (firstQuote >= 0)
                {
                    int secondQuote = cmd.IndexOf('"', firstQuote + 1);
                    if (secondQuote > firstQuote)
                    {
                        string extracted = cmd[(firstQuote + 1)..secondQuote];
                        if (File.Exists(extracted))
                            return extracted;
                    }
                }
                else
                {
                    string extracted = cmd.Split(' ')[0];
                    if (File.Exists(extracted))
                        return extracted;
                }
            }
        }
        catch
        {
            // Ignore registry lookup errors
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            string candidate1 = Path.Combine(drive.RootDirectory.FullName, "ProgramData", "Smilegate", "STOVE", "STOVE.exe");
            if (File.Exists(candidate1))
                return candidate1;

            string candidate2 = Path.Combine(drive.RootDirectory.FullName, "Smilegate", "STOVE", "STOVE.exe");
            if (File.Exists(candidate2))
                return candidate2;
        }

        return null;
    }

    public static void LaunchStoveAndCzn(int proxyPort, bool autoLaunchGame, Action<string>? logger = null)
    {
        EnableSmartSystemProxy(proxyPort);

        string? stoveExe = ResolveStoveExePath();
        if (string.IsNullOrEmpty(stoveExe) || !File.Exists(stoveExe))
        {
            using var ofd = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Chọn file STOVE.exe trong thư mục cài đặt STOVE Launcher",
                Filter = "STOVE Launcher (STOVE.exe)|STOVE.exe|Executable (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                stoveExe = ofd.FileName;
            }
            else
            {
                throw new FileNotFoundException("Không tìm thấy STOVE.exe. Vui lòng cài đặt STOVE Launcher trước.");
            }
        }

        string proxyUrl = $"http://127.0.0.1:{proxyPort}";
        string arguments = autoLaunchGame ? $"\"{CznSgupUri}\"" : "";

        var psi = new ProcessStartInfo
        {
            FileName = stoveExe,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(stoveExe)!,
            UseShellExecute = false
        };

        psi.Environment["HTTP_PROXY"] = proxyUrl;
        psi.Environment["HTTPS_PROXY"] = proxyUrl;
        psi.Environment["http_proxy"] = proxyUrl;
        psi.Environment["https_proxy"] = proxyUrl;
        psi.Environment["NO_PROXY"] = "localhost,127.0.0.1,.cloudfront.net";
        psi.Environment["no_proxy"] = "localhost,127.0.0.1,.cloudfront.net";

        logger?.Invoke(autoLaunchGame
            ? "Launching STOVE + Chaos Zero Nightmare (PrepareAndLaunch)..."
            : "Launching STOVE Launcher in Global Region mode...");

        Process.Start(psi);
    }
}
