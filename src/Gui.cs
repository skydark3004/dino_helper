using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Ghi log từ luồng xử lý sang ô log trên giao diện
class UiWriter : TextWriter
{
    readonly TextBox box; readonly StringBuilder line = new StringBuilder();
    public UiWriter(TextBox b) { box = b; }
    public override Encoding Encoding { get { return Encoding.UTF8; } }
    public override void Write(char c)
    {
        if (c == '\n') { string s = line.ToString().TrimEnd('\r'); line.Clear(); Append(s + "\r\n"); }
        else line.Append(c);
    }
    public override void Write(string s) { foreach (char c in s ?? "") Write(c); }
    void Append(string s)
    {
        if (box.IsDisposed) return;
        box.BeginInvoke((Action)(() => box.AppendText(s)));
    }
}

class MainForm : Form
{
    // thư mục replay của game (ISLE_DEMOS_DIR chỉ dùng khi thử nghiệm, để không đụng thư mục thật)
    static readonly string DemosDir = Environment.GetEnvironmentVariable("ISLE_DEMOS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TheIsle\Saved\Demos");

    ListView list; TextBox log; CheckBox chkFull, chkOpenMap;
    Button btnRun, btnOpenOut, btnOpenMap, btnDelete, btnLiveMap;
    string lastOut;
    TextBox txtCutFrom, txtCutTo; Button btnCut; volatile bool cutBusy;
    TabControl tabs; TabPage tabCutPage, tabMapPage, tabPvnPage, tabUpdatePage;
    // tab "Cập nhật"
    Label lblVerCur, lblVerNew, lblUpdStatus; TextBox txtNotes; Button btnCheckUpd, btnDoUpd; CheckBox chkAutoUpd;
    Updater.Info latestInfo; volatile bool updBusy;
    // lớp bản đồ bật/tắt: khoá (giống map_template.html) -> tên hiển thị; lưu ở map_layers.txt cạnh exe
    static readonly string[][] LayerDefs = {
        new[] { "player", "Người chơi khác" }, new[] { "ghost", "Mất dấu (vị trí cuối khi ra khỏi tầm)" },
        new[] { "animal", "Thú AI (hươu, lợn, thỏ, gà, cua…)" },
        new[] { "corpse", "Xác" }, new[] { "fish", "Cá" }, new[] { "meat", "Thịt / nội tạng" },
        new[] { "poi", "Địa danh" }, new[] { "mud", "Vũng bùn" }, new[] { "gastro", "Sỏi đã nhặt (tự ghi)" },
        new[] { "pvngastro", "Sỏi theo PVN (không chính xác)" }, new[] { "map", "Ảnh bản đồ Gateway" },
        new[] { "heat", "Bản đồ nhiệt PVN (cần cookie, xem dưới)" } };
    // chỉ số khủng long của bạn qua WebSocket của PVN Overlay (PvnLive.cs) -> cửa sổ overlay riêng (VitalsOverlay.cs)
    readonly PvnLive pvnLive = new PvnLive(); CheckBox chkPvnLive, chkVitalsOv, chkVitalsNum, chkCombat; Label lblPvnLive; int pvnLastFrames; bool pvnKickLogged;
    readonly PvnCombat pvnCombat = new PvnCombat(); // máu đối thủ khi đánh nhau (combatlog)
    readonly PvnFriends pvnFriends = new PvnFriends(); // bạn bè/group từ PVN (/map) -> chấm xanh
    Button btnCombatRate, btnScanKey; Label lblPvnKey;
    string CombatRateText() { return pvnCombat.PollMs == 500 ? "Nhịp gọi: 0,5 s (nhấn để về 2 s)" : "Nhịp gọi: 2 s (nhấn để xuống 0,5 s)"; }
    // bật/tắt combat theo đã có khoá PVN chưa (combatlog cần ký HMAC)
    void UpdateCombatEnabled()
    {
        bool hasKey = PvnSign.HasKey;
        chkCombat.Enabled = hasKey; if (!hasKey) chkCombat.Checked = false;
        chkFeed.Enabled = hasKey; if (!hasKey) chkFeed.Checked = false;
        btnCombatRate.Enabled = hasKey;
        if (lblPvnKey != null)
        {
            lblPvnKey.ForeColor = hasKey ? Color.DarkGreen : Color.Gray;
            lblPvnKey.Text = hasKey ? "Đã có khoá PVN → combat dùng được." : "Chưa có khoá PVN → bấm \"Quét khoá PVN\" (cần đã cài PVN Overlay trên máy này).";
        }
    }
    // nút "Quét khoá PVN": dò bản PVN Overlay đã cài (luồng nền vì có thể quét tiến trình ~0,8 s), lưu pvn_hmac.txt
    void ScanPvnKey()
    {
        btnScanKey.Enabled = false; string old = btnScanKey.Text; btnScanKey.Text = "Đang quét…";
        new Thread(() =>
        {
            bool ok = PvnSign.Scan();
            try
            {
                BeginInvoke((Action)(() =>
                {
                    btnScanKey.Enabled = true; btnScanKey.Text = old;
                    UpdateCombatEnabled();
                    log.AppendText(ok ? "Đã tìm thấy khoá PVN (combat bật được).\r\n" : "Chưa tìm thấy khoá PVN — cần đã cài PVN Overlay trên máy này.\r\n");
                }));
            }
            catch { }
        }) { IsBackground = true }.Start();
    }
    VitalsOverlay vitalsOv;
    CompassOverlay compassOv; CheckBox chkCompass; volatile bool compassOn;
    CombatFeed feedOv; CheckBox chkFeed;
    void UpdateVitalsWanted() { if (vitalsOv != null) vitalsOv.Wanted = chkPvnLive.Checked && chkVitalsOv.Checked; }
    // bản đồ nhiệt PVN: cookie đăng nhập (đã giải mã) + trạng thái gọi API mỗi 5 s
    string pvnToken; bool pvnAuthFail; volatile bool heatBusy; DateTime heatNext = DateTime.MinValue; bool heatPushedEmpty = true;
    Label lblPvn; TextBox txtPvn;
    readonly Dictionary<string, CheckBox> layerChecks = new Dictionary<string, CheckBox>(); ListView cutList; Label lblCutInfo; // tab "Cắt replay" (danh sách riêng)
    // báo khủng long người chơi mới vào vùng (khi đang quay)
    CheckBox chkAlert, chkAlertSound;
    readonly Dictionary<string, DateTime> alertPresent = new Dictionary<string, DateTime>(); // SteamId -> lần cuối có mặt (chỉ luồng theo dõi dùng)
    // tự cập nhật: file replay đang theo dõi + trạng thái lần trích xuất gần nhất
    CheckBox chkLive; Label lblLive; System.Windows.Forms.Timer liveTimer, liveFastTimer;
    DateTime perfShownAt = DateTime.MinValue; int perfHidden; // giới hạn dòng [chậm] trong ô log
    string watchFile; DateTime watchTime; long watchLen; volatile bool runBusy, liveBusy; // trích xuất thủ công và tự theo dõi chạy độc lập
    bool liveRunning; // replay đang theo dõi còn đang được game quay (chưa dừng)
    // bộ đọc giữ trong bộ nhớ -> mỗi lần chỉ đọc phần game vừa ghi thêm
    Replay liveRp; long liveRpLen; DateTime lastFullReport, lastFileWrite;
    FileSystemWatcher dirWatcher; // báo khi thư mục Demos thay đổi
    LiveServer server; // máy chủ web trong máy: mở bản đồ qua http:// để tool đẩy dữ liệu mới vào ngay
    readonly List<string> extraFiles = new List<string>();
    // overlay radar (cửa sổ Edge luôn nổi trên game)
    readonly EdgeOverlay overlay = new EdgeOverlay();
    Button btnOverlay; CheckBox chkOverlayLock; NumericUpDown numOverlayOpacity; System.Windows.Forms.Timer ovTimer;
    bool ovWanted, ovSeen; DateTime ovOpenedAt;
    const int HOTKEY_OVERLAY = 0x4952, HOTKEY_ZOOM_IN = 0x4953, HOTKEY_ZOOM_OUT = 0x4954;

