using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AntiCliUi.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AntiCliUi;

public sealed class MainForm : Form
{
    private const int DWMWA_USE_IMMUTABLE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMUTABLE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly WebView2 _webView;
    private AppBridge? _bridge;

    public MainForm()
    {
        Text = "AntiCLI Studio";
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1440, 900);
        Width = Math.Max(860, Math.Min(1380, (int)(wa.Width * 0.90)));
        Height = Math.Max(600, Math.Min(880, (int)(wa.Height * 0.90)));
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(11, 14, 20);
        Icon = CreateAppIcon();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(11, 14, 20)
        };
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) => _bridge?.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableDarkTitleBar();
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMUTABLE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMUTABLE_DARK_MODE_BEFORE_20H1, ref enabled, sizeof(int));
        }
        catch { }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AntiCLI",
                "WebView2Data");
            Directory.CreateDirectory(userDataDir);

            var envOptions = new CoreWebView2EnvironmentOptions("--disable-features=msWebOOUI,msPdfOOUI");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir, envOptions);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;

            var wwwroot = ResolveWwwRoot();
            core.SetVirtualHostNameToFolderMapping(
                "anticli.local",
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            _bridge = new AppBridge(
                postJsonToUi: PostJsonToUi,
                pickFolderOnUiThread: PickFolderOnUiThread,
                setWindowTitle: title =>
                {
                    if (IsDisposed) return;
                    if (InvokeRequired) BeginInvoke(() => Text = title);
                    else Text = title;
                });

            core.WebMessageReceived += (_, args) =>
            {
                var raw = args.WebMessageAsJson;
                // WebMessageAsJson wraps string messages in JSON string quotes; accept both raw object & string
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (raw.StartsWith('"'))
                    {
                        try { raw = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? ""; } catch { }
                    }
                    _ = Task.Run(() => _bridge.HandleWebMessageAsync(raw));
                }
            };

            core.Navigate("https://anticli.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Không thể khởi tạo WebView2:\n{ex.Message}\n\nHãy đảm bảo Microsoft Edge WebView2 Runtime đã được cài đặt.",
                "AntiCLI Studio — Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void PostJsonToUi(string json)
    {
        if (IsDisposed || _webView.IsDisposed) return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(() => PostJsonSafe(json));
            else
                PostJsonSafe(json);
        }
        catch { }
    }

    private void PostJsonSafe(string json)
    {
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private string? PickFolderOnUiThread(string? initialDir)
    {
        string? selected = null;
        void ShowDialogAction()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Chọn thư mục dự án (Workspace)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                dlg.InitialDirectory = initialDir;

            if (dlg.ShowDialog(this) == DialogResult.OK)
                selected = dlg.SelectedPath;
        }

        if (InvokeRequired) Invoke(ShowDialogAction);
        else ShowDialogAction();

        return selected;
    }

    private static string ResolveWwwRoot()
    {
        var exeDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(exeDir, "wwwroot");
        if (Directory.Exists(candidate)) return candidate;

        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, "wwwroot");
        if (Directory.Exists(cwdCandidate)) return cwdCandidate;

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new LinearGradientBrush(
                new Rectangle(2, 2, 60, 60),
                Color.FromArgb(17, 24, 39),
                Color.FromArgb(10, 14, 24),
                45f);
            using var path = RoundedRect(new Rectangle(2, 2, 60, 60), 14);
            g.FillPath(bgBrush, path);

            using var borderPen = new Pen(Color.FromArgb(34, 211, 238), 2.5f);
            g.DrawPath(borderPen, path);

            // Stylized >_ prompt glyph
            using var promptPen = new Pen(Color.FromArgb(34, 211, 238), 5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(promptPen, new[]
            {
                new PointF(16, 20),
                new PointF(30, 32),
                new PointF(16, 44)
            });

            using var cursorPen = new Pen(Color.FromArgb(52, 211, 153), 5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(cursorPen, 34, 44, 49, 44);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
