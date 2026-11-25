# AntiCLI Studio & AntiCLI Server

Bộ ứng dụng **Windows Native (.NET 8)** siêu gọn nhẹ, tiêu thụ ít RAM để lập trình trực tiếp với **Google Antigravity** qua giao thức chính thức **`agy acp`** (Agent Client Protocol) và **`agy` CLI (`stream-json`)**.

Bao gồm **2 phiên bản dùng chung lõi & giao diện**:
1. **`AntiCLI.Server` (Headless Web Server thuần)**: Cài trên **1 máy chủ** (chạy ngầm không cần cửa sổ Desktop, chỉ ~25–35 MB RAM ở trạng thái nghỉ), các máy khác (PC, Laptop, Tablet, Điện thoại — cùng LAN hay **khác mạng qua Internet**) truy cập trực tiếp bằng trình duyệt.
2. **`AntiCLI.exe` (Desktop App Native)**: App Windows độc lập dùng Edge WebView2 (~1 MB `.exe`, ~60 MB RAM).

---

## Tính năng nổi bật

- **Hỗ trợ cả 2 chế độ làm việc trên cùng 1 máy chủ (Multi-Room)**:
  - **Shared Room (Dùng chung & đồng bộ thời gian thực)**: Mở trên nhiều máy/điện thoại vào cùng 1 Room để xem chung tiến trình streaming, tool calls, lịch sử hội thoại và bấm duyệt quyền **Allow/Deny** từ bất kỳ thiết bị nào.
  - **Isolated Room (Độc lập hoàn toàn)**: Tạo `+ Room Độc Lập` ngay trên thanh trên cùng — mỗi Room sở hữu 1 tiến trình `agy acp`, thư mục workspace và PowerShell Terminal riêng biệt, không đè lên máy khác.
- **Bảo mật cho truy cập nhiều nơi / nhiều mạng**:
  - **Màn hình đăng nhập bắt buộc** (mật khẩu lưu bằng PBKDF2-SHA256 100.000 vòng, cookie phiên `HttpOnly` ký `HMAC-SHA256`, tự động khóa tạm 5 phút nếu nhập sai quá 8 lần).
  - Hỗ trợ **Đổi mật khẩu** & **Thu hồi toàn bộ phiên đăng nhập** từ xa ngay trong Settings.
- **Trình duyệt thư mục máy chủ ngay trên Web**: Bấm `Ctrl+O` hoặc chọn workspace để duyệt ổ đĩa/thư mục của máy chủ trực tiếp từ trình duyệt từ xa.
- **Kết nối Antigravity linh hoạt**:
  - Mặc định dùng **Tài khoản Antigravity cá nhân** (chung phiên đăng nhập Google với `agy`).
  - Có tùy chọn chuyển sang **Gemini API Key** (mã hóa bằng Windows DPAPI) + `GOOGLE_GEMINI_BASE_URL` trong Settings.
- **Giao diện CLI Workbench chuyên nghiệp**:
  - Stream Markdown + Syntax Highlighting, *Thinking trace*, thẻ Tool Call + inline Diff, thẻ duyệt quyền `[Y]/[N]`, `/` Slash Commands, `@file` fuzzy attach, `Ctrl+V` dán ảnh, thanh Context Window, Code Viewer/Editor (`Ctrl+S`), Unified/Split Diff Viewer, Wire Protocol Inspector, PowerShell Terminal tích hợp, Command Palette (`Ctrl+K`), 4 theme tối và giao diện responsive cho mobile/tablet.

---

## Cách chạy bản Web Server (1 máy cài, nhiều máy dùng)

### 1. Chạy trên mạng LAN / Wi-Fi nội bộ
```powershell
# Chạy trực tiếp trên cổng 3847 (hiển thị link LAN và mật khẩu tự tạo lần đầu)
powershell -ExecutionPolicy Bypass -File .\start-server.ps1

# Hoặc chạy ngầm không hiện cửa sổ + đặt mật khẩu riêng
powershell -ExecutionPolicy Bypass -File .\start-server.ps1 -Password "MatKhauCuaBan" -Background

# Cài tự động chạy ngầm mỗi khi mở máy Windows (không cần Admin)
powershell -ExecutionPolicy Bypass -File .\install-autostart.ps1
```
Các máy cùng Wi-Fi/LAN mở trình duyệt vào `http://<IP-máy-chủ>:3847` (ví dụ `http://192.168.1.15:3847`).

