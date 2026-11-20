using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace CznNoVpn;

public static class SystemProxyManager
{
    private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string InternetSettingsPolicyKeyPath = @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int INTERNET_OPTION_REFRESH = 37;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;

    private const uint INTERNET_PER_CONN_FLAGS = 1;
    private const uint INTERNET_PER_CONN_PROXY_SERVER = 2;
    private const uint INTERNET_PER_CONN_PROXY_BYPASS = 3;

    private const uint PROXY_TYPE_DIRECT = 1;
    private const uint PROXY_TYPE_PROXY = 2;

    public const string DefaultStoveExePath = @"C:\ProgramData\Smilegate\STOVE\STOVE.exe";
    public const string DefaultCznLoaderPath = @"C:\ProgramData\Smilegate\Games\ChaosZeroNightmare\bin\ucldr_ChaosZeroNightmare_GL_loader_x64.exe";
    public const string CznSgupUri = "sgup://run/STOVE_CHAOSZERO?auto_action=PrepareAndLaunch";

    [StructLayout(LayoutKind.Explicit)]
    private struct INTERNET_PER_CONN_OPTION_VALUE
    {
        [FieldOffset(0)] public uint dwValue;
        [FieldOffset(0)] public IntPtr pszValue;
        [FieldOffset(0)] public System.Runtime.InteropServices.ComTypes.FILETIME ftValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTIONW
    {
        public uint dwOption;
        public INTERNET_PER_CONN_OPTION_VALUE Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct INTERNET_PER_CONN_OPTION_LISTW
    {
        public uint dwSize;
        public IntPtr pszConnection;
        public uint dwOptionCount;
        public uint dwOptionError;
        public IntPtr pOptions;
    }

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InternetSetOptionW(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartAsAdministrator(string[]? extraArgs = null)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (extraArgs != null)
            {
                foreach (string arg in extraArgs)
                {
                    if (!string.IsNullOrWhiteSpace(arg))
                        psi.ArgumentList.Add(arg);
                }
            }

            Process.Start(psi);
            return true;
        }
        catch
        {
            // User declined UAC prompt or elevation failed
            return false;
        }
    }

    public static bool EnableSmartSystemProxy(int port) => EnableSmartSystemProxy(port, out _);

    public static bool EnableSmartSystemProxy(int port, out string? errorMessage)
    {
        string proxyServer = $"127.0.0.1:{port}";
        // Bypass common high-bandwidth domains at the OS level so browsers/updaters don't even touch localhost,
        // while STOVE's CprConnector (WinHttpGetIEProxyConfigForCurrentUser) still routes *.onstove.com to 127.0.0.1:port!
        string proxyOverride =
            "localhost;127.*;10.*;172.16.*;192.168.*;*.local;" +
            "*.playstove.com;*.game.onstove.com;dl.onstove.com;static-resource.onstove.com;msg-session.onstove.com;" +
            "*.cloudfront.net;*.akamaized.net;*.fastly.net;" +
            "*.googlevideo.com;*.youtube.com;*.google.com;*.gstatic.com;" +
            "*.discord.com;*.discordapp.com;*.discord.gg;" +
            "*.steampowered.com;*.steamcontent.com;" +
            "*.microsoft.com;*.windowsupdate.com;*.fbcdn.net;*.facebook.com;<local>";

        bool winInetOk = TrySetProxyViaWinInet(enable: true, proxyServer, proxyOverride, out string? winInetErr);
        bool hkcuOk = TryWriteProxyToRegistry(Registry.CurrentUser, enable: true, proxyServer, proxyOverride, out string? hkcuErr);

        bool hklmOk = false;
        string? hklmErr = null;
        if (!winInetOk && !hkcuOk && IsRunningAsAdministrator())
        {
            hklmOk = TryWriteProxyToRegistry(Registry.LocalMachine, enable: true, proxyServer, proxyOverride, out hklmErr);
        }

        if (winInetOk || hkcuOk || hklmOk)
        {
            NotifyWinInet();
            errorMessage = null;
            return true;
        }

        errorMessage = hkcuErr ?? winInetErr ?? hklmErr ?? "Access denied when configuring Windows System Proxy.";
        return false;
    }

