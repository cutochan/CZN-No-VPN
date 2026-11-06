# 🚀 CZN No-VPN Launcher (STOVE Split-Tunnel)

> **Developed by [CutoChan](https://github.com/cutochan)**  
> **Repository**: [https://github.com/cutochan/CZN-No-VPN](https://github.com/cutochan/CZN-No-VPN)

Ứng dụng siêu nhẹ (**~230 KB**, viết bằng C# `.NET 8` thuần) giúp mở khóa và chơi **Chaos Zero Nightmare (CZN)** trên **STOVE Launcher PC** tại Việt Nam **mà không cần bật VPN toàn máy**.

---

## ✨ Tính năng nổi bật

- 🎯 **Split-Tunnel Thông Minh (Chỉ định tuyến STOVE)**:
  - **Chỉ định tuyến qua HTTPS Tunnel**: Các tên miền kiểm tra vùng và cửa hàng của STOVE (`*.onstove.com`, `*.onstove.vn`, `*.gate8.com`) để STOVE Launcher luôn nhận vùng Quốc Tế và hiển thị **Chaos Zero Nightmare**.
  - **100% Mạng Gốc Việt Nam (`DIRECT`)**: Mọi kết nối tải bản cập nhật game (`*.cloudfront.net`), máy chủ game **Chaos Zero Nightmare**, Discord, YouTube, Steam, trình duyệt... đều đi trực tiếp qua mạng gốc Việt Nam của bạn (không tăng ping, không bóp băng thông).
- ⚡ **Siêu Nhẹ & Tự Động Hóa**:
  - Không cài đặt card mạng ảo (TAP/TUN/WireGuard), không làm `NetworkChangeDetector` của STOVE báo đổi IP hay bắt khởi động lại Launcher giữa chừng.
  - Tự động dò tìm đường dẫn `STOVE.exe` trên mọi ổ đĩa (`C:`, `D:`, `E:`...) thông qua Windows Registry.
  - Tự động đo ping song song các node và lưu bộ nhớ đệm (`proxy_cache.json`) giúp khởi động chỉ trong **~1 giây**.
  - Tự động khôi phục cài đặt Proxy mặc định của Windows ngay khi thoát ứng dụng.

---

## 📥 Cách tải và sử dụng

### Cách 1: Tải bản dựng sẵn (Recommended)
1. Tải file nén siêu nhẹ tại [`Share/CZN_NoVPN_SieuNhe_CanDotNet8.zip`](./Share/CZN_NoVPN_SieuNhe_CanDotNet8.zip) (hoặc trong mục **Releases**).
2. Giải nén ra một thư mục bất kỳ.
3. Mở **`CznNoVpn.exe`** (hoặc chạy **`Choi_CZN_Ngay_1Click.bat`** để tự động mở STOVE và vào thẳng game).
4. Đợi 1–2 giây cho trạng thái báo **"● Đã sẵn sàng — Chế độ Split-Tunnel Hoạt Động"** rồi bấm **🚀 CHƠI CHAOS ZERO NIGHTMARE**.
5. Khi đang chơi, bạn có thể bấm `X` để thu nhỏ ứng dụng xuống khay hệ thống (**System Tray**). Khi nghỉ chơi, chuột phải vào biểu tượng ở khay hệ thống chọn **Thoát hoàn toàn (Tắt Proxy)**.

> **Lưu ý**: Bản siêu nhẹ (~100 KB) yêu cầu máy có **.NET 8.0 Desktop Runtime**. Nếu máy chưa có, Windows sẽ tự hiện thông báo kèm nút tải chính chủ từ Microsoft.

---

### Cách 2: Tự biên dịch từ mã nguồn (Build from Source)

Yêu cầu cài đặt [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Biên dịch bản siêu nhẹ (Framework-dependent Single-File)
dotnet publish CznNoVpn.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./Publish

# Hoặc biên dịch bản Độc Lập chạy trên mọi máy không cần .NET Runtime (Self-contained Single-File)
dotnet publish CznNoVpn.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./PublishStandalone
```

---

## 🛠️ Cấu trúc dự án

| File | Mô tả |
| :--- | :--- |
| [`Program.cs`](./Program.cs) | Điểm khởi chạy ứng dụng, quản lý Single-Instance Mutex, chế độ `--play` / `--test` và dọn dẹp hệ thống khi thoát. |
| [`MainForm.cs`](./MainForm.cs) | Giao diện điều khiển chính (Dark UI), khay hệ thống (System Tray), kiểm tra trạng thái vùng STOVE theo thời gian thực. |
| [`SurfEasyClient.cs`](./SurfEasyClient.cs) | Giao thức khởi tạo HTTPS Tunnel ẩn danh, xác thực Digest SHA-256, tự động đo độ trễ (latency) và chọn node nhanh nhất. |
| [`SmartProxyServer.cs`](./SmartProxyServer.cs) | Máy chủ Split-Tunnel cục bộ (`127.0.0.1:18080`) dùng `ArrayPool<byte>` phân luồng thông minh giữa STOVE Tunnel và mạng gốc `DIRECT`. |
| [`SystemProxyManager.cs`](./SystemProxyManager.cs) | Tự động nhận diện vị trí cài đặt `STOVE.exe`, cấu hình/khôi phục WinINET Proxy & danh sách Bypass tối ưu. |

---

## 👤 Credits

- **Developer**: **[CutoChan](https://github.com/cutochan)**
- **GitHub Repository**: [https://github.com/cutochan/CZN-No-VPN](https://github.com/cutochan/CZN-No-VPN)
