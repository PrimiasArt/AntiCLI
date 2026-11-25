# AntiCLI Studio

Ứng dụng desktop **Windows Native (C# .NET 8 + Edge WebView2)** gọn nhẹ, tiêu thụ ít RAM, mang lại giao diện **CLI Workbench** chuyên nghiệp kết nối trực tiếp với **Google Antigravity** thông qua giao thức chính thức **`agy acp`** (Agent Client Protocol) và **`agy` CLI (`stream-json`)**.

## Điểm nổi bật

- **Nhanh & Nhẹ (Native Windows + Edge WebView2)**: Không dùng Electron, khởi động tức thì, tốn rất ít RAM (~50–90 MB cho host native), tận dụng sẵn WebView2 Runtime trên Windows 10/11.
- **Kết nối Antigravity chính thống (`agy acp` + `stream-json`)**:
  - **Mặc định dùng Tài khoản Antigravity cá nhân**: Dùng chung phiên đăng nhập Google với Antigravity App/CLI (không cần tạo API key riêng).
  - **Tùy chọn chuyển sang Gemini API Key trong Settings**: Khóa API được mã hóa cục bộ bằng **Windows DPAPI** (`DataProtectionScope.CurrentUser`), hỗ trợ thêm `GOOGLE_GEMINI_BASE_URL` cho endpoint tùy biến.
- **Giao diện CLI Workbench chuyên nghiệp**:
  - **Stream lệnh & Agent trung tâm**: Streaming Markdown + Syntax Highlighting, khung suy luận (*Thinking trace*) đóng/mở gọn gàng, thẻ **Tool Call** hiển thị tham số/kết quả, thẻ **Duyệt quyền (Allow / Deny)** trực tiếp kèm phím tắt `Y`/`N`, và thanh tiến trình **Plan** + **Context Window** (`used / 1M`).
  - **Sidebar 3 tab**: Cây thư mục `Files` (có bộ lọc nhanh & nút `@` gắn file), lịch sử `Sessions` (mở/resume phiên cũ), và `Changes` (gộp thay đổi từ ACP Diff + Git status).
  - **Right Inspector**: Trình xem/sửa nhanh mã nguồn (`Code` với nút Sửa + `Ctrl+S`), trình xem `Diff` (Unified & Side-by-Side), và `Wire` Inspector theo dõi trực tiếp gói JSON-RPC.
  - **PowerShell Terminal tích hợp**: Chạy trực tiếp trong thư mục workspace, có nút `▶ Run` ngay trên các khối lệnh shell trong câu trả lời của AI.
  - **4 Theme tối lập trình**: Obsidian Cyan, Tokyo Night, Emerald Terminal, Amber Phosphor.

## Cách chạy nhanh

Chạy trực tiếp file thực thi đã biên dịch tại:

```text
dist\AntiCLI\AntiCLI.exe
```

Hoặc biên dịch lại bằng PowerShell:

```powershell
# Build siêu gọn (~vài MB, dùng .NET 8 đã cài sẵn trên máy)
powershell -ExecutionPolicy Bypass -File .\build.ps1

# Hoặc build bản portable độc lập hoàn toàn (mang sang máy Windows khác không cần .NET 8)
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained
```

## Phím tắt chính

| Phím tắt | Chức năng |
| :--- | :--- |
| `Enter` / `Shift+Enter` | Gửi lệnh / Xuống dòng |
| `Esc` | Hủy turn đang chạy / Đóng hộp thoại |
| `Shift+Tab` | Đổi nhanh chế độ thực thi (`Default` → `Accept Edits` → `Plan`) |
| `/` | Mở menu Slash Commands (`/plan`, `/compact`, `/goal`, `/usage`, `/diff`…) |
| `@` | Tìm & đính kèm tệp từ workspace làm ngữ cảnh |
| `Ctrl+V` | Dán trực tiếp ảnh chụp màn hình vào prompt |
| `Y` / `N` | Duyệt nhanh (`Allow` / `Deny`) khi có thẻ yêu cầu cấp quyền |
| `Ctrl+K` / `Ctrl+P` | Mở Command Palette / Tìm tệp nhanh |
| `Ctrl+O` | Mở thư mục dự án (Workspace) |
| `Ctrl+N` | Tạo phiên làm việc mới |
| `Ctrl+B` | Bật/tắt Sidebar trái |
| `Ctrl+J` | Bật/tắt PowerShell Terminal |
| `Ctrl+\` | Bật/tắt Inspector phải (Code / Diff / Wire) |
| `Ctrl+,` | Mở Cài đặt & Xác thực |
| `Ctrl+S` | Lưu tệp đang chỉnh sửa trong Code Inspector |
