# 🚀 CZN No-VPN Launcher (STOVE Split-Tunnel)

> **Developed by [CutoChan](https://github.com/cutochan)**  
> **Repository**: [https://github.com/cutochan/CZN-No-VPN](https://github.com/cutochan/CZN-No-VPN)

An ultra-lightweight (**~230 KB**, native C# `.NET 8`) split-tunnel launcher that unlocks **Chaos Zero Nightmare (CZN)** on **STOVE PC Launcher** without routing your entire PC through a VPN.

---

## ✨ Key Features

- 🎯 **Smart Domain-Selective Split-Tunneling**:
  - **Tunneled via HTTPS Proxy**: Routes *only* STOVE's region-check and store domains (`*.onstove.com`, `*.onstove.vn`, `*.gate8.com`) so STOVE Launcher always detects a Global region and keeps **Chaos Zero Nightmare** unlocked in your library.
  - **100% Direct Native Connection (`DIRECT`)**: All game patch downloads (`*.cloudfront.net`), **Chaos Zero Nightmare** game servers, Discord, YouTube, Steam, and web browsers connect directly through your native ISP at full speed with zero ping penalty.
- ⚡ **Zero Virtual Adapter & Zero Rebound Restarts**:
  - Does not install any virtual network adapter (no TAP/TUN/WireGuard drivers), preventing STOVE's `NetworkChangeDetector` from triggering forced region-change restarts mid-game.
  - Automatically detects `STOVE.exe` across any drive (`C:`, `D:`, `E:`, etc.) via Windows Registry (`sgup://` protocol handler).
  - Benchmarks upstream nodes in parallel and caches credentials (`proxy_cache.json`) for **~1-second startup**.
  - Automatically restores default Windows proxy settings upon exit.

---

## 📥 Download & Usage

### Option 1: Pre-built Release (Recommended)
1. Download [`CZN_NoVPN_Lightweight.zip`](https://github.com/cutochan/CZN-No-VPN/releases/latest) from the [**Releases**](https://github.com/cutochan/CZN-No-VPN/releases/latest) page.
2. Extract the archive to any folder.
3. Run **`CznNoVpn.exe`** (or run **`Play_CZN_1Click.bat`** to automatically start STOVE and launch the game immediately).
4. Wait 1–2 seconds until the status shows **"● Ready — Split-Tunnel Mode Active"**, then click **🚀 PLAY CHAOS ZERO NIGHTMARE**.
5. While playing, you can click `X` to minimize the app to the **System Tray**. When you finish playing, right-click the System Tray icon and select **Exit Completely (Disable Proxy)**.

> **Note**: The Lightweight edition (~101 KB) requires the [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). If you prefer a zero-dependency executable, you can build or use the Standalone edition below.

---

### Option 2: Build from Source

Requires the [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

```powershell
# Build Lightweight Single-File Executable (Requires .NET 8 Runtime)
dotnet publish CznNoVpn.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./Publish

# Build Standalone Single-File Executable (Runs on any Windows 10/11 64-bit PC without .NET installed)
dotnet publish CznNoVpn.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./PublishStandalone
```

---

## 🛠️ Project Structure

| File | Description |
| :--- | :--- |
| [`Program.cs`](./Program.cs) | Entry point, single-instance mutex guard, `--play` / `--test` CLI flags, and automatic shutdown cleanup hooks. |
| [`MainForm.cs`](./MainForm.cs) | Modern Dark UI dashboard, System Tray integration, and real-time STOVE region policy verifier. |
| [`SurfEasyClient.cs`](./SurfEasyClient.cs) | Anonymous HTTPS tunnel client with RFC 7616 SHA-256 Digest Auth, parallel latency benchmarking, and endpoint caching. |
| [`SmartProxyServer.cs`](./SmartProxyServer.cs) | Local `ArrayPool<byte>` split-tunnel proxy (`127.0.0.1:18080`) routing STOVE domains via TLS tunnel and all other traffic `DIRECT`. |
| [`SystemProxyManager.cs`](./SystemProxyManager.cs) | Automatic `STOVE.exe` path discovery via Registry/drives, WinINET proxy management, and process launcher. |

---

## 👤 Credits

- **Developer**: **[CutoChan](https://github.com/cutochan)**
- **GitHub Repository**: [https://github.com/cutochan/CZN-No-VPN](https://github.com/cutochan/CZN-No-VPN)