> Nếu Windows Firewall chặn kết nối từ máy khác trong LAN, mở cổng 3847 (PowerShell Admin) một lần:
> `New-NetFirewallRule -DisplayName "AntiCLI Server" -Direction Inbound -Protocol TCP -LocalPort 3847 -Action Allow`

### 2. Truy cập từ nhiều nơi khác nhau (khác Wi-Fi / 4G / văn phòng — không cùng mạng)

Bạn có **2 cách an toàn nhất** (đều không cần mở port router):

- **Cách A — Nhanh nhất (1 lệnh, HTTPS tức thì bằng Cloudflare Tunnel)**:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\start-tunnel.ps1
  ```
  Script tự tải `cloudflared.exe` chính thức (không cần quyền Admin), mở tunnel HTTPS và in ra link dạng `https://xxxx.trycloudflare.com` để bạn đăng nhập bằng mật khẩu từ bất kỳ mạng nào. Nếu có tên miền riêng trên Cloudflare Zero Trust, truyền thêm `-TunnelToken "<token>"` để giữ URL cố định vĩnh viễn.
- **Cách B — Riêng tư tuyệt đối (VPN mạng lưới Tailscale — khuyến nghị dùng lâu dài)**:
  1. Cài **Tailscale** miễn phí trên máy chủ và trên laptop/điện thoại của bạn (đăng nhập cùng tài khoản Tailscale).
  2. Chạy `.\start-server.ps1 -Background` trên máy chủ.
  3. Từ bất kỳ đâu (4G, quán cafe, công ty), mở trình duyệt vào `http://<Tailscale-IP-máy-chủ>:3847` — lưu lượng được mã hóa WireGuard đầu-cuối và hoàn toàn ẩn khỏi Internet công cộng.

---

## Cách chạy bản Desktop & Build lại

- **Mở bản Desktop cục bộ**: chạy [AntiCLI.exe](dist/AntiCLI/AntiCLI.exe).
- **Mở bản Web Server trực tiếp**: chạy [AntiCLI.Server.exe](dist/AntiCLI-Server/AntiCLI.Server.exe).
- **Build lại cả 2 bản**:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\build.ps1
  # Build bản portable tự chứa .NET 8 runtime (mang sang máy Windows khác không cần cài .NET):
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
  ```

## Phím tắt chính

| Phím tắt | Chức năng |
| :--- | :--- |
| `Enter` / `Shift+Enter` | Gửi lệnh / Xuống dòng |
| `Esc` | Hủy turn đang chạy / Đóng hộp thoại |
| `Shift+Tab` | Đổi nhanh chế độ thực thi (`Default` → `Accept Edits` → `Plan`) |
| `/` | Mở menu Slash Commands (`/plan`, `/compact`, `/goal`, `/usage`, `/diff`…) |
| `@` | Tìm & đính kèm tệp từ workspace |
| `Ctrl+V` | Dán trực tiếp ảnh chụp màn hình vào prompt |
| `Y` / `N` | Duyệt nhanh (`Allow` / `Deny`) thẻ yêu cầu cấp quyền |
| `Ctrl+K` / `Ctrl+P` | Command Palette / Tìm tệp nhanh |
| `Ctrl+O` | Mở thư mục dự án (trình duyệt thư mục máy chủ khi ở chế độ Web) |
| `Ctrl+N` | Phiên mới |
| `Ctrl+B` / `Ctrl+J` / `Ctrl+\` | Bật/tắt Sidebar / Terminal PowerShell / Inspector (Code·Diff·Wire) |
| `Ctrl+,` | Mở Cài đặt, API Key & Bảo mật máy chủ |
| `Ctrl+S` | Lưu tệp đang chỉnh sửa trong Code Inspector |
