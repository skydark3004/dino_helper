using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Overlay radar: cửa sổ Edge chế độ app (/live/map.html?overlay) được tool giữ luôn nổi trên game.
//  - Khoá: chuột bấm xuyên qua (WS_EX_TRANSPARENT), không lấy focus, cắt bỏ thanh tiêu đề (SetWindowRgn).
//  - Mở khoá: kéo/đổi cỡ bằng thanh tiêu đề của Edge, lăn chuột để đổi bán kính radar.
//  - Độ đậm: cả cửa sổ trong suốt theo % (WS_EX_LAYERED).
// Game phải chạy Borderless/Windowed: fullscreen độc quyền sẽ che overlay.
class EdgeOverlay
{
    public const string Title = "Dino Radar Overlay"; // = <title> của map.html khi có ?overlay
    public IntPtr Hwnd = IntPtr.Zero;
    public bool Locked = true;
    public int Opacity = 85;
    bool applied; bool appliedLocked; int appliedOpacity = -1;

    static string ProfileDir
    {
        get
        {
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(la, @"DinoReplayReader\EdgeOverlay");
            // tên cũ của app (IsleReplayReader): chuyển hồ sơ sang để giữ vị trí/cỡ overlay đã lưu
            string old = Path.Combine(la, @"IsleReplayReader\EdgeOverlay");
            if (!Directory.Exists(dir) && Directory.Exists(old))
                try { Directory.CreateDirectory(Path.GetDirectoryName(dir)); Directory.Move(old, dir); try { Directory.Delete(Path.GetDirectoryName(old)); } catch { } } catch { }
            return dir;
        }
    }

    static string EdgePath()
    {
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA" })
        {
            string b = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(b)) continue;
            string p = Path.Combine(b, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public bool IsOpen { get { return Hwnd != IntPtr.Zero && IsWindow(Hwnd); } }

    // trả về thông báo lỗi, hoặc null nếu đã mở
    public string Open(string url)
    {
        // Cửa sổ radar còn sót (vd từ lần chạy tool trước, trỏ tới cổng máy chủ cũ đã chết -> ảnh bản đồ không tải được):
        // đóng rồi mở trang mới với địa chỉ hiện tại, không dùng lại.
        IntPtr old = IsOpen ? Hwnd : Find();
        if (old != IntPtr.Zero)
        {
            PostMessage(old, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < 40 && IsWindow(old); i++) System.Threading.Thread.Sleep(50);
            Hwnd = IntPtr.Zero;
        }
        string edge = EdgePath();
        if (edge == null) return "Không tìm thấy Microsoft Edge (msedge.exe).";
        // hồ sơ Edge riêng: không đụng trình duyệt đang dùng. Vị trí/cỡ do tool tự nhớ (Edge không nhớ cho cửa sổ --app).
        string args = "--app=\"" + url + "\" --user-data-dir=\"" + ProfileDir + "\" --no-first-run --no-default-browser-check --disable-features=Translate";
        int[] b = LoadPlacement();
        if (b == null)
        {
            var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            b = new[] { wa.Right - 400, wa.Top + 320, 380, 420 };
        }
        args += string.Format(" --window-position={0},{1} --window-size={2},{3}", b[0], b[1], b[2], b[3]);
        try { Process.Start(new ProcessStartInfo(edge, args) { UseShellExecute = false }); }
        catch (Exception ex) { return "Không mở được Edge: " + ex.Message; }
        applied = false;
        return null;
    }

    public void Close()
    {
        if (IsOpen) PostMessage(Hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
        Hwnd = IntPtr.Zero; applied = false;
    }

    // gọi định kỳ (timer): tìm cửa sổ, áp kiểu khi trạng thái đổi, giữ luôn nổi
    public void Tick()
    {
        if (!IsOpen)
        {
            Hwnd = Find();
            applied = false;
            if (Hwnd == IntPtr.Zero) return;
        }
        if (!applied || appliedLocked != Locked || appliedOpacity != Opacity) Apply();
        RECT r;
        if (GetWindowRect(Hwnd, out r) && !IsIconic(Hwnd) && r.Right - r.Left >= 100 && r.Bottom - r.Top >= 100
            && (r.Left != saved.Left || r.Top != saved.Top || r.Right != saved.Right || r.Bottom != saved.Bottom))
        {
            saved = r; // người dùng vừa kéo/đổi cỡ -> nhớ cho lần mở sau
            try { File.WriteAllText(PlacementFile, string.Format("{0},{1},{2},{3}", r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)); } catch { }
        }
        // giữ trên cùng (game borderless đôi khi được đưa lên trên khi lấy focus)
        SetWindowPos(Hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
    }

    void Apply()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
        int ex = GetWindowLong(Hwnd, GWL_EXSTYLE) | WS_EX_LAYERED;
        ex = Locked ? ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : ex & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        SetWindowLong(Hwnd, GWL_EXSTYLE, ex);
        SetLayeredWindowAttributes(Hwnd, 0, (byte)(Math.Max(20, Math.Min(100, Opacity)) * 255 / 100), 2 /*LWA_ALPHA*/);
        if (Locked)
        {
            // chỉ giữ phần nội dung trang, bỏ thanh tiêu đề / viền. Edge tự vẽ thanh tiêu đề trong vùng client,
            // nên lấy khung của cửa sổ con hiển thị trang (Chrome_RenderWidgetHostHWND); không thấy thì dùng vùng client.
            RECT wr, cr;
            GetWindowRect(Hwnd, out wr);
            IntPtr page = FindChild(Hwnd, "Chrome_RenderWidgetHostHWND");
            if (page != IntPtr.Zero) GetWindowRect(page, out cr);
            else
            {
                POINT o = new POINT(); GetClientRect(Hwnd, out cr); ClientToScreen(Hwnd, ref o);
                cr.Left += o.X; cr.Right += o.X; cr.Top += o.Y; cr.Bottom += o.Y;
            }
            SetWindowRgn(Hwnd, CreateRectRgn(cr.Left - wr.Left, cr.Top - wr.Top, cr.Right - wr.Left, cr.Bottom - wr.Top), true);
        }
        else SetWindowRgn(Hwnd, IntPtr.Zero, true);
        applied = true; appliedLocked = Locked; appliedOpacity = Opacity;
    }

    static IntPtr Find()
    {
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(256);
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            sb.Length = 0; GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != "Chrome_WidgetWin_1") return true;
            sb.Length = 0; GetWindowText(h, sb, sb.Capacity);
            string t = sb.ToString();
            if (t == Title || t == "Isle Radar Overlay") { found = h; return false; } // tên cũ: trang map.html cũ còn mở
            return true;
        }, IntPtr.Zero);
        return found;
    }

    RECT saved;
    static string PlacementFile { get { return Path.Combine(ProfileDir, "overlay_placement.txt"); } }
    static int[] LoadPlacement()
    {
        try
        {
            var p = File.ReadAllText(PlacementFile).Split(',');
            var b = new int[4];
            for (int i = 0; i < 4; i++) b[i] = int.Parse(p[i].Trim());
            // còn nằm trên một màn hình nào đó (đã rút màn hình phụ -> về mặc định)
            var rc = new System.Drawing.Rectangle(b[0], b[1], b[2], b[3]);
            foreach (var s in System.Windows.Forms.Screen.AllScreens) if (s.WorkingArea.IntersectsWith(rc) && b[2] >= 100 && b[3] >= 100) return b;
        }
        catch { }
        return null;
    }

    static IntPtr FindChild(IntPtr parent, string cls)
    {
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(256);
        EnumChildWindows(parent, (h, l) =>
        {
            sb.Length = 0; GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == cls && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