    public static void DisableSystemProxy()
    {
        try
        {
            TrySetProxyViaWinInet(enable: false, null, null, out _);
            TryWriteProxyToRegistry(Registry.CurrentUser, enable: false, null, null, out _);

            if (IsRunningAsAdministrator() && IsProxyPerMachinePolicy())
            {
                TryWriteProxyToRegistry(Registry.LocalMachine, enable: false, null, null, out _);
            }

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
            if (IsRegistryProxyEnabled(Registry.CurrentUser, expectedPort))
                return true;

            if (IsProxyPerMachinePolicy() && IsRegistryProxyEnabled(Registry.LocalMachine, expectedPort))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRegistryProxyEnabled(RegistryKey root, int expectedPort)
    {
        try
        {
            using var key = root.OpenSubKey(InternetSettingsKeyPath, writable: false);
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

    private static bool IsProxyPerMachinePolicy()
    {
        try
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey(InternetSettingsPolicyKeyPath, writable: false);
            if (policyKey == null)
                return false;

            object? val = policyKey.GetValue("ProxySettingsPerUser");
            return val != null && Convert.ToInt32(val) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteProxyToRegistry(
        RegistryKey root,
        bool enable,
        string? proxyServer,
        string? proxyOverride,
        out string? errorMessage)
    {
        try
        {
            using var key = root.OpenSubKey(InternetSettingsKeyPath, writable: true);
            if (key == null)
            {
                errorMessage = $@"Registry key not found: {root.Name}\{InternetSettingsKeyPath}";
                return false;
            }

            if (enable)
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", proxyServer ?? "", RegistryValueKind.String);
                key.SetValue("ProxyOverride", proxyOverride ?? "<local>", RegistryValueKind.String);
                key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TrySetProxyViaWinInet(
        bool enable,
        string? proxyServer,
        string? proxyBypass,
        out string? errorMessage)
    {
        IntPtr proxyServerPtr = IntPtr.Zero;
        IntPtr proxyBypassPtr = IntPtr.Zero;
        IntPtr optionsPtr = IntPtr.Zero;
        IntPtr listPtr = IntPtr.Zero;

        try
        {
            INTERNET_PER_CONN_OPTIONW[] options;
            if (enable)
            {
                proxyServerPtr = Marshal.StringToHGlobalUni(proxyServer ?? "");
                proxyBypassPtr = Marshal.StringToHGlobalUni(proxyBypass ?? "<local>");

                options = new INTERNET_PER_CONN_OPTIONW[3];
                options[0] = new INTERNET_PER_CONN_OPTIONW
                {
                    dwOption = INTERNET_PER_CONN_FLAGS,
                    Value = new INTERNET_PER_CONN_OPTION_VALUE { dwValue = PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY }
                };
                options[1] = new INTERNET_PER_CONN_OPTIONW
                {
                    dwOption = INTERNET_PER_CONN_PROXY_SERVER,
                    Value = new INTERNET_PER_CONN_OPTION_VALUE { pszValue = proxyServerPtr }
                };
                options[2] = new INTERNET_PER_CONN_OPTIONW
                {
                    dwOption = INTERNET_PER_CONN_PROXY_BYPASS,
                    Value = new INTERNET_PER_CONN_OPTION_VALUE { pszValue = proxyBypassPtr }
                };
            }
            else
            {
                options = new INTERNET_PER_CONN_OPTIONW[1];
                options[0] = new INTERNET_PER_CONN_OPTIONW
                {
                    dwOption = INTERNET_PER_CONN_FLAGS,
                    Value = new INTERNET_PER_CONN_OPTION_VALUE { dwValue = PROXY_TYPE_DIRECT }
                };
            }

            int optionSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTIONW>();
            optionsPtr = Marshal.AllocHGlobal(optionSize * options.Length);
            for (int i = 0; i < options.Length; i++)
            {
                Marshal.StructureToPtr(options[i], IntPtr.Add(optionsPtr, i * optionSize), false);
            }

            int listSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LISTW>();
            var list = new INTERNET_PER_CONN_OPTION_LISTW
            {
                dwSize = (uint)listSize,
                pszConnection = IntPtr.Zero,
                dwOptionCount = (uint)options.Length,
                dwOptionError = 0,
                pOptions = optionsPtr
            };

            listPtr = Marshal.AllocHGlobal(listSize);
            Marshal.StructureToPtr(list, listPtr, false);

            bool ok = InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, listPtr, listSize);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                errorMessage = $"WinINet InternetSetOptionW failed (Win32 error {err}).";
                return false;
            }

            NotifyWinInet();
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        finally
        {
            if (listPtr != IntPtr.Zero) Marshal.FreeHGlobal(listPtr);
            if (optionsPtr != IntPtr.Zero) Marshal.FreeHGlobal(optionsPtr);
            if (proxyBypassPtr != IntPtr.Zero) Marshal.FreeHGlobal(proxyBypassPtr);
            if (proxyServerPtr != IntPtr.Zero) Marshal.FreeHGlobal(proxyServerPtr);
        }
    }

    private static void NotifyWinInet()
    {
        try
        {
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch
        {
            // Ignore WinINet notification failures
        }
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
        if (!EnableSmartSystemProxy(proxyPort, out string? proxyErr))
        {
            logger?.Invoke($"[WARN] Could not set Windows System Proxy ({proxyErr}). Launching STOVE with process-level proxy settings.");
        }

        string? stoveExe = ResolveStoveExePath();
        if (string.IsNullOrEmpty(stoveExe) || !File.Exists(stoveExe))
        {
            using var ofd = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select STOVE.exe in your STOVE Launcher installation folder",
                Filter = "STOVE Launcher (STOVE.exe)|STOVE.exe|Executable (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                stoveExe = ofd.FileName;
            }
            else
            {
                throw new FileNotFoundException("STOVE.exe could not be found. Please make sure STOVE Launcher is installed.");
            }
        }

        string proxyUrl = $"http://127.0.0.1:{proxyPort}";

        var psi = new ProcessStartInfo
        {
            FileName = stoveExe,
            WorkingDirectory = Path.GetDirectoryName(stoveExe)!,
            UseShellExecute = false
        };

        if (autoLaunchGame)
        {
            psi.ArgumentList.Add(CznSgupUri);
        }
        psi.ArgumentList.Add($"--proxy-server={proxyUrl}");

        const string noProxyList = "localhost,127.0.0.1,.cloudfront.net,.playstove.com,.game.onstove.com,dl.onstove.com,static-resource.onstove.com,msg-session.onstove.com";
        psi.Environment["HTTP_PROXY"] = proxyUrl;
        psi.Environment["HTTPS_PROXY"] = proxyUrl;
        psi.Environment["http_proxy"] = proxyUrl;
        psi.Environment["https_proxy"] = proxyUrl;
        psi.Environment["NO_PROXY"] = noProxyList;
        psi.Environment["no_proxy"] = noProxyList;

        logger?.Invoke(autoLaunchGame
            ? "Launching STOVE + Chaos Zero Nightmare (PrepareAndLaunch)..."
            : "Launching STOVE Launcher in Global Region mode...");

        Process.Start(psi);
    }
}