    public MainForm()
    {
        Text = "Dino Replay Reader";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(840, 760);
        MinimumSize = new Size(720, 620);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // khung ngoài: 2 tab ở trên, ô log dùng chung ở dưới
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 2 };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 76));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        Controls.Add(outer);
        tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 4) };
        var tabRead = new TabPage("Đọc replay & bản đồ");
        tabCutPage = new TabPage("Cắt replay");
        tabMapPage = new TabPage("Bản đồ & overlay");
        tabPvnPage = new TabPage("PVN trực tiếp");
        tabs.TabPages.Add(tabRead); tabs.TabPages.Add(tabMapPage); tabs.TabPages.Add(tabPvnPage); tabs.TabPages.Add(tabCutPage);
        outer.Controls.Add(tabs);

        // tab 1: đọc replay, bản đồ, theo dõi trực tiếp, báo, overlay
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tabRead.Controls.Add(root);

        root.Controls.Add(new Label { Text = "Replay trong thư mục game (mới nhất ở trên). Nhấp đúp: trích xuất (mỗi lần 1 file), hoặc mở bản đồ nếu đã trích xuất. Ctrl/Shift: chọn nhiều để xoá kết quả.", AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(0, 0, 0, 6) });

        list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, ShowItemToolTips = true };
        list.Columns.Add("Thời gian quay", 130);
        list.Columns.Add("Độ dài", 60, HorizontalAlignment.Right);
        list.Columns.Add("Trạng thái", 110);
        list.Columns.Add("Tên file", 420);
        list.DoubleClick += (s, e) => OnDoubleClick();
        list.SelectedIndexChanged += (s, e) =>
        {
            btnRun.Enabled = list.SelectedItems.Count > 0;
            btnDelete.Enabled = SelectedOutputDirs().Count > 0;
        };
        root.Controls.Add(list);

        var row1 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        row1.Controls.Add(MakeButton("Làm mới", (s, e) => LoadList()));
        row1.Controls.Add(MakeButton("Chọn file khác…", (s, e) => PickFiles()));
        row1.Controls.Add(MakeButton("Mở thư mục Demos", (s, e) => OpenPath(DemosDir)));
        row1.Controls.Add(MakeButton("Mở thư mục output", (s, e) => { Directory.CreateDirectory(Path.Combine(Program.ExeDir, "output")); OpenPath(Path.Combine(Program.ExeDir, "output")); }));
        root.Controls.Add(row1);

        var row2 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        btnRun = MakeButton("Trích xuất", (s, e) => Run());
        btnRun.Font = new Font(Font, FontStyle.Bold);
        btnRun.Width = 120; btnRun.Enabled = false;
        chkOpenMap = new CheckBox { Text = "Mở bản đồ khi xong", Checked = true, AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
        chkFull = new CheckBox { Text = "Xuất thêm dữ liệu thô (events.csv, positions.csv - nặng)", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
        chkLive = new CheckBox { Text = "Tự theo dõi replay đang quay (tự chuyển sang file mới khi script cắt file)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
        chkLive.CheckedChanged += (s, e) => { if (!chkLive.Checked) StopWatching("đã tắt tự theo dõi"); else LiveScan(); };
        btnLiveMap = MakeButton("Mở bản đồ trực tiếp", (s, e) => OpenLiveMap());
        btnLiveMap.Margin = new Padding(12, 0, 0, 0);
        lblLive = new Label { AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(12, 7, 0, 0), Text = "Chưa thấy replay nào đang quay" };
        row2.Controls.Add(btnRun); row2.Controls.Add(chkOpenMap); row2.Controls.Add(chkFull);
        row2.SetFlowBreak(chkFull, true);
        row2.Controls.Add(chkLive); row2.Controls.Add(btnLiveMap); row2.Controls.Add(lblLive);
        row2.SetFlowBreak(lblLive, true);
        chkAlert = new CheckBox { Text = "Báo khi có khủng long người chơi mới vào vùng (góc màn hình, không chiếm focus game)", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 0, 0) };
        chkAlertSound = new CheckBox { Text = "kèm âm thanh", AutoSize = true, Checked = true, Margin = new Padding(12, 4, 0, 0) };
        var btnTestAlert = MakeButton("Thử", (s, e) => ShowAlerts(new List<DinoAlert> { new DinoAlert { Title = "Tyrannosaurus · Tên người chơi", Detail = "đực, 86% · 120 m · trên-phải radar (Đông Bắc) · phía sau bên phải" } }));
        btnTestAlert.Margin = new Padding(12, 0, 0, 0);
        row2.Controls.Add(chkAlert); row2.Controls.Add(chkAlertSound); row2.Controls.Add(btnTestAlert);
        BuildMapTab();
        BuildPvnTab();
        vitalsOv = new VitalsOverlay(pvnLive, pvnCombat, LoadSetting, SaveSetting) { Locked = chkOverlayLock.Checked, ShowNumbers = chkVitalsNum.Checked };
        vitalsOv.SetOpacity((int)numOverlayOpacity.Value);
        UpdateVitalsWanted();
        compassOv = new CompassOverlay(LoadSetting, SaveSetting) { Locked = chkOverlayLock.Checked, Wanted = chkCompass.Checked };
        compassOv.SetOpacity((int)numOverlayOpacity.Value);
        compassOn = chkCompass.Checked;
        // combat: gọi combatlog khi bật kết nối PVN + ô combat + đang nhận live (đang trong game) + có khoá HMAC
        pvnCombat.TokenProvider = () => { string b, e; return PvnToken.Load(out b, out e); };
        pvnCombat.Enabled = () => chkPvnLive.Checked && (chkCombat.Checked || chkFeed.Checked) && pvnLive.Ok;
        vitalsOv.FoesOn = () => chkCombat.Checked;
        feedOv = new CombatFeed(LoadSetting, SaveSetting) { Locked = chkOverlayLock.Checked, Wanted = chkFeed.Checked };
        feedOv.SetOpacity((int)numOverlayOpacity.Value);
        if (chkPvnLive.Checked) pvnCombat.Start(PvnToken.SteamId());
        pvnFriends.TokenProvider = () => { string b, e; return PvnToken.Load(out b, out e); };
        pvnFriends.Enabled = () => chkPvnLive.Checked;
        pvnFriends.Changed = json => { if (server != null) server.PushFriends(json); };
        if (chkPvnLive.Checked) pvnFriends.Start();
        ovTimer = new System.Windows.Forms.Timer { Interval = 250 };
        ovTimer.Tick += (s, e) => OverlayTick();
        ovTimer.Start();
        // mỗi giây: tìm replay đang quay (dự phòng cho sự kiện thay đổi file của Windows) + cập nhật nếu có dữ liệu mới
        liveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        // (lần đầu đọc cài đặt log ở tick đầu tiên, sau khi ô log đã được tạo)
        liveTimer.Tick += (s, e) => { LiveScan(); LiveTick(); RefreshPerfSetting(); PerfLog.Flush(); HeatTick(); PvnLiveTick(); };
        liveTimer.Start();
        // Không chỉ dựa vào FileSystemWatcher (có máy không báo / báo chậm -> bản đồ chỉ cập nhật mỗi 1 s theo liveTimer):
        // khi đang theo dõi, cứ 100 ms hỏi kích thước file qua handle đang giữ (rất rẻ), có dữ liệu mới thì đọc ngay.
        // chỗ chậm (tool, game, trang bản đồ/radar) -> perf_log.txt; ô log chỉ hiện tối đa 1 dòng / 20 s (đủ để biết, chi tiết xem file)
        PerfLog.Logged += line =>
        {
            if (line.Contains("nhân CPU") || line.Contains("đọc lần đầu") || line.Contains("[game] ghi replay mỗi") || line.Contains("mở trang · GPU")) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if ((DateTime.Now - perfShownAt).TotalSeconds < 20) { perfHidden++; return; }
                    perfShownAt = DateTime.Now;
                    log.AppendText("[chậm] " + line.Substring(11) + (perfHidden > 0 ? string.Format("  (+{0} dòng khác trong perf_log.txt)", perfHidden) : "") + "\r\n");
                    perfHidden = 0;
                }));
            }
            catch { }
        };
        liveFastTimer = new System.Windows.Forms.Timer { Interval = 100 };
        liveFastTimer.Tick += (s, e) => { if (watchFile != null) LiveTick(); };
        liveFastTimer.Start();
        root.Controls.Add(row2);

        // tab 2: cắt 1 đoạn replay thành file mới (để xem lại trong game)
        var cutLay = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), ColumnCount = 1, RowCount = 5 };
        cutLay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cutLay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cutLay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cutLay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cutLay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tabCutPage.Controls.Add(cutLay);
        cutLay.Controls.Add(new Label { Text = "Chọn 1 replay, nhập đoạn cần cắt rồi bấm Cắt. File mới nằm cạnh file gốc (thư mục Demos → xem được ngay trong menu Replay của game).", AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(0, 0, 0, 6) });
        cutList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        cutList.Columns.Add("Thời gian quay", 130);
        cutList.Columns.Add("Độ dài", 60, HorizontalAlignment.Right);
        cutList.Columns.Add("Trạng thái", 110);
        cutList.Columns.Add("Tên file", 420);
        cutList.SelectedIndexChanged += (s, e) => UpdateCutInfo();
        cutList.DoubleClick += (s, e) => { txtCutFrom.Focus(); txtCutFrom.SelectAll(); };
        cutLay.Controls.Add(cutList);
        var rowCutFiles = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        rowCutFiles.Controls.Add(MakeButton("Làm mới", (s, e) => LoadList()));
        rowCutFiles.Controls.Add(MakeButton("Chọn file khác…", (s, e) => PickFiles()));
        rowCutFiles.Controls.Add(MakeButton("Mở thư mục Demos", (s, e) => OpenPath(DemosDir)));
        cutLay.Controls.Add(rowCutFiles);

        var rowCut = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        lblCutInfo = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 7, 16, 0), Text = "Chưa chọn replay" };
        rowCut.Controls.Add(lblCutInfo);
        rowCut.Controls.Add(new Label { Text = "Từ", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        txtCutFrom = new TextBox { Width = 70, Margin = new Padding(0, 3, 8, 0) };
        rowCut.Controls.Add(txtCutFrom);
        rowCut.Controls.Add(new Label { Text = "Đến", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        txtCutTo = new TextBox { Width = 70, Margin = new Padding(0, 3, 8, 0) };
        rowCut.Controls.Add(txtCutTo);
        btnCut = MakeButton("Cắt", (s, e) => CutSelected());
        btnCut.Width = 70;
        rowCut.Controls.Add(btnCut);
        txtCutFrom.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CutSelected(); } };
        txtCutTo.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CutSelected(); } };
        cutLay.Controls.Add(rowCut);
        cutLay.Controls.Add(new Label { Text = "Thời gian: phút, phút:giây hoặc giờ:phút:giây (vd 52, 52:30, 1:05:00) · để trống \"Đến\" = cắt tới hết.\r\nFile mới tên <tên>_cat_52-00_den_het.replay. Game xoá thư mục Demos khi khởi động lại – chép ra chỗ khác nếu muốn giữ.", AutoSize = true, ForeColor = Color.Gray, MaximumSize = new Size(740, 0), Margin = new Padding(0, 6, 0, 0) });

        log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, WordWrap = false, Margin = new Padding(0, 6, 0, 0) };
        outer.Controls.Add(log);

        var row3 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        btnOpenOut = MakeButton("Mở thư mục kết quả", (s, e) => OpenPath(lastOut));
        btnOpenMap = MakeButton("Mở bản đồ", (s, e) => OpenMap(lastOut));
        btnOpenOut.Enabled = btnOpenMap.Enabled = false;
        row3.Controls.Add(btnOpenOut); row3.Controls.Add(btnOpenMap);
        btnDelete = MakeButton("Xoá kết quả đã chọn", (s, e) => DeleteSelectedOutputs());
        btnDelete.Margin = new Padding(24, 0, 6, 0);
        btnDelete.Enabled = false;
        row3.Controls.Add(btnDelete);
        row3.Controls.Add(MakeButton("Xoá tất cả kết quả", (s, e) => DeleteAllOutputs()));
        root.Controls.Add(row3);

        // chuột phải trên danh sách
        var menu = new ContextMenuStrip();
        menu.Items.Add("Trích xuất", null, (s, e) => Run());
        menu.Items.Add("Mở bản đồ", null, (s, e) => OnDoubleClick());
        menu.Items.Add("Cắt đoạn…", null, (s, e) => GoToCut());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Xoá kết quả", null, (s, e) => DeleteSelectedOutputs());
        list.ContextMenuStrip = menu;
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) DeleteSelectedOutputs(); };

        server = new LiveServer(Program.ExeDir);
        if (!server.Start()) server = null;
        PushLayers(); // trang mở sau sẽ nhận ngay cài đặt lớp khi kết nối
        if (chkPvnLive.Checked) pvnLive.Start();
        // cho thấy máy chủ bản đồ đang chạy ở đâu
        Text = server != null ? "Dino Replay Reader  ·  máy chủ bản đồ: http://127.0.0.1:" + server.Port : "Dino Replay Reader  ·  (không mở được máy chủ bản đồ - bản đồ sẽ mở dạng file)";
        Shown += (s, e) => { LoadList(); LiveScan(); };
        // báo ngay khi có file .replay mới / thay đổi trong thư mục Demos (game bắt đầu quay, script đổi tên...)
        try
        {
            if (Directory.Exists(DemosDir))
            {
                dirWatcher = new FileSystemWatcher(DemosDir, "*.replay") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                dirWatcher.Changed += (s, e) => OnDemosChanged(e.FullPath);
                dirWatcher.Created += (s, e) => OnDemosChanged(e.FullPath);
                dirWatcher.Renamed += (s, e) => OnDemosChanged(e.FullPath);
                dirWatcher.Deleted += (s, e) => OnDemosChanged(e.FullPath);
                dirWatcher.EnableRaisingEvents = true;
            }
        }
        catch { dirWatcher = null; }
    }

    void OnDemosChanged(string path)
    {
        try
        {
            BeginInvoke((Action)(() =>
            {
                // file đang theo dõi thay đổi -> cập nhật ngay; file khác -> có thể là lần quay mới
                if (watchFile != null && string.Equals(path, watchFile, StringComparison.OrdinalIgnoreCase)) LiveTick();
                else LiveScan();
            }));
        }
        catch { }
    }

    Button MakeButton(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(6, 0, 6, 0), Margin = new Padding(0, 0, 6, 0) };
        b.Click += click;
        return b;
    }

    // ---------- danh sách ----------
    void LoadList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        var files = new List<string>(extraFiles);
        if (Directory.Exists(DemosDir)) files.AddRange(Directory.GetFiles(DemosDir, "*.replay"));
        list.Groups.Clear();
        var groups = new Dictionary<DateTime, ListViewGroup>();
        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => File.GetLastWriteTime(f)))
        {
            var fi = new FileInfo(f);
            DateTime hdrStart; double len; bool live;
            bool ok = ReadHeader(f, out hdrStart, out len, out live);
            // Giờ thật: game ghi file tới lúc dừng quay -> kết thúc = lần sửa cuối, bắt đầu = kết thúc - độ dài.
            // (Không dùng giờ trong header vì script AHK đã lùi nó 30 phút; cách này đúng cả khi file bị đổi tên.)
            DateTime end = fi.LastWriteTime, start = ok ? end.AddSeconds(-len) : end;

            ListViewGroup grp;
            if (!groups.TryGetValue(start.Date, out grp))
            {
                string title = start.Date == DateTime.Today ? "Hôm nay" : start.Date == DateTime.Today.AddDays(-1) ? "Hôm qua" : start.ToString("dddd, dd/MM/yyyy");
                grp = new ListViewGroup(title + " (" + start.ToString("dd/MM") + ")");
                groups[start.Date] = grp; list.Groups.Add(grp);
            }

            var it = new ListViewItem(start.ToString("HH:mm") + " → " + end.ToString("HH:mm"), grp) { Tag = f, UseItemStyleForSubItems = false };
            it.SubItems.Add(ok ? TimeSpan.FromSeconds(len).ToString(len >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "?");

            string outDir = Program.DefaultOutDir(f);
            bool done = File.Exists(Path.Combine(outDir, "summary.txt"));
            var st = it.SubItems.Add(live ? "● Đang quay" : done ? "✓ Đã trích xuất" : "Chưa trích xuất");
            st.ForeColor = live ? Color.Firebrick : done ? Color.ForestGreen : Color.Gray;

            var fn = it.SubItems.Add(fi.Name + "  (" + (fi.Length / 1024.0 / 1024.0).ToString("0.0") + " MB)");
            fn.ForeColor = Color.DimGray;
            it.ToolTipText = f;
            list.Items.Add(it);
        }
        list.EndUpdate();
        // danh sách của tab "Cắt replay": cùng nội dung, giữ file đang chọn
        string cutSel = cutList.SelectedItems.Count > 0 ? (string)cutList.SelectedItems[0].Tag : null;
        cutList.BeginUpdate();
        cutList.Items.Clear(); cutList.Groups.Clear();
        var cutGroups = new Dictionary<string, ListViewGroup>();
        foreach (ListViewItem src in list.Items)
        {
            var it = (ListViewItem)src.Clone();
            string h = src.Group != null ? src.Group.Header : "";
            ListViewGroup g;
            if (!cutGroups.TryGetValue(h, out g)) { g = new ListViewGroup(h); cutGroups[h] = g; cutList.Groups.Add(g); }
            it.Group = g;
            cutList.Items.Add(it);
            if (cutSel != null && string.Equals((string)it.Tag, cutSel, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
        }
        cutList.EndUpdate();
        UpdateCutInfo();
        if (list.Items.Count == 0) log.AppendText("Không thấy file .replay nào trong " + DemosDir + "\r\n");
    }

    void PickFiles()
    {
        using (var dlg = new OpenFileDialog { Filter = "Replay (*.replay)|*.replay|Tất cả|*.*", Multiselect = true, InitialDirectory = Directory.Exists(DemosDir) ? DemosDir : "" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            extraFiles.AddRange(dlg.FileNames);
            LoadList();
            foreach (ListViewItem it in list.Items) it.Selected = dlg.FileNames.Contains((string)it.Tag, StringComparer.OrdinalIgnoreCase);
        }
    }

    // Đọc nhanh header: thời điểm bắt đầu, độ dài, còn đang quay hay không
    static bool ReadHeader(string path, out DateTime start, out double len, out bool live)
    {
        start = DateTime.MinValue; len = 0; live = false;
        try
        {
            byte[] b = new byte[4096];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) fs.Read(b, 0, b.Length);
            if (BitConverter.ToUInt32(b, 0) != 0x1CA2E27F) return false;
            int p = 8; int ncv = BitConverter.ToInt32(b, p); p += 4 + 20 * ncv;
            len = BitConverter.ToUInt32(b, p) / 1000.0; p += 12;
            int sl = BitConverter.ToInt32(b, p); p += 4 + (sl < 0 ? -sl * 2 : sl);
            live = BitConverter.ToUInt32(b, p) != 0; p += 4;
            start = new DateTime(BitConverter.ToInt64(b, p));
            return true;
        }
        catch { return false; }
    }

    void OnDoubleClick()
    {
        if (list.SelectedItems.Count != 1) return;
        string f = (string)list.SelectedItems[0].Tag;
        string map = Path.Combine(Program.DefaultOutDir(f), "map.html");
        if (File.Exists(map)) { lastOut = Program.DefaultOutDir(f); btnOpenOut.Enabled = btnOpenMap.Enabled = true; OpenMap(lastOut); }
        else Run();
    }

    // ---------- trích xuất ----------
    void Run()
    {
        var files = list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
        if (files.Count == 0) return;
        bool full = chkFull.Checked, openMap = chkOpenMap.Checked;
        // mỗi lần chỉ trích xuất 1 file
        if (files.Count > 1) { ShowError("Mỗi lần chỉ trích xuất được 1 file.\n\nHãy chọn 1 replay rồi bấm Trích xuất."); return; }
        if (runBusy) { ShowError("Đang trích xuất một file khác, hãy đợi xong rồi thử lại."); return; }
        string file = files[0];
        if (watchFile != null && liveRunning)
        {
            // đúng file đang quay: đã được cập nhật liên tục -> chỉ mở bản đồ trực tiếp
            if (string.Equals(file, watchFile, StringComparison.OrdinalIgnoreCase))
            {
                log.AppendText(Path.GetFileName(file) + " đang được theo dõi trực tiếp (luôn cập nhật) - mở bản đồ trực tiếp.\r\n");
                if (openMap) OpenLiveMap();
                return;
            }
            // đang có replay quay trực tiếp -> không cho trích xuất file khác
            ShowError("Đang theo dõi replay đang quay: " + Path.GetFileName(watchFile) + "\n\n"
                + "Không thể trích xuất file khác trong lúc này. Hãy đợi dừng quay, "
                + "hoặc bỏ tick \"Tự theo dõi replay đang quay\" rồi thử lại.");
            return;
        }
        runBusy = true;
        btnRun.Enabled = false; btnRun.Text = "Đang xử lý…";
        UseWaitCursor = true;
        log.Clear();
        var w = new UiWriter(log);
        new Thread(() =>
        {
            string outDir = null; Replay lastRp = null;
            foreach (var f in files)
            {
                w.WriteLine("===== " + Path.GetFileName(f) + " =====");
                var sw = Stopwatch.StartNew();
                try { lastRp = Program.ExtractReplay(f, full, w); outDir = lastRp.OutDir; }
                catch (Exception e) { w.WriteLine("LỖI: " + e.Message); lastRp = null; }
                w.WriteLine("Xong sau {0:0.0}s → {1}\r\n", sw.Elapsed.TotalSeconds, outDir);
            }
            BeginInvoke((Action)(() =>
            {
                UseWaitCursor = false;
                btnRun.Text = "Trích xuất"; btnRun.Enabled = true;
                if (outDir != null && Directory.Exists(outDir))
                {
                    lastOut = outDir; btnOpenOut.Enabled = true;
                    btnOpenMap.Enabled = File.Exists(Path.Combine(outDir, "map.html"));
                    if (openMap && btnOpenMap.Enabled) OpenMap(outDir);
                }
                runBusy = false;
                LoadList();
            }));
        }) { IsBackground = true }.Start();
    }

    void ShowError(string msg) { ShowError(msg, "Không trích xuất được"); }
    void ShowError(string msg, string title)
    {
        log.AppendText("[LỖI] " + msg.Replace("\n\n", " ").Replace("\n", " ") + "\r\n");
        if (Environment.GetEnvironmentVariable("ISLE_TEST") == null) // khi thử nghiệm tự động: không hiện hộp thoại
            MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---------- cắt replay ----------
    // File mới đặt cạnh file gốc (thư mục Demos -> game thấy ngay). File gốc không bị sửa.
    void CutSelected()
    {
        const string T = "Không cắt được";
        var files = cutList.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
        if (files.Count != 1) { ShowError(files.Count == 0 ? "Hãy chọn 1 replay trong danh sách (tab Cắt replay) để cắt." : "Mỗi lần chỉ cắt được 1 file.", T); return; }
        if (cutBusy) { ShowError("Đang cắt một file khác, hãy đợi xong rồi thử lại.", T); return; }
        string file = files[0];
        if (liveRunning && string.Equals(file, watchFile, StringComparison.OrdinalIgnoreCase))
        { ShowError("File này đang được game quay. Hãy dừng quay rồi mới cắt.", T); return; }
        long fromMs, toMs, lenMs;
        try { fromMs = ReplayCut.ParseTime(txtCutFrom.Text); }
        catch { ShowError("Thời gian bắt đầu không hợp lệ: \"" + txtCutFrom.Text + "\"\n\nVí dụ: 52 (phút), 52:30 (phút:giây), 1:05:00 (giờ:phút:giây).", T); return; }
        try { toMs = Math.Max(0, ReplayCut.ParseTime(txtCutTo.Text)); }
        catch { ShowError("Thời gian kết thúc không hợp lệ: \"" + txtCutTo.Text + "\"\n\nVí dụ: 58, 58:30, 1:05:00 - hoặc để trống để cắt tới hết.", T); return; }
        if (fromMs < 0) { ShowError("Hãy nhập thời gian bắt đầu (vd 52 hoặc 52:30).", T); return; }
        try { lenMs = ReplayCut.LengthMs(file); }
        catch (Exception e) { ShowError("Không đọc được file: " + e.Message, T); return; }
        if (fromMs >= lenMs) { ShowError("Thời gian bắt đầu " + ReplayCut.FormatTime(fromMs) + " vượt quá độ dài replay (" + ReplayCut.FormatTime(lenMs) + ").", T); return; }
        if (toMs > 0 && toMs <= fromMs) { ShowError("Thời gian kết thúc phải sau thời gian bắt đầu.", T); return; }
        if (toMs >= lenMs) toMs = 0; // quá độ dài = tới hết
        string outFile = ReplayCut.DefaultCutName(file, fromMs, toMs);
        if (File.Exists(outFile) && MessageBox.Show(this, "Đã có file " + Path.GetFileName(outFile) + ".\n\nGhi đè?", "Cắt replay",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        cutBusy = true; btnCut.Enabled = false; btnCut.Text = "Đang cắt…"; UseWaitCursor = true;
        var w = new UiWriter(log);
        w.WriteLine("===== Cắt {0}: từ {1} đến {2} =====", Path.GetFileName(file), ReplayCut.FormatTime(fromMs), toMs > 0 ? ReplayCut.FormatTime(toMs) : "hết (" + ReplayCut.FormatTime(lenMs) + ")");
        new Thread(() =>
        {
            string err = null, info = null;
            var sw = Stopwatch.StartNew();
            try
            {
                // ghi ra file tạm rồi đổi tên: không để lại file hỏng nếu lỗi giữa chừng
                string tmp = outFile + ".tmp";
                info = ReplayCut.Cut(file, fromMs, toMs, true, tmp, TextWriter.Null);
                if (File.Exists(outFile)) File.Delete(outFile);
                File.Move(tmp, outFile);
            }
            catch (Exception e) { err = e.Message; try { File.Delete(outFile + ".tmp"); } catch { } }
            BeginInvoke((Action)(() =>
            {
                UseWaitCursor = false; btnCut.Text = "Cắt"; btnCut.Enabled = true; cutBusy = false;
                if (err != null) { ShowError("Cắt thất bại: " + err, T); return; }
                w.WriteLine("Xong sau {0:0.0}s: {1}", sw.Elapsed.TotalSeconds, info);
                w.WriteLine("→ {0}", outFile);
                w.WriteLine("Xem trong game: menu Replay → chọn {0}. (Lưu ý: game xoá thư mục Demos khi khởi động lại - chép ra chỗ khác nếu muốn giữ.)", Path.GetFileNameWithoutExtension(outFile));
                LoadList();
                foreach (ListViewItem it in cutList.Items) it.Selected = string.Equals((string)it.Tag, outFile, StringComparison.OrdinalIgnoreCase);
            }));
        }) { IsBackground = true }.Start();
    }

    // chuột phải "Cắt đoạn…" ở tab 1: sang tab Cắt, chọn sẵn file đó
    void GoToCut()
    {
        string f = list.SelectedItems.Count == 1 ? (string)list.SelectedItems[0].Tag : null;
        tabs.SelectedTab = tabCutPage;
        if (f != null) foreach (ListViewItem it in cutList.Items) { it.Selected = string.Equals((string)it.Tag, f, StringComparison.OrdinalIgnoreCase); if (it.Selected) it.EnsureVisible(); }
        txtCutFrom.Focus(); txtCutFrom.SelectAll();
    }

    void UpdateCutInfo()
    {
        if (cutList.SelectedItems.Count != 1) { lblCutInfo.Text = "Chưa chọn replay"; return; }
        var it = cutList.SelectedItems[0];
        string f = (string)it.Tag;
        bool live = liveRunning && string.Equals(f, watchFile, StringComparison.OrdinalIgnoreCase);
        lblCutInfo.Text = Path.GetFileNameWithoutExtension(f) + " · dài " + it.SubItems[1].Text + (live ? " · đang quay (chưa cắt được)" : "");
    }

    // ---------- tự theo dõi replay đang quay ----------
    // Việc theo dõi độc lập với nút Trích xuất: tool tự tìm file game đang ghi (cờ "đang quay" trong header)
    // trong thư mục Demos, và tự chuyển sang file mới khi script AHK cắt file.
    void LiveScan()
    {
        if (!chkLive.Checked || !Directory.Exists(DemosDir)) return;
        string best = null; DateTime bestTime = DateTime.MinValue;
        foreach (var f in Directory.GetFiles(DemosDir, "*.replay"))
        {
            DateTime t = File.GetLastWriteTime(f);
            if ((DateTime.Now - t).TotalSeconds > 15 || t <= bestTime) continue; // game ghi liên tục -> file đang quay luôn mới sửa
            DateTime st; double len; bool live;
            if (ReadHeader(f, out st, out len, out live) && live) { best = f; bestTime = t; }
        }
        if (best == null || string.Equals(best, watchFile, StringComparison.OrdinalIgnoreCase)) return;
        if (watchFile != null) log.AppendText(string.Format("[{0:HH:mm:ss}] Chuyển sang replay mới đang quay: {1}\r\n", DateTime.Now, Path.GetFileName(best)));
        else log.AppendText(string.Format("[{0:HH:mm:ss}] Phát hiện replay đang quay: {1} - tự theo dõi\r\n", DateTime.Now, Path.GetFileName(best)));
        StartWatching(best);
    }

    void StartWatching(string f)
    {
        watchFile = f;
        watchTime = DateTime.MinValue; watchLen = -1;      // buộc cập nhật ngay lần đầu
        DropLiveReader(); liveRpLen = 0;                   // lần đầu đọc cả file, sau đó chỉ đọc phần mới
        dataTimes.Clear(); lastDataMax = -1;
        PerfHeader("bắt đầu theo dõi " + Path.GetFileName(f));
        lastFullReport = DateTime.MinValue;
        liveRunning = true;
        if (server != null) server.SetLive(Path.GetFileName(Program.DefaultOutDir(f)));
        lblLive.ForeColor = Color.Firebrick;
        LoadList();
        LiveTick();
    }

    // ---------- bật/tắt ghi log chỗ chậm (IsleReplayConfig.bat mục 5 -> settings.txt: perflog=1) ----------
    DateTime perfSettingTime = DateTime.MinValue;
    void RefreshPerfSetting()
    {
        DateTime wt;
        try { wt = File.Exists(SettingsFile) ? File.GetLastWriteTimeUtc(SettingsFile) : DateTime.MinValue; } catch { return; }
        if (wt == perfSettingTime || log == null) return; // chỉ đọc lại khi file đổi
        perfSettingTime = wt;
        bool on = LoadSetting("perflog", "0") == "1";
        if (on == PerfLog.Enabled) return;
        PerfLog.SetEnabled(on);
        log.AppendText(on
            ? string.Format("[{0:HH:mm:ss}] Ghi log chỗ chậm: BẬT → {1} (tắt bằng IsleReplayConfig.bat, mục 5)\r\n", DateTime.Now, PerfLog.FilePath)
            : string.Format("[{0:HH:mm:ss}] Ghi log chỗ chậm: TẮT\r\n", DateTime.Now));
        if (on) PerfHeader(watchFile != null ? "đang theo dõi " + Path.GetFileName(watchFile) : "bật ghi log (chưa có replay đang quay)");
    }
    // dòng thông tin máy ở đầu mỗi lần theo dõi: giúp đọc log của người khác
    void PerfHeader(string what)
    {
        double cs;
        PerfLog.Write(null, string.Format("[tool] {0} · bản {1} · Engine.ini ChunkUploadDelayInSeconds = {2} · {3} nhân CPU · {4}",
            what, Updater.CurrentVersion() ?? "tự build", ReplayChunkSetting(out cs), Environment.ProcessorCount, Environment.OSVersion.VersionString));
    }

    // ---------- nhịp game ghi replay ----------
    // Bản đồ chỉ cập nhật nhanh bằng nhịp game ghi dữ liệu ra file (localReplay.ChunkUploadDelayInSeconds trong Engine.ini).
    // Máy chưa chỉnh (hoặc chỉnh 1 s) -> dữ liệu đến mỗi 1–2 s: đo nhịp thật và báo cách sửa (1 lần mỗi lần mở tool).
    readonly List<DateTime> dataTimes = new List<DateTime>(); double lastDataMax = -1; bool slowWarned;
    // trả về khoảng cách trung vị (giây) giữa các lần có dữ liệu mới, -1 nếu chưa đủ mẫu
    double TrackWriteInterval(double maxTime)
    {
        if (maxTime > lastDataMax)
        {
            if (lastDataMax >= 0)
            {
                // game lâu không ghi dữ liệu mới (game lag / đang loading / ổ đĩa chậm)
                if (dataTimes.Count > 0)
                {
                    double gap = (DateTime.Now - dataTimes[dataTimes.Count - 1]).TotalSeconds;
                    if (gap > 3)
                    {
                        double sv; string cs = ReplayChunkSetting(out sv);
                        bool cfgFast = sv > 0 && sv <= 0.25;
                        PerfLog.Write("game-gap", string.Format("[game] {0:0.0} s không có dữ liệu replay mới ({1})", gap,
                            cfgFast ? "game lag / loading / ổ đĩa chậm?"
                                    : "Engine.ini ChunkUploadDelayInSeconds = " + cs + " → game chỉ ghi replay theo khối ~15 s; chạy IsleReplayConfig.bat chọn 2"), gap);
                        // Game ghi theo khối: mỗi khối tool có thể đọc qua vài lần sát nhau -> trung vị bị kéo xuống,
                        // nên đếm thêm số lần trống dài: >= 3 lần > 5 s trong 90 s = ghi thưa thật (loading chỉ 1 lần)
                        if (gap > 5)
                        {
                            bigGaps.Add(DateTime.Now);
                            bigGaps.RemoveAll(t => (DateTime.Now - t).TotalSeconds > 90);
                            if (bigGaps.Count >= 3) WarnSlowWrites(gap);
                        }
                    }
                }
                dataTimes.Add(DateTime.Now); if (dataTimes.Count > 21) dataTimes.RemoveAt(0);
            }
            lastDataMax = maxTime;
        }
        if (dataTimes.Count < 11) return -1;
        var gaps = new List<double>();
        for (int i = 1; i < dataTimes.Count; i++) gaps.Add((dataTimes[i] - dataTimes[i - 1]).TotalSeconds);
        gaps.Sort();
        double med = gaps[gaps.Count / 2];
        if (med > 0.6) WarnSlowWrites(med);
        return med;
    }
    readonly List<DateTime> bigGaps = new List<DateTime>();
    // báo 1 lần mỗi lần mở tool: game ghi replay thưa + cách sửa
    void WarnSlowWrites(double every)
    {
        if (slowWarned) return;
        slowWarned = true;
        double setVal;
        string cur = ReplayChunkSetting(out setVal);
        string fix = setVal > 0 && setVal <= 0.25
            // đã đặt nhanh mà game vẫn ghi thưa: game chưa đọc lại cấu hình (đang mở từ trước) hoặc máy/ổ đĩa quá tải
            ? "Engine.ini đã đặt nhanh nhưng game chưa áp dụng: tắt hẳn game rồi mở lại (nếu vẫn vậy: máy/ổ đĩa đang quá tải)."
            : "Muốn cập nhật ~0,15 s: tắt game, chạy IsleReplayConfig.bat, chọn 2 (Nhanh), rồi mở lại game.";
        log.AppendText(string.Format("[{0:HH:mm:ss}] ⚠ Game đang ghi replay mỗi ~{1:0.0} s (Engine.ini: ChunkUploadDelayInSeconds = {2}) → bản đồ/radar chỉ có dữ liệu mới theo nhịp này. {3}\r\n",
            DateTime.Now, every, cur, fix));
        PerfLog.Write(null, string.Format("[game] ghi replay mỗi ~{0:0.00} s (Engine.ini: {1}) – {2}", every, cur, fix));
    }
    // giá trị localReplay.ChunkUploadDelayInSeconds trong Engine.ini của game (val = -1 nếu chưa đặt / không đọc được)
    static string ReplayChunkSetting(out double val)
    {
        val = -1;
        try
        {
            string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TheIsle\Saved\Config\WindowsClient\Engine.ini");
            if (!File.Exists(ini)) return "chưa có Engine.ini";
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(ini), @"localReplay\.ChunkUploadDelayInSeconds\s*=\s*([0-9.]+)");
            if (!m.Success) return "chưa đặt (mặc định của game)";
            double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val);
            return m.Groups[1].Value + " s";
        }
        catch { return "không đọc được"; }
    }

    // Bỏ bộ đọc trực tiếp hiện tại và đóng file nó đang giữ mở. Luồng đọc đang chạy thì nó tự đóng khi xong (LiveTick).
    void DropLiveReader()
    {
        if (liveRp != null && !liveBusy) liveRp.CloseFile();
        liveRp = null;
    }

    void StopWatching(string reason)
    {
        if (watchFile != null) log.AppendText(string.Format("[{0:HH:mm:ss}] Ngừng theo dõi {1} ({2})\r\n", DateTime.Now, Path.GetFileName(watchFile), reason));
        watchFile = null; DropLiveReader(); liveRunning = false;
        lblLive.ForeColor = Color.Gray;
        lblLive.Text = "Chưa thấy replay nào đang quay";
    }

    // Bản đồ trực tiếp: địa chỉ cố định luôn hiện replay đang quay (tự chuyển khi sang file mới)
    // ---------- tab "Cập nhật" (tải bản Latest từ GitHub Releases) ----------
    void BuildUpdateTab()
    {
        var lay = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 7 };
        for (int i = 0; i < 6; i++) lay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tabUpdatePage.Controls.Add(lay);
        string cur = Updater.CurrentVersion();
        lblVerCur = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4),
            Text = "Bản đang dùng: " + (cur ?? "tự build trên máy (không có version.txt)") };
        lblVerNew = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 8), Text = "Bản mới nhất: chưa kiểm tra" };
        lay.Controls.Add(lblVerCur); lay.Controls.Add(lblVerNew);
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        btnCheckUpd = MakeButton("Kiểm tra bản mới", (s, e) => CheckUpdate(true));
        btnDoUpd = MakeButton("Cập nhật ngay", (s, e) => DoUpdate());
        btnDoUpd.Font = new Font(Font, FontStyle.Bold); btnDoUpd.Enabled = false;
        var btnPage = MakeButton("Mở trang Releases", (s, e) => { try { Process.Start(new ProcessStartInfo(Updater.PageUrl) { UseShellExecute = true }); } catch { } });
        row.Controls.Add(btnCheckUpd); row.Controls.Add(btnDoUpd); row.Controls.Add(btnPage);
        lay.Controls.Add(row);
        chkAutoUpd = new CheckBox { Text = "Tự kiểm tra bản mới khi mở tool", AutoSize = true, Checked = LoadSetting("autoupdatecheck", "1") == "1", Margin = new Padding(0, 8, 0, 0) };
        chkAutoUpd.CheckedChanged += (s, e) => SaveSetting("autoupdatecheck", chkAutoUpd.Checked ? "1" : "0");
        lay.Controls.Add(chkAutoUpd);
        lblUpdStatus = new Label { AutoSize = true, ForeColor = Color.Gray, MaximumSize = new Size(760, 0), Margin = new Padding(0, 8, 0, 4),
            Text = "Cập nhật: tải " + Updater.ZipUrl + " rồi thay exe, map_template.html, maps\\, file .bat và tự mở lại tool. Giữ nguyên cài đặt lớp (map_layers.txt); chỗ sỏi (gastro_learned.txt) được gộp, không mất." };
        lay.Controls.Add(lblUpdStatus);
        lay.Controls.Add(new Label { Text = "Có gì mới:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        txtNotes = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        lay.Controls.Add(txtNotes);
        Updater.CleanupOld();
        Shown += (s, e) => { if (chkAutoUpd.Checked) CheckUpdate(false); };
    }

    // manual = bấm nút (báo cả khi lỗi / đã mới nhất); tự kiểm tra lúc mở thì chỉ báo khi có bản mới
    void CheckUpdate(bool manual)
    {
        if (updBusy) return;
        updBusy = true; btnCheckUpd.Enabled = false; lblVerNew.Text = "Bản mới nhất: đang kiểm tra…";
        new Thread(() =>
        {
            Updater.Info info = null; string err = null;
            try { info = Updater.CheckLatest(); } catch (Exception e) { err = e.Message; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    updBusy = false; btnCheckUpd.Enabled = true;
                    if (err != null) { lblVerNew.Text = "Bản mới nhất: không kiểm tra được (" + err + ")"; if (manual) log.AppendText("[Cập nhật] Lỗi: " + err + "\r\n"); return; }
                    latestInfo = info;
                    string curV = Updater.CurrentVersion();
                    int curN = Updater.BuildNumber(curV), newN = Updater.BuildNumber(info.Tag);
                    bool newer = curV != null && newN > curN; // bản tự build (không có version.txt): không so được, không báo
                    string when = info.Date.Length >= 10 ? info.Date.Substring(0, 10) : info.Date;
                    lblVerNew.Text = "Bản mới nhất: " + info.Tag + (when != "" ? "  (" + when + ")" : "") + (newer ? "  –  CÓ BẢN MỚI" : curV == null ? "" : "  –  bạn đang dùng bản mới nhất");
                    lblVerNew.ForeColor = newer ? Color.ForestGreen : SystemColors.ControlText;
                    txtNotes.Text = (info.Notes ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                    btnDoUpd.Enabled = true; // vẫn cho cài lại khi đã mới nhất / bản tự build
                    tabUpdatePage.Text = newer ? "Cập nhật (có bản mới)" : "Cập nhật";
                    if (newer) log.AppendText("[Cập nhật] Có bản mới " + info.Tag + " – vào tab \"Cập nhật\" để cài.\r\n");
                }));
            }
            catch { }
        }) { IsBackground = true }.Start();
    }

    void DoUpdate()
    {
        if (updBusy) return;
        const string T = "Cập nhật";
        if (Directory.Exists(Path.Combine(Program.ExeDir, "src")) &&
            MessageBox.Show(this, "Thư mục này có mã nguồn (src\\). Cập nhật sẽ ghi đè DinoReplayReader.exe, map_template.html và các file .bat bằng bản trên GitHub.\n\nTiếp tục?", T,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        if (MessageBox.Show(this, "Tải và cài " + (latestInfo != null ? latestInfo.Tag : "bản mới nhất") + "? Tool sẽ tự tắt rồi mở lại (overlay đang mở sẽ đóng).", T,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        updBusy = true; btnDoUpd.Enabled = btnCheckUpd.Enabled = false; UseWaitCursor = true;
        new Thread(() =>
        {
            string exe = null, err = null;
            try { exe = Updater.DownloadAndApply(msg => { try { BeginInvoke((Action)(() => { lblUpdStatus.Text = msg; lblUpdStatus.ForeColor = SystemColors.ControlText; })); } catch { } }); }
            catch (Exception e) { err = e.Message; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    UseWaitCursor = false; updBusy = false; btnDoUpd.Enabled = btnCheckUpd.Enabled = true;
                    if (err != null) { lblUpdStatus.Text = "Cập nhật thất bại: " + err; lblUpdStatus.ForeColor = Color.Firebrick; ShowError("Cập nhật thất bại: " + err, T); return; }
                    lblUpdStatus.Text = "Đã cài xong – đang mở lại tool…";
                    try { Process.Start(new ProcessStartInfo(exe, "-afterupdate " + Process.GetCurrentProcess().Id) { UseShellExecute = false, WorkingDirectory = Program.ExeDir }); }
                    catch (Exception e) { ShowError("Đã cài xong nhưng không mở lại được tool: " + e.Message + "\nHãy tự mở DinoReplayReader.exe.", T); }
                    Close();
                }));
            }
            catch { }
        }) { IsBackground = true }.Start();
    }

    static string SettingsFile { get { return Path.Combine(Program.ExeDir, "settings.txt"); } }
    static string LoadSetting(string key, string def)
    {
        try { foreach (var l in File.ReadAllLines(SettingsFile)) { int k = l.IndexOf('='); if (k > 0 && l.Substring(0, k).Trim() == key) return l.Substring(k + 1).Trim(); } } catch { }
        return def;
    }
    static void SaveSetting(string key, string val)
    {
        try
        {
            var lines = File.Exists(SettingsFile) ? File.ReadAllLines(SettingsFile).Where(l => !l.StartsWith(key + "=")).ToList() : new List<string>();
            lines.Add(key + "=" + val); File.WriteAllLines(SettingsFile, lines);
        }
        catch { }
    }

    // ---------- tab "Bản đồ & overlay" ----------
    void BuildMapTab()
    {
        var lay = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        tabMapPage.Controls.Add(lay);

        lay.Controls.Add(new Label { Text = "Overlay radar (luôn nổi trên game – game cần chạy Borderless/Windowed)", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) });
        var rowOv = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        btnOverlay = MakeButton("Mở overlay radar", (s, e) => ToggleOverlay());
        // bản đồ đầy đủ trên trình duyệt (giống trang mở ra sau khi trích xuất)
        var btnMapWeb = MakeButton("Mở bản đồ trên web", (s, e) => OpenWebMap());
        rowOv.Controls.Add(btnOverlay); rowOv.Controls.Add(btnMapWeb);
        lay.Controls.Add(rowOv);
        var rowOv2 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        chkOverlayLock = new CheckBox { Text = "Khoá overlay (bấm xuyên, ẩn thanh tiêu đề) · Ctrl+F9", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
        chkOverlayLock.CheckedChanged += (s, e) => { overlay.Locked = chkOverlayLock.Checked; overlay.Tick(); if (vitalsOv != null) vitalsOv.Locked = chkOverlayLock.Checked; if (compassOv != null) compassOv.Locked = chkOverlayLock.Checked; if (feedOv != null) feedOv.Locked = chkOverlayLock.Checked; };
        numOverlayOpacity = new NumericUpDown { Minimum = 20, Maximum = 100, Increment = 5, Value = 85, Width = 52, Margin = new Padding(4, 3, 0, 0) };
        numOverlayOpacity.ValueChanged += (s, e) => { overlay.Opacity = (int)numOverlayOpacity.Value; overlay.Tick(); if (vitalsOv != null) vitalsOv.SetOpacity((int)numOverlayOpacity.Value); if (compassOv != null) compassOv.SetOpacity((int)numOverlayOpacity.Value); if (feedOv != null) feedOv.SetOpacity((int)numOverlayOpacity.Value); };
        rowOv2.Controls.Add(chkOverlayLock);
        rowOv2.Controls.Add(new Label { Text = "Độ đậm", AutoSize = true, Margin = new Padding(16, 7, 0, 0) });
        rowOv2.Controls.Add(numOverlayOpacity);
        rowOv2.Controls.Add(new Label { Text = "%", AutoSize = true, Margin = new Padding(2, 7, 0, 0) });
        lay.Controls.Add(rowOv2);
        lay.Controls.Add(new Label { Text = "Ctrl+F10 = phóng to, Ctrl+F11 = thu nhỏ (radar và bản đồ đang mở, không cần mở khoá). Mở khoá (Ctrl+F9) để kéo, đổi cỡ.", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 4, 0, 0) });
        chkCompass = new CheckBox { Text = "Hiện thanh la bàn (giữa phía trên màn hình, khi đang quay)", AutoSize = true, Checked = LoadSetting("compass", "1") == "1", Margin = new Padding(0, 10, 0, 0) };
        chkCompass.CheckedChanged += (s, e) => { SaveSetting("compass", chkCompass.Checked ? "1" : "0"); compassOn = chkCompass.Checked; if (compassOv != null) compassOv.Wanted = chkCompass.Checked; };
        lay.Controls.Add(chkCompass);
        lay.Controls.Add(new Label { Text = "Giữa thanh = hướng camera của bạn; chấm = khủng long người chơi (đỏ < 50 m, cam < 150 m), ◀ ▶ ở mép = ngoài tầm, quay về phía đó. Quay chuột tới khi chấm vào giữa là đang nhìn thẳng con đó. Vùng sáng giữa thanh ≈ khung màn hình. Trễ theo replay ~0,2 s. Khoá / độ đậm chung với radar.", AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.Gray, Margin = new Padding(0, 2, 0, 0) });

        lay.Controls.Add(new Label { Text = "Hiện trên bản đồ / overlay", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 18, 0, 4) });
        var saved = LoadLayers();
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        foreach (var d in LayerDefs)
        {
            bool on; if (!saved.TryGetValue(d[0], out on)) on = d[0] != "pvngastro"; // PVN sỏi mặc định tắt
            var cb = new CheckBox { Text = d[1], AutoSize = true, Checked = on, Margin = new Padding(0, 2, 28, 2) };
            cb.CheckedChanged += (s, e) => { SaveLayers(); PushLayers(); };
            layerChecks[d[0]] = cb; grid.Controls.Add(cb);
        }
        lay.Controls.Add(grid);
        var rowAll = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        rowAll.Controls.Add(MakeButton("Bật tất cả", (s, e) => SetAllLayers(true)));
        rowAll.Controls.Add(MakeButton("Tắt tất cả", (s, e) => SetAllLayers(false)));
        lay.Controls.Add(rowAll);
        lay.Controls.Add(new Label { Text = "Áp dụng ngay cho overlay, radar web và bản đồ đang mở qua tool (http://127.0.0.1…). Trên trang bản đồ vẫn tự bật/tắt riêng được; đổi ở đây sẽ ghi đè.", AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.Gray, Margin = new Padding(0, 6, 0, 0) });
        lay.Controls.Add(new Panel { Width = 1, Height = 16, Margin = Padding.Empty });
    }

    // ---------- tab "PVN trực tiếp": chỉ số của bạn, combat, bản đồ nhiệt (đều dùng tài khoản PVN) ----------
    void BuildPvnTab()
    {
        var lay = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        tabPvnPage.Controls.Add(lay);

        // ---- chỉ số khủng long của bạn (WebSocket PVN Overlay) ----
        lay.Controls.Add(new Label { Text = "Chỉ số khủng long của bạn (máu, blood, thể lực, đói, khát, chảy máu, gãy xương – từ PVN, ~4 lần/giây)", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 18, 0, 4) });
        chkPvnLive = new CheckBox { Text = "Kết nối PVN (dùng tài khoản PVN Overlay đã đăng nhập trên máy này)", AutoSize = true, Checked = LoadSetting("pvnlive", "1") == "1", Margin = new Padding(0, 2, 0, 0) };
        chkPvnLive.CheckedChanged += (s, e) =>
        {
            SaveSetting("pvnlive", chkPvnLive.Checked ? "1" : "0");
            if (chkPvnLive.Checked) { pvnKickLogged = false; pvnLive.Start(); pvnCombat.Start(PvnToken.SteamId()); pvnFriends.Start(); } else { pvnLive.Stop(); pvnCombat.Stop(); pvnFriends.Stop(); }
            UpdateVitalsWanted(); PvnLiveTick();
        };
        lay.Controls.Add(chkPvnLive);
        lblPvnLive = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 4, 0, 0) };
        lay.Controls.Add(lblPvnLive);
        chkVitalsOv = new CheckBox { Text = "Hiện overlay chỉ số (cửa sổ nhỏ riêng, luôn nổi trên game)", AutoSize = true, Checked = LoadSetting("vitalsov", "1") == "1", Margin = new Padding(0, 4, 0, 0) };
        chkVitalsOv.CheckedChanged += (s, e) => { SaveSetting("vitalsov", chkVitalsOv.Checked ? "1" : "0"); UpdateVitalsWanted(); };
        lay.Controls.Add(chkVitalsOv);
        chkVitalsNum = new CheckBox { Text = "Hiện số thật (vd Máu 31/50) thay vì %", AutoSize = true, Checked = LoadSetting("vitalsnum", "0") == "1", Margin = new Padding(0, 2, 0, 0) };
        chkVitalsNum.CheckedChanged += (s, e) => { SaveSetting("vitalsnum", chkVitalsNum.Checked ? "1" : "0"); if (vitalsOv != null) vitalsOv.ShowNumbers = chkVitalsNum.Checked; };
        lay.Controls.Add(chkVitalsNum);
        chkCombat = new CheckBox { Text = "Khi đánh nhau: hiện máu đối thủ (combat)", AutoSize = true, Checked = LoadSetting("vitalscombat", "1") == "1", Margin = new Padding(0, 2, 0, 0) };
        chkCombat.CheckedChanged += (s, e) => SaveSetting("vitalscombat", chkCombat.Checked ? "1" : "0");
        lay.Controls.Add(chkCombat);
        chkFeed = new CheckBox { Text = "Thông báo từng đòn: hiện sát thương bạn gây / nhận + kỹ năng, mất dần sau 3 s", AutoSize = true, Checked = LoadSetting("combatfeed", "1") == "1", Margin = new Padding(0, 6, 0, 0) };
        chkFeed.CheckedChanged += (s, e) => { SaveSetting("combatfeed", chkFeed.Checked ? "1" : "0"); if (feedOv != null) feedOv.Wanted = chkFeed.Checked; };
        lay.Controls.Add(chkFeed);
        var rowFeed = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        rowFeed.Controls.Add(MakeButton("Thử thông báo đòn", (s, e) => { if (feedOv != null) { feedOv.Wanted = true; feedOv.Demo(); } }));
        rowFeed.Controls.Add(new Label { Text = "Đổi vị trí: mở khoá (Ctrl+F9) → kéo khung nét đứt, lăn chuột đổi cỡ → khoá lại.", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(8, 8, 0, 0) });
        lay.Controls.Add(rowFeed);
        // nút đổi nhịp gọi combatlog: 2 s <-> 0,5 s (0,5 s = mượt hơn nhưng gọi server nhiều gấp 4) + nút quét khoá PVN
        pvnCombat.PollMs = LoadSetting("combatms", "2000") == "500" ? 500 : 2000;
        var rowCombat = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        btnCombatRate = MakeButton(CombatRateText(), (s, e) =>
        {
            pvnCombat.PollMs = pvnCombat.PollMs == 500 ? 2000 : 500;
            SaveSetting("combatms", pvnCombat.PollMs.ToString());
            btnCombatRate.Text = CombatRateText();
        });
        btnScanKey = MakeButton("Quét khoá PVN", (s, e) => ScanPvnKey());
        rowCombat.Controls.Add(btnCombatRate); rowCombat.Controls.Add(btnScanKey);
        lay.Controls.Add(rowCombat);
        lblPvnKey = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 4, 0, 0) };
        lay.Controls.Add(lblPvnKey);
        lay.Controls.Add(new Label { Text = "Combat chỉ thấy con bạn đã đánh trúng (máu lấy từ combatlog, trễ ~nhịp gọi); con chỉ đánh bạn thì biết tên nhưng không biết máu. Cần khoá PVN: bấm \"Quét khoá PVN\" (tự tìm trong bản PVN Overlay đã cài, chỉ cần quét 1 lần).", AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.Gray, Margin = new Padding(0, 2, 0, 0) });
        lay.Controls.Add(new Label { Text = "Overlay chỉ số khoá / mở khoá chung với radar (Ctrl+F9), độ đậm chung ô ở tab \"Bản đồ & overlay\". Mỗi tài khoản PVN chỉ được 1 kết nối: mở PVN Overlay thì tool bị đá ra và tự dừng (tắt PVN Overlay rồi tick lại ô kết nối).", AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.Gray, Margin = new Padding(0, 4, 0, 0) });
        UpdateCombatEnabled();

        // ---- bản đồ nhiệt PVN ----
        lay.Controls.Add(new Label { Text = "Bản đồ nhiệt PVN (nơi đông người chơi trên server, cập nhật mỗi 5 giây)", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 18, 0, 4) });
        lay.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 0, 0, 4), Text =
            "Bước 1: Bấm \"Mở trang PVN\" và đăng nhập Steam.\r\n" +
            "Bước 2: Nhấn F12 → tab Network → gõ heatmap vào ô Filter (chưa thấy thì F5) → nhấp phải dòng heatmap → Copy → Copy as cURL (cmd).\r\n" +
            "Bước 3: Dán vào ô dưới (Ctrl+V) → bấm \"Trích xuất cookie\"." });
        lay.Controls.Add(MakeButton("Mở trang PVN", (s, e) => OpenPvnPage()));
        // nhiều dòng: lệnh cURL dài nhiều dòng (ô 1 dòng sẽ cắt mất dòng -b có cookie)
        txtPvn = new TextBox { Width = 600, Height = 54, Multiline = true, ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 6, 0, 0) };
        txtPvn.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; ExtractPvnCookie(); } };
        lay.Controls.Add(txtPvn);
        var rowPvn = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        btnPvnExtract = MakeButton("Trích xuất cookie", (s, e) => ExtractPvnCookie());
        rowPvn.Controls.Add(btnPvnExtract);
        rowPvn.Controls.Add(MakeButton("Xoá cookie", (s, e) =>
        {
            if (pvnToken == null) { PvnNotify("Chưa có cookie nào để xoá.", false); return; }
            PvnHeat.Clear(); pvnToken = null; PushHeatEmpty(); UpdatePvnStatus(null);
            log.AppendText("Đã xoá cookie PVN.\r\n");
        }));
        lay.Controls.Add(rowPvn);
        lblPvn = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 6, 0, 0) };
        lay.Controls.Add(lblPvn);
        lay.Controls.Add(new Label { Text = "Cookie dùng được khoảng 30 ngày, lưu mã hoá theo tài khoản Windows (pvn_cookie.dat). Đừng gửi cookie / đoạn cURL cho ai: ai có nó là vào được tài khoản PVN của bạn.", AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.Gray, Margin = new Padding(0, 4, 0, 0) });

        // khoảng trống cuối: FlowLayoutPanel AutoScroll không tính Padding/Margin dưới -> dòng cuối bị che khi cuộn hết
        lay.Controls.Add(new Panel { Width = 1, Height = 24, Margin = Padding.Empty });

        pvnToken = PvnHeat.Load();
        PvnSign.Rejected = OnPvnKeyRejected;
        UpdatePvnStatus(null);
    }

    // ---------- bản đồ nhiệt PVN ----------
    Button btnPvnExtract;

    void OpenPvnPage()
    {
        try { Process.Start(new ProcessStartInfo(PvnHeat.PageUrl) { UseShellExecute = true }); } catch { }
    }

    // hộp thoại thân thiện (không phải [LỖI]): ok = thành công, còn lại = nhắc làm lại

    // Popup cảnh báo (không phải lỗi nhập của người dùng); ISLE_TEST -> chỉ ghi log
    void PvnWarn(string title, string msg)
    {
        log.AppendText("[PVN] " + msg.Replace("\n\n", " ").Replace("\n", " ") + "\r\n");
        if (Environment.GetEnvironmentVariable("ISLE_TEST") == null)
            MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // Cookie hết hạn (theo exp trong JWT) -> xoá file + báo, TRƯỚC khi gọi API. true = vừa xoá.
    bool ExpireCookieIfNeeded()
    {
        if (pvnToken == null) return false;
        var info = PvnHeat.Decode(pvnToken);
        if (info == null || info.Expires == DateTime.MaxValue || info.Expires > DateTime.Now) return false;
        DropCookie("Cookie PVN đã hết hạn (từ " + info.Expires.ToString("dd/MM/yyyy HH:mm") + ").");
        return true;
    }

    void DropCookie(string reason)
    {
        PvnHeat.Clear(); pvnToken = null; pvnAuthFail = false; PushHeatEmpty(); UpdatePvnStatus(null);
        BeginInvoke((Action)(() => PvnWarn("Cookie PVN không dùng được",
            reason + "\n\nTool đã xoá cookie cũ. Bản đồ nhiệt sẽ tắt cho tới khi bạn lấy cookie mới:\ntab \"Bản đồ & overlay\" → \"1. Mở trang PVN\" → làm lại 3 bước.")));
    }

    // PVN từ chối khoá HMAC / token (2 lần liên tiếp) -> pvn_hmac.txt đã bị xoá (PvnSign.Invalidate) -> báo
    void OnPvnKeyRejected()
    {
        try
        {
            BeginInvoke((Action)(() =>
            {
                UpdateCombatEnabled();
                PvnWarn("Khoá PVN không còn dùng được",
                    "PVN từ chối yêu cầu (401/403) hai lần liên tiếp: khoá HMAC cũ (hoặc token PVN Overlay) đã hết hiệu lực.\n\nTool đã xoá pvn_hmac.txt, nên máu đối thủ / thông báo đòn / bạn bè (chấm xanh) tạm tắt.\n\nCách lấy lại: mở PVN Overlay một lần (đăng nhập, cập nhật bản mới nếu có), tắt nó, rồi bấm \"Quét khoá PVN\" ở tab \"PVN trực tiếp\".");
            }));
        }
        catch { }
    }
    void PvnNotify(string msg, bool ok)
    {
        log.AppendText("Bản đồ nhiệt PVN: " + msg.Replace("\n\n", " ").Replace("\n", " ") + "\r\n");
        if (Environment.GetEnvironmentVariable("ISLE_TEST") == null)
            MessageBox.Show(this, msg, ok ? "Đã lấy được cookie PVN" : "Chưa lấy được cookie PVN", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // Lấy cookie từ chữ trong ô (cURL cmd/bash, hoặc chỉ giá trị cookie) -> thử gọi PVN -> lưu + báo
    void ExtractPvnCookie()
    {
        string input = txtPvn.Text;
        if (input.Trim().Length == 0)
        {
            PvnNotify("Ô đang trống.\n\nTrên trang PVN: F12 → Network → nhấp phải dòng heatmap → Copy → Copy as cURL (cmd), rồi dán vào ô (Ctrl+V) và bấm lại \"Trích xuất cookie\".", false);
            return;
        }
        string tok = PvnHeat.ExtractToken(input);
        if (tok == null)
        {
            PvnNotify("Trong đoạn vừa dán không có cookie đăng nhập (islepilot_player).\n\nThử lại:\n• Kiểm tra đã đăng nhập Steam trên trang PVN chưa (góc trên có tên bạn).\n• Copy đúng dòng heatmap trong tab Network, chọn \"Copy as cURL (cmd)\".", false);
            return;
        }
        var info = PvnHeat.Decode(tok);
        string who = info != null && info.Name != null ? info.Name : "của bạn";
        if (info != null && info.Expires <= DateTime.Now)
        {
            PvnNotify("Cookie của tài khoản " + who + " đã hết hạn (" + info.Expires.ToString("dd/MM/yyyy") + ").\n\nTải lại trang PVN (F5), đăng nhập lại rồi copy cURL mới.", false);
            return;
        }

        // kiểm tra với PVN trước khi lưu (luồng nền, ~1 s)
        btnPvnExtract.Enabled = false; btnPvnExtract.Text = "Đang kiểm tra…";
        new Thread(() =>
        {
            string json, err; int cells;
            var res = PvnHeat.Fetch(tok, out json, out cells, out err);
            try
            {
                BeginInvoke((Action)(() =>
                {
                    btnPvnExtract.Enabled = true; btnPvnExtract.Text = "Trích xuất cookie";
                    if (res == PvnHeat.Result.AuthFailed)
                    {
                        PvnNotify("PVN không nhận cookie này – có thể bạn đã đăng xuất, hoặc đây là cookie cũ.\n\nTải lại trang PVN (F5), đăng nhập rồi copy cURL mới.", false);
                        return;
                    }
                    try { PvnHeat.Save(tok); }
                    catch (Exception e) { PvnNotify("Không lưu được cookie vào thư mục tool: " + e.Message, false); return; }
                    txtPvn.Text = ""; // không để cookie nằm trên màn hình
                    pvnToken = tok; pvnAuthFail = false; heatNext = DateTime.MinValue;
                    int days = info != null && info.Expires != DateTime.MaxValue ? (int)Math.Floor((info.Expires - DateTime.Now).TotalDays) : -1;
                    string exp = days >= 0 ? "Cookie dùng được đến " + info.Expires.ToString("dd/MM/yyyy") + " (còn " + days + " ngày) – hết hạn thì làm lại 3 bước.\n" : "";
                    CheckBox cb; bool layerOn = layerChecks.TryGetValue("heat", out cb) && cb.Checked;
                    string msg;
                    if (res == PvnHeat.Result.Ok)
                    {
                        server.PushHeat(json); heatPushedEmpty = false; heatNext = DateTime.Now.AddSeconds(5);
                        UpdatePvnStatus("lần cuối " + DateTime.Now.ToString("HH:mm:ss") + ": " + cells + " vùng");
                        msg = "Xong! Đã lấy cookie của tài khoản " + who + " (PVN đang có " + cells + " vùng đông người).\n\n" + exp +
                              "Bản đồ nhiệt sẽ hiện trên bản đồ web và overlay radar đang mở, tự cập nhật mỗi 5 giây.";
                    }
                    else
                    {
                        UpdatePvnStatus("chưa kết nối được PVN: " + err);
                        msg = "Đã lưu cookie của tài khoản " + who + ", nhưng lúc này chưa kết nối được PVN (" + err + ").\nTool sẽ tự thử lại.\n\n" + exp;
                    }
                    if (!layerOn) msg += "\n\nLưu ý: lớp \"Bản đồ nhiệt PVN\" đang tắt – tick nó trong danh sách lớp phía trên để xem.";
                    PvnNotify(msg, true);
                }));
            }
            catch { }
        }) { IsBackground = true }.Start();
    }

    void UpdatePvnStatus(string fetchMsg)
    {
        if (lblPvn == null) return;
        if (pvnToken == null) { lblPvn.ForeColor = Color.Gray; lblPvn.Text = "Chưa có cookie PVN → chưa có bản đồ nhiệt."; return; }
        var info = PvnHeat.Decode(pvnToken);
        string who = info != null && info.Name != null ? info.Name : "?";
        string exp = info != null && info.Expires != DateTime.MaxValue ? " · hết hạn " + info.Expires.ToString("dd/MM/yyyy") : "";
        bool expired = info != null && info.Expires <= DateTime.Now;
        if (pvnAuthFail || expired)
        {
            lblPvn.ForeColor = Color.Firebrick;
            lblPvn.Text = "Cookie PVN của " + who + " " + (expired ? "đã hết hạn" : "không còn dùng được") + " → lấy lại cookie (nút 1).";
            return;
        }
        lblPvn.ForeColor = Color.DarkGreen;
        lblPvn.Text = "Đã có cookie PVN: " + who + exp + (fetchMsg != null ? " · " + fetchMsg : "");
    }

    void PushHeatEmpty() { if (server != null && !heatPushedEmpty) { server.PushHeat("null"); heatPushedEmpty = true; } }

    // gọi mỗi giây (liveTimer): có cookie + lớp đang bật + có trang đang mở -> gọi API mỗi 5 s (luồng nền)
    void HeatTick()
    {
        if (ExpireCookieIfNeeded()) return; // hết hạn: xoá file + popup, không gọi API
        if (server == null || pvnToken == null || pvnAuthFail || heatBusy) return;
        CheckBox cb;
        if (!layerChecks.TryGetValue("heat", out cb) || !cb.Checked || server.ClientCount == 0) return;
        if (DateTime.Now < heatNext) return;
        heatBusy = true; heatNext = DateTime.Now.AddSeconds(5);
        string tok = pvnToken;
        new Thread(() =>
        {
            string json, err; int cells;
            var res = PvnHeat.Fetch(tok, out json, out cells, out err);
            try
            {
                BeginInvoke((Action)(() =>
                {
                    heatBusy = false;
                    if (tok != pvnToken) return; // cookie vừa đổi / bị xoá
                    if (res == PvnHeat.Result.Ok)
                    {
                        server.PushHeat(json); heatPushedEmpty = false;
                        UpdatePvnStatus("lần cuối " + DateTime.Now.ToString("HH:mm:ss") + ": " + cells + " vùng");
                    }
                    else if (res == PvnHeat.Result.AuthFailed)
                    {
                        DropCookie("PVN không nhận cookie nữa (" + err + ") – có thể đã hết hạn hoặc bạn đã đăng xuất.");
                        log.AppendText("Bản đồ nhiệt PVN: " + err + " → lấy lại cookie ở tab \"Bản đồ & overlay\".\r\n");
                    }
                    else
                    {
                        heatNext = DateTime.Now.AddSeconds(15); // lỗi mạng: thử lại chậm hơn
                        UpdatePvnStatus("lỗi " + DateTime.Now.ToString("HH:mm:ss") + ": " + err);
                    }
                }));
            }
            catch { heatBusy = false; }
        }) { IsBackground = true }.Start();
    }

    // mỗi giây (liveTimer): hiện trạng thái kết nối PVN
    void PvnLiveTick()
    {
        if (lblPvnLive == null) return;
        if (!chkPvnLive.Checked) { lblPvnLive.ForeColor = Color.Gray; lblPvnLive.Text = "Đang tắt."; return; }
        int fr = pvnLive.Frames, rate = fr - pvnLastFrames; pvnLastFrames = fr;
        var v = pvnLive.Last;
        if (pvnLive.Ok && v != null && (DateTime.Now - v.At).TotalSeconds < 3)
        {
            string who = !v.HasDino ? "chưa có khủng long (đang ở menu?)" : v.Species + " " + Math.Round(v.Growth * 100) + " %";
            lblPvnLive.ForeColor = Color.DarkGreen; lblPvnLive.Text = "Đang nhận: " + who + " · " + rate + " lần/s";
            return;
        }
        lblPvnLive.ForeColor = pvnLive.Kicked || !pvnLive.Running ? Color.Firebrick : Color.Gray;
        lblPvnLive.Text = pvnLive.Running || pvnLive.Kicked ? pvnLive.State : "Đã dừng.";
        if (pvnLive.Kicked && !pvnKickLogged) { pvnKickLogged = true; log.AppendText("PVN: " + pvnLive.State + "\r\n"); }
    }

    // Đang quay -> bản đồ trực tiếp; không -> bản đồ của file đang chọn ở tab 1 (nếu đã trích xuất) hoặc lần trích xuất gần nhất
    void OpenWebMap()
    {
        if (liveRunning && watchFile != null) { OpenLiveMap(); return; }
        string dir = null;
        if (list.SelectedItems.Count == 1)
        {
            string d = Program.DefaultOutDir((string)list.SelectedItems[0].Tag);
            if (File.Exists(Path.Combine(d, "map.html"))) dir = d;
        }
        if (dir == null && lastOut != null && File.Exists(Path.Combine(lastOut, "map.html"))) dir = lastOut;
        if (dir != null) { OpenMap(dir); return; }
        if (watchFile != null) { OpenLiveMap(); return; }
        ShowError("Chưa có bản đồ nào: không có replay đang quay, và file đang chọn ở tab \"Đọc replay\" chưa được trích xuất.", "Mở bản đồ");
    }

    bool layersBulk;
    void SetAllLayers(bool on)
    {
        layersBulk = true;
        foreach (var cb in layerChecks.Values) cb.Checked = on;
        layersBulk = false;
        SaveLayers(); PushLayers();
    }

    static string LayersFile { get { return Path.Combine(Program.ExeDir, "map_layers.txt"); } }
    static Dictionary<string, bool> LoadLayers()
    {
        var r = new Dictionary<string, bool>();
        try { foreach (var l in File.ReadAllLines(LayersFile)) { var p = l.Split('='); if (p.Length == 2) r[p[0].Trim()] = p[1].Trim() == "1"; } } catch { }
        return r;
    }
    void SaveLayers()
    {
        if (layersBulk) return;
        try { File.WriteAllLines(LayersFile, layerChecks.Select(kv => kv.Key + "=" + (kv.Value.Checked ? "1" : "0"))); } catch { }
    }
    void PushLayers()
    {
        if (layersBulk || server == null) return;
        server.PushLayers("{" + string.Join(",", layerChecks.Select(kv => "\"" + kv.Key + "\":" + (kv.Value.Checked ? "true" : "false"))) + "}");
    }

    // ---------- overlay radar ----------
    void ToggleOverlay()
    {
        if (ovWanted) { overlay.Close(); ovWanted = false; UpdateOverlayButton(); return; }
        if (server == null) { MessageBox.Show(this, "Không mở được máy chủ bản đồ nên không chạy được overlay.", Text); return; }
        overlay.Locked = chkOverlayLock.Checked; overlay.Opacity = (int)numOverlayOpacity.Value;
        string err = overlay.Open(server.LiveUrl + "?overlay");
        if (err != null) { MessageBox.Show(this, err, Text); return; }
        ovWanted = true; ovSeen = false; ovOpenedAt = DateTime.Now;
        UpdateOverlayButton();
        log.AppendText("Overlay radar: game cần chạy Borderless/Windowed. Ctrl+F9 = khoá/mở khoá (mở khoá để kéo, đổi cỡ, lăn chuột đổi bán kính).\r\n");
    }

    void DrainCombatHits()
    {
        var hits = pvnCombat.TakeHits();
        if (hits.Length == 0 || feedOv == null || !chkFeed.Checked) return;
        foreach (var h in hits) feedOv.Add(h);
    }

    void OverlayTick()
    {
        if (feedOv != null) feedOv.Active = ovWanted || (liveRp != null && liveRunning);
        DrainCombatHits();
        if (!ovWanted) return;
        overlay.Tick();
        if (overlay.IsOpen) { ovSeen = true; return; }
        // người dùng đã đóng cửa sổ overlay (hoặc Edge không mở được sau 20 s)
        if (ovSeen || (DateTime.Now - ovOpenedAt).TotalSeconds > 20) { ovWanted = false; UpdateOverlayButton(); }
    }

    void UpdateOverlayButton() { btnOverlay.Text = ovWanted ? "Đóng overlay radar" : "Mở overlay radar"; }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, HOTKEY_OVERLAY, 0x0002 /*MOD_CONTROL*/ | 0x4000 /*MOD_NOREPEAT*/, (uint)Keys.F9);
        // zoom radar/bản đồ mà không phải mở khoá overlay (giữ phím được: không có MOD_NOREPEAT)
        RegisterHotKey(Handle, HOTKEY_ZOOM_IN, 0x0002, (uint)Keys.F10);
        RegisterHotKey(Handle, HOTKEY_ZOOM_OUT, 0x0002, (uint)Keys.F11);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnregisterHotKey(Handle, HOTKEY_OVERLAY);
        UnregisterHotKey(Handle, HOTKEY_ZOOM_IN); UnregisterHotKey(Handle, HOTKEY_ZOOM_OUT);
        if (ovWanted) overlay.Close(); // overlay không còn dữ liệu khi tool tắt
        pvnLive.Stop(); pvnCombat.Stop(); pvnFriends.Stop();
        if (vitalsOv != null) vitalsOv.Close();
        if (compassOv != null) compassOv.Close();
        if (feedOv != null) feedOv.Close();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 /*WM_HOTKEY*/)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_OVERLAY) { chkOverlayLock.Checked = !chkOverlayLock.Checked; return; }
            if (id == HOTKEY_ZOOM_IN || id == HOTKEY_ZOOM_OUT) { if (server != null) server.PushEvent("zoom", id == HOTKEY_ZOOM_IN ? "in" : "out"); return; }
        }
        base.WndProc(ref m);
    }

    void OpenLiveMap()
    {
        if (server != null) { try { Process.Start(new ProcessStartInfo(server.LiveUrl) { UseShellExecute = true }); } catch { } return; }
        if (watchFile != null) OpenMap(Program.DefaultOutDir(watchFile));
        else MessageBox.Show(this, "Chưa thấy replay nào đang quay.", Text);
    }

    // Game ghi thêm vào file đang theo dõi -> đọc phần mới và cập nhật bản đồ
    void LiveTick()
    {
        if (!chkLive.Checked || liveBusy || watchFile == null) return;
        FileInfo fi;
        try { fi = new FileInfo(watchFile); } catch { return; }
        if (!fi.Exists)
        {
            StopWatching("đã dừng quay / script đã đổi tên file");
            LoadList(); LiveScan();
            return;
        }
        // kích thước thật qua file đang giữ mở (thông tin thư mục có thể chậm cập nhật khi game đang ghi)
        long held = liveRp != null ? liveRp.HeldLength() : -1;
        long curLen = Math.Max(fi.Length, held);
        if (fi.LastWriteTime == watchTime && curLen == watchLen) return;
        // nghỉ ít nhất bằng thời gian lần xử lý trước -> không chiếm quá ~50% một nhân CPU khi đang chơi
        if (DateTime.Now < nextLiveAllowed) return;
        liveBusy = true;
        string f = watchFile; bool full = chkFull.Checked, alertOn = chkAlert.Checked;
        // ghi nhận trạng thái file TRƯỚC khi đọc: nếu game ghi thêm trong lúc xử lý thì lần sau vẫn bắt được
        DateTime seenTime = fi.LastWriteTime; long seenLen = curLen;
        var rp = liveRp;
        new Thread(() =>
        {
            string msg; bool stillLive = true; double len = 0; List<DinoAlert> alerts = null; CompassData compass = null;
            var sw = Stopwatch.StartNew();
            // thời gian từng bước (ms) -> ghi perf_log.txt khi một lần cập nhật chậm
            double tLoad = 0, tParse = 0, tAlert = 0, tMap = 0, tPush = 0; bool fresh = false, report = false; long bytesNew = 0;
            try
            {
                // file bị ghi lại từ đầu (ngắn đi) hoặc lần đầu -> bộ đọc mới
                if (rp == null || seenLen < liveRpLen) { if (rp != null) rp.CloseFile(); rp = Program.NewReplay(f, full, TextWriter.Null); fresh = true; }
                bytesNew = seenLen - (fresh ? 0 : liveRpLen);
                double m0 = sw.Elapsed.TotalMilliseconds;
                rp.LoadFile(f);
                double m1 = sw.Elapsed.TotalMilliseconds; tLoad = m1 - m0;
                rp.ParseMore();
                double m2 = sw.Elapsed.TotalMilliseconds; tParse = m2 - m1;
                liveRpLen = seenLen;
                stillLive = rp.IsLive; len = rp.LengthSec;
                // chỉ báo khi game còn đang quay (dữ liệu là "bây giờ")
                if (alertOn && stillLive) alerts = rp.CheckNewDinos(alertPresent, DateTime.Now, TimeSpan.FromSeconds(30));
                if (compassOn && stillLive) compass = rp.CompassSnapshot(1000);
                double m3 = sw.Elapsed.TotalMilliseconds; tAlert = m3 - m2;
                // bản đồ: mỗi lần; báo cáo + csv: mỗi 15 giây và khi dừng quay
                if (!stillLive || (DateTime.Now - lastFullReport).TotalSeconds >= 15) { rp.Finish(); lastFullReport = DateTime.Now; report = true; }
                else
                {
                    bool writeFiles = server == null || (DateTime.Now - lastFileWrite).TotalSeconds >= 2;
                    rp.WriteLiveMap(writeFiles);
                    if (writeFiles) lastFileWrite = DateTime.Now;
                }
                double m4 = sw.Elapsed.TotalMilliseconds; tMap = m4 - m3;
                if (server != null && rp.LastMapJson != null) server.Push(Path.GetFileName(rp.OutDir), rp.LastMapJson);
                tPush = sw.Elapsed.TotalMilliseconds - m4;
                msg = null;
            }
            catch (Exception e) { msg = "lỗi: " + e.Message; if (rp != null) rp.CloseFile(); rp = null; }
            double took = sw.Elapsed.TotalSeconds;
            if (fresh)
                PerfLog.Write(null, string.Format("[tool] đọc lần đầu {0}: {1:0.0} MB trong {2:0} ms", Path.GetFileName(f), seenLen / 1048576.0, took * 1000));
            else if (took * 1000 > 150 || (report && took * 1000 > 400))
                PerfLog.Write("tool-slow", string.Format("[tool] cập nhật chậm {0:0} ms (đọc file {1:0} · giải mã {2:0} [{3} KB mới] · báo khủng long {4:0} · {5} {6:0} · gửi trang {7:0}) – {8}",
                    took * 1000, tLoad, tParse, bytesNew / 1024, tAlert, report ? "báo cáo đầy đủ" : "dữ liệu bản đồ", tMap, tPush,
                    tLoad > 50 ? "đọc file chậm (ổ đĩa / phần mềm diệt virus?)" : tPush > 50 ? "gửi tới trang chậm (trình duyệt bận?)" : tParse > 80 ? "nhiều dữ liệu mới một lúc" : "máy đang bận (CPU)"), took * 1000);
            BeginInvoke((Action)(() =>
            {
                if (watchFile == f) { liveRp = rp; watchTime = seenTime; watchLen = seenLen; liveRunning = stillLive; }
                else if (rp != null) rp.CloseFile(); // đã chuyển file / ngừng theo dõi trong lúc đọc -> bỏ bộ đọc này
                if (msg != null) log.AppendText(string.Format("[{0:HH:mm:ss}] {1}: {2}\r\n", DateTime.Now, Path.GetFileName(f), msg));
                if (alerts != null && alerts.Count > 0 && chkAlert.Checked) ShowAlerts(alerts);
                if (compass != null && watchFile == f && compassOv != null) compassOv.SetData(compass);
                double every = watchFile == f && rp != null && stillLive ? TrackWriteInterval(rp.MaxTime) : -1;
                lblLive.Text = string.Format("{0} {1} · {2} · cập nhật {3:HH:mm:ss} ({4:0} ms){5}",
                    stillLive ? "● Đang quay" : "■ Đã dừng", Path.GetFileName(f), TimeSpan.FromSeconds(len).ToString(@"m\:ss"), DateTime.Now, took * 1000,
                    every > 0 ? string.Format(" · game ghi mỗi {0:0.00} s", every) : "");
                nextLiveAllowed = DateTime.Now.AddSeconds(took);
                liveBusy = false;
                // game có thể đã ghi thêm trong lúc đang xử lý -> kiểm tra lại ngay
                BeginInvoke((Action)LiveTick);
            }));
        }) { IsBackground = true }.Start();
    }
    DateTime nextLiveAllowed = DateTime.MinValue;

    void ShowAlerts(List<DinoAlert> alerts)
    {
        foreach (var a in alerts) log.AppendText(string.Format("[{0:HH:mm:ss}] ⚠ Vào vùng: {1} – {2}\r\n", DateTime.Now, a.Title, a.Detail));
        // nhiều con cùng lúc (vd mới bật tool giữa đám đông) -> gộp 1 ô
        if (alerts.Count > 3)
            AlertToast.Show(new DinoAlert { Title = alerts.Count + " khủng long người chơi trong vùng", Detail = string.Join(", ", alerts.Select(a => a.Title.Split('·')[0].Trim()).ToArray()) });
        else foreach (var a in alerts) AlertToast.Show(a);
        if (chkAlertSound.Checked) AlertToast.Beep();
    }

    // ---------- xoá kết quả ----------
    static string OutputRoot { get { return Path.Combine(Program.ExeDir, "output"); } }

    // chỉ chấp nhận thư mục con trực tiếp của output\ (không bao giờ xoá replay hay thư mục khác)
    static bool IsSafeOutputDir(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            string root = Path.GetFullPath(OutputRoot).TrimEnd('\\');
            return Directory.Exists(full) && string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    List<string> SelectedOutputDirs()
    {
        return list.SelectedItems.Cast<ListViewItem>().Select(i => Program.DefaultOutDir((string)i.Tag)).Where(IsSafeOutputDir).ToList();
    }

    void DeleteSelectedOutputs()
    {
        var dirs = SelectedOutputDirs();
        if (dirs.Count == 0) { MessageBox.Show(this, "Các file đang chọn chưa có kết quả nào để xoá.", Text); return; }
        string names = string.Join("\n", dirs.Take(8).Select(d => "• " + Path.GetFileName(d))) + (dirs.Count > 8 ? "\n…" : "");
        if (MessageBox.Show(this, "Xoá kết quả trích xuất của " + dirs.Count + " replay (chuyển vào Thùng rác)?\n\n" + names + "\n\nFile replay gốc KHÔNG bị xoá.",
            "Xoá kết quả", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        DeleteDirs(dirs);
    }

    void DeleteAllOutputs()
    {
        var dirs = Directory.Exists(OutputRoot) ? Directory.GetDirectories(OutputRoot).Where(IsSafeOutputDir).ToList() : new List<string>();
        if (dirs.Count == 0) { MessageBox.Show(this, "Thư mục output đang trống.", Text); return; }
        if (MessageBox.Show(this, "Xoá TẤT CẢ " + dirs.Count + " kết quả trong thư mục output (chuyển vào Thùng rác)?\n\nFile replay gốc KHÔNG bị xoá.",
            "Xoá tất cả kết quả", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        DeleteDirs(dirs);
    }

    void DeleteDirs(List<string> dirs)
    {
        int ok = 0;
        var blockers = new List<string>();
        foreach (var d in dirs)
        {
            if (RecycleSilently(d)) { ok++; log.AppendText("Đã xoá (vào Thùng rác): " + Path.GetFileName(d) + "\r\n"); continue; }
            var procs = ProcessesUsing(d);
            log.AppendText("Không xoá được " + Path.GetFileName(d) + (procs.Count > 0 ? " - đang mở bởi: " + string.Join(", ", procs) : " - có file đang được mở") + "\r\n");
            blockers.AddRange(procs);
        }
        if (lastOut != null && !Directory.Exists(lastOut)) { lastOut = null; btnOpenOut.Enabled = btnOpenMap.Enabled = false; }
        log.AppendText(string.Format("Xoá xong {0}/{1} thư mục.\r\n", ok, dirs.Count));
        LoadList();
        if (ok < dirs.Count)
            MessageBox.Show(this, (dirs.Count - ok) + " thư mục chưa xoá được vì đang được chương trình khác mở"
                + (blockers.Count > 0 ? ":\n\n" + string.Join("\n", blockers.Distinct().Select(b => "• " + b)) : ".")
                + "\n\nHãy đóng các chương trình đó (vd. Notepad đang mở summary.txt, Excel đang mở file .csv, cửa sổ Explorer đang ở trong thư mục) rồi bấm xoá lại.",
                "Chưa xoá được", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Chuyển thư mục vào Thùng rác mà không hiện hộp thoại lỗi của Windows
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
        public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    static bool RecycleSilently(string dir)
    {
        var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = dir + "\0\0", fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT) };
        int r = SHFileOperation(ref op);
        return r == 0 && !op.fAnyOperationsAborted && !Directory.Exists(dir);
    }

    // Tìm chương trình đang mở file/thư mục này (dựa vào dòng lệnh của tiến trình)
    static List<string> ProcessesUsing(string dir)
    {
        var res = new List<string>();
        try
        {
            string d = Path.GetFullPath(dir).TrimEnd('\\');
            using (var s = new System.Management.ManagementObjectSearcher("SELECT Name, ProcessId, CommandLine FROM Win32_Process"))
                foreach (System.Management.ManagementObject p in s.Get())
                {
                    string cmd = p["CommandLine"] as string;
                    if (cmd == null || cmd.IndexOf(d, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(cmd, System.Text.RegularExpressions.Regex.Escape(d) + @"\\?([^""]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    string file = m.Success && m.Groups[1].Value.Trim() != "" ? m.Groups[1].Value.Trim() : "";
                    res.Add(p["Name"] + (file != "" ? " (" + file + ")" : ""));
                }
        }
        catch { }
        return res;
    }

    // Mở bản đồ: qua máy chủ của tool (cập nhật tức thì) nếu đang chạy, không thì mở thẳng file
    void OpenMap(string outDir)
    {
        if (outDir == null) return;
        string file = Path.Combine(outDir, "map.html");
        if (!File.Exists(file)) return;
        if (server != null) { try { Process.Start(new ProcessStartInfo(server.MapUrl(outDir)) { UseShellExecute = true }); return; } catch { } }
        OpenPath(file);
    }

    static void OpenPath(string p)
    {
        if (string.IsNullOrEmpty(p) || (!File.Exists(p) && !Directory.Exists(p))) return;
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
    }
}
