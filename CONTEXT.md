# Dino Replay Reader + script AHK – Bối cảnh dự án

> Đọc file này trước khi làm tiếp. Viết cho cả người dùng và AI (Claude Code).
> Cập nhật lần cuối: 2026-11-04.

> **Tên app (2026-11-04)**: người dùng muốn bỏ chữ "Isle" khỏi tên app → exe/process **`DinoReplayReader.exe`**, cửa sổ "Dino Replay Reader", overlay "Dino Radar Overlay" (hồ sơ Edge `%LOCALAPPDATA%\DinoReplayReader\EdgeOverlay`, tự chuyển từ thư mục cũ `IsleReplayReader`), báo cáo "BÁO CÁO REPLAY DINO". **Chỉ đổi tên app**: GIỮ tên thư mục (`THE_ISLE`, `IsleReplayReader`), script AHK, tiền tố file replay `TheIsle_`, và mọi tên của game (`%LOCALAPPDATA%\TheIsle\…`, `TheIsleClient`, tiêu đề cửa sổ game "The Isle", `TimeOfDayIsle`). Biến môi trường thử nghiệm `ISLE_*`, khoá localStorage `isle*` giữ nguyên (nội bộ).

## 1. Tổng quan

Hai phần, dùng cho game **The Isle (Evrima)**, bản đồ **Gateway**, Unreal Engine **5.6**:

| Phần | File | Việc |
|---|---|---|
| Script AHK | `AutoHotkey\the isle_auto_E_record.ahk` | Tự giữ E (ăn sỏi), tự quay replay bằng F2 và cắt file mỗi 10 phút, đổi tên file replay |
| Tool đọc replay | thư mục `IsleReplayReader\` (thư mục chứa file này) | Đọc file `.replay` của game → báo cáo, CSV, **bản đồ HTML** (cập nhật trực tiếp khi đang quay) |

Người dùng: Steam/in-game **skydark3004**, chơi T-Rex. Giao tiếp **tiếng Việt**.

## 2. Script AHK – `the isle_auto_E_record.ahk` (AutoHotkey v2)

- Cửa sổ nhỏ "Isle Helper" (nền tối, luôn trên cùng, nút ✕ Thoát đỏ, link "Mở thư mục replay").
- **Ctrl+3** – bật/tắt **ăn sỏi**: giữ `E` 5 s, mỗi 40 s (`HOLD_MS`, `INTERVAL_MS`).
- **Ctrl+2** – bật/tắt **tự quay**: ấn `F2` (scan code `sc03C`) để quay; mỗi `REC_CYCLE_MS` = 10 phút ấn F2 tắt, chờ 2 s, ấn F2 quay lại.
  - Chỉ ấn phím khi cửa sổ game đang active **liên tục ≥ 1,5 s** (`REC_SETTLE_MS`); cửa sổ của chính script bị loại (tiêu đề có chữ "The Isle" từng gây lỗi).
  - Hotkey đợi thả Ctrl (`KeyWait`) để game không nhận Ctrl+E / Ctrl+F2.
  - Script **chỉ tự nhớ** trạng thái quay (không đọc được từ game); nếu lệch, tắt quay trong game rồi bật/tắt lại.
- Sau khi tắt quay: đổi tên `demoN.replay` (file mới sửa nhất kể từ lúc bắt đầu) thành
  `TheIsle_<yyyy-MM-dd>_<HH-mm bắt đầu>_den_<HH-mm kết thúc>.replay`,
  rồi **lùi timestamp trong header đi 30 phút** (`REC_TS_SHIFT_MIN`, theo yêu cầu người dùng; 0 = tắt).
  ⇒ Giờ trong header của file đã đổi tên **sai 30 phút**; giờ thật nằm ở **tên file** / thời gian sửa file.
- Thư mục replay của game: `%LOCALAPPDATA%\TheIsle\Saved\Demos`.

## 3. Tool `IsleReplayReader`

### Cách dùng
- Nhấp đúp `DinoReplayReader.exe` → cửa sổ (tiêu đề hiện địa chỉ máy chủ bản đồ, mặc định `http://127.0.0.1:47321`, nếu bận thì 47322…47340).
- Danh sách replay trong Demos (mới nhất trên, nhóm theo ngày): *Thời gian quay · Độ dài · Trạng thái · Tên file*.
  Thời gian quay = `LastWriteTime − độ dài` (đúng cả với file đã đổi tên / header bị lùi).
- **Trích xuất**: mỗi lần **1 file** (chọn nhiều → báo lỗi). Đang có replay quay trực tiếp mà trích xuất file khác → **báo lỗi** (theo yêu cầu). Chọn đúng file đang quay → mở bản đồ trực tiếp.
- **Tự theo dõi replay đang quay** (mặc định bật): tự tìm file có cờ `bIsLive` trong header, mới sửa ≤ 15 s; tự chuyển sang `demoN+1` khi script cắt file.
- **Mở bản đồ trực tiếp** → `http://127.0.0.1:<port>/live/map.html` (luôn là replay đang quay, tự chuyển file).
- Xoá kết quả (chọn nhiều được, phím Delete, chuột phải) → vào **Thùng rác**; chỉ xoá thư mục con của `output\`, không bao giờ xoá replay. Thư mục bị chương trình khác giữ (vd Notepad mở `summary.txt`) → báo tên chương trình.
- Dòng lệnh vẫn chạy: `DinoReplayReader.exe <file.replay> [-full] [-out <thư mục>]`; `DocNhanh.bat` / `DocDayDu.bat` (kéo thả file).
- **Báo khủng long người chơi mới vào vùng** (2026-11-04, chỉ khi đang quay / tự theo dõi): tick "Báo khi có khủng long người chơi mới vào vùng" + "kèm âm thanh", nút "Thử".
  - Điều kiện: actor khủng long có `SteamId`, không phải bạn (`Controller`), chưa chết, kênh còn mở; báo khi SteamId đó **không có mặt trong 30 s trước** (không báo lại khi script cắt sang file mới / lấp ló rìa tầm nhìn). AI dino không báo.
  - Nội dung: loài · tên người chơi; giới tính, % trưởng thành · khoảng cách · hướng theo radar (trên-phải…) + la bàn · so với hướng bạn nhìn.
  - Hiển thị: ô góc trên-phải màn hình (`AlertToast`: TOPMOST + NOACTIVATE + bấm xuyên, **không lấy focus game/AHK**, tự ẩn 9 s, tối đa 5, >3 con cùng lúc gộp 1 ô) + âm 2 nốt tự tạo + dòng log. Có thể không hiện trên game fullscreen độc quyền / khi Focus Assist chặn – âm thanh vẫn kêu.
  - Bản đồ (`map_template.html`, `checkNewPlayers`): cùng logic theo tên người chơi, ô thông báo góc trái (bấm để ghim chỉ đường) + âm WebAudio (trình duyệt chỉ phát sau khi đã bấm vào trang 1 lần). Lần nạp đầu chỉ ghi nhận, không báo.
  - Thử: `DinoReplayReader.exe <file> -alerts <bytes>` (đọc dần như đang quay, in thông báo theo giờ replay). demo5: 18 thông báo/1 giờ.
- **Overlay radar** (2026-11-04): nút "Mở overlay radar" → Edge `--app=/live/map.html?overlay` (hồ sơ riêng `%LOCALAPPDATA%\IsleReplayReader\EdgeOverlay`), tool (`Overlay.cs`, timer 250 ms) tìm cửa sổ theo tiêu đề "Isle Radar Overlay" + class `Chrome_WidgetWin_1`, giữ TOPMOST, `WS_EX_LAYERED` (ô "Độ đậm" %). Giữ TOPMOST: **chỉ gọi `SetWindowPos` khi mất cờ TOPMOST hoặc có cửa sổ khác (không phải của tool, không cloaked) đè lên** (`CoveredByOther`) – trước đó gọi mỗi 250 ms làm khựng (người dùng báo "giật giật", 2026-11-04).
  - **Khoá** (mặc định, **Ctrl+F9** bật/tắt – `RegisterHotKey`): `WS_EX_TRANSPARENT|NOACTIVATE` (bấm xuyên, không lấy focus) + `SetWindowRgn` cắt theo cửa sổ con `Chrome_RenderWidgetHostHWND` (Edge tự vẽ thanh tiêu đề trong vùng client nên bỏ `WS_CAPTION` không có tác dụng; đã kiểm chứng phần bị cắt trong suốt thật). Mở khoá: kéo/đổi cỡ, lăn chuột đổi bán kính radar (nhớ trong localStorage).
  - "Mở overlay radar" luôn **đóng cửa sổ radar cũ rồi mở trang mới** (2026-11-04: lỗi "Đang tải bản đồ…" mãi do cửa sổ cũ trỏ tới cổng máy chủ của lần chạy tool trước đã chết). Trang: ảnh lỗi → thử lại sau 2 s ("Không tải được ảnh bản đồ – đang thử lại…"); SSE `onerror` → chữ đỏ "Mất kết nối với tool" (`sseDown`). Tải ảnh qua máy chủ trên máy rất nhanh (base.webp 6,7 MB ≈ 0,01 s, curl).
  - Vị trí/cỡ do tool nhớ (`overlay_placement.txt` trong hồ sơ) – Edge không nhớ cho cửa sổ `--app`. Tắt tool → đóng overlay. Game phải Borderless/Windowed.
  - Chế độ `?overlay` trong `map_template.html`: không thanh bên, luôn lấy bạn làm tâm (Bắc ở trên), bỏ lưới + vệt, vòng 50…1000 m (chữ ở đỉnh vòng), không tự báo/kêu (tool đã báo). `translate="no"` để Edge không hỏi dịch trang.
- **Sỏi (gastro)** (2026-11-04):
  - **Dữ liệu điểm sỏi của PVN KHÔNG khớp game** (người dùng đứng trên bãi sỏi, game hiện "Forage for gastroliths", điểm PVN gần nhất 152 m; `data_gastro` của người dùng (257 điểm) thử 8 cách đổi trục, gần nhất 143 m). PVN giữ ở lớp "Sỏi theo PVN (không chính xác)", **mặc định tắt** (`GATEWAY.gastro`, 221 điểm từ mảng `oN` của bundle PVN, u,v → toạ độ game).
  - **Sỏi tự ghi** (`Gastro.cs`, `GastroStore`): `AbilitySystemComponent.RepAnimMontageInfo` được giải mã tên animation (`MontageName`: dò packed NetGUID trỏ tới asset `/Animations/`, ở bit 37 → "montage:AM_…"). Animation chứa `Gastrolith` (vd `AM_Tyrannosaurus_Juvenile_Carry_Grab_Gastrolith`) = đang nhặt sỏi → `LastPos` của actor đó lưu vào `gastro_learned.txt` cạnh exe (gộp điểm < 15 m, dùng chung mọi replay; cả khủng long khác trong tầm nhìn). Bản đồ nhận qua `data.js` → `"gastro":[[x,y],…]`; lớp "Sỏi đã nhặt (tự ghi)", viên gần nhất vòng vàng + "Sỏi N m", thanh bên "Sỏi gần nhất". Kiểm chứng: fast1 nhặt lúc 430 s/458 s → điểm 73216,390128 (vị trí thật 73216,390270). `-simulate 8192` vẫn giống hệt.
  - PVN còn nhiều nhóm khác trong `oN` (SaltRock, Fish, Crab, Turtle, Boar, Deer, trái cây, nấm…) – độ chính xác chưa kiểm chứng.
- **Bố cục tab** (2026-11-05, chia lại cho đỡ rối): 4 tab — **Đọc replay & bản đồ** · **Bản đồ & overlay** (overlay radar, khoá/độ đậm, lớp hiển thị) · **PVN trực tiếp** (`BuildPvnTab`: kết nối PVN + overlay chỉ số + combat + nút "Quét khoá PVN", bản đồ nhiệt PVN) · **Cắt replay**. **Bỏ tab "Cập nhật"** (repo sẽ private, link release công khai không dùng được; `BuildUpdateTab`/`Updater.cs` còn trong mã nhưng KHÔNG gọi/không thêm tab). Mọi thứ liên quan PVN (cần tài khoản PVN) gom vào tab "PVN trực tiếp".
- **Tab "Bản đồ & overlay" + bật/tắt lớp** (2026-11-04): có nút "Mở overlay radar" và "Mở bản đồ trên web" (`OpenWebMap`: bản đồ đầy đủ như sau khi trích xuất – đang quay → /live, không → file đang chọn ở tab 1 nếu đã trích xuất, hoặc lần trích xuất gần nhất; nút "radar trên web" cũ đã bỏ vì người dùng không cần), khoá + độ đậm, và ô tick các lớp (`LayerDefs`: player, ghost, corpse, fish, meat, poi, mud, gastro, pvngastro, map; lưu `map_layers.txt` cạnh exe; PVN sỏi mặc định tắt). Đổi → `LiveServer.PushLayers` gửi SSE `event: layers` (JSON {khoá: bool}) tới **mọi** trang đang mở; client mới kết nối nhận ngay. Trang (`applyLayers`, `LAYER_INPUT`, `pendingLayers`) đặt giá trị + cập nhật ô tick; ô trên trang vẫn đổi riêng được (tool đổi sau sẽ ghi đè). Mở bằng file:// thì chỉ dùng ô trên trang. Bản đồ HTML có thêm ô "Mất dấu (vị trí cuối)" (`SHOW.ghost`).
- **Bản đồ nhiệt PVN** (2026-11-05, `PvnHeat.cs`): API `https://pvn.islepilot.eu/api/p/pvn/map/heatmap` → `{"ok":true,"cells":[{u,v,intensity}],"radius":30}` (lưới 72×72 trên ảnh PVN, u,v = góc ô; ~10 ô). **Chỉ cần cookie `islepilot_player`** (JWT HS256, payload steamId/personaName/exp, hạn 30 ngày); không cần `cf_clearance`/header trình duyệt, Cloudflare không chặn. Không cookie / sai → 200 `{"ok":false}`.
  - Lấy cookie (cách chính, người dùng chọn): tab "Bản đồ & overlay" → nút "1. Mở trang PVN" (trình duyệt mặc định) → F12 → Network → request heatmap → **Copy as cURL (cmd)** → dán vào ô (`Multiline` – ô 1 dòng cắt mất cURL nhiều dòng) → nút **"Trích xuất cookie"** (hoặc Enter) → `ExtractPvnCookie`: tìm `islepilot_player=eyJ…` (`ExtractToken`, cũng nhận riêng giá trị cookie), kiểm tra hạn, **gọi thử API** (luồng nền) rồi mới lưu, xoá ô, báo bằng hộp thoại thân thiện (`PvnNotify`: tên tài khoản, số vùng, hạn + số ngày còn, nhắc nếu lớp đang tắt; lỗi = hướng dẫn làm lại, không phải "[LỖI]"). Người dùng không muốn nút "Lưu" / tự lưu khi dán / tự đọc clipboard (đã bỏ). **Cookie là HttpOnly** (đã thử script dán console 2026-11-05: `document.cookie`/`cookieStore` không thấy) → không script nào đọc được, đã bỏ script. Không đọc file cookie của trình duyệt (Chrome app-bound encryption; giống infostealer).
  - Lưu `pvn_cookie.dat` cạnh exe, mã hoá DPAPI CurrentUser (`.gitignore`, không có trong gói). Cần `/r:System.Security.dll` (đã thêm vào workflow).
  - `HeatTick` (mỗi tick `liveTimer`): có cookie + lớp "heat" bật + có trang SSE đang mở (`LiveServer.ClientCount`) → gọi API mỗi 5 s trên luồng nền (lỗi mạng → 15 s); `ok:false`/401/403 → dừng, chữ đỏ "lấy lại cookie", đẩy `null` xoá lớp. `LiveServer.PushHeat` → SSE `event: heatmap` (trang mở sau nhận bản mới nhất).
  - Trang: lớp "Bản đồ nhiệt PVN" (`gw.heat`, `drawHeat` trên lớp nền `#bg`, đốm đỏ/cam `lighter` tại tâm ô, bán kính 0,85 ô ≈ 133 m, đậm theo intensity; có cả trên overlay). Ý nghĩa intensity (mật độ người chơi?) chưa kiểm chứng.
- **Chỉ số khủng long của bạn từ PVN** (2026-11-05, `PvnLive.cs`): tool tự nối WebSocket `wss://islepilot.eu/ows` của PVN Overlay (~4 frame `t:"live"`/s, CHỈ khủng long của mình: health, blood, stamina, hunger, thirst, oxygen + max…, debuffs `["bleeding"]`, bones fractured/recoverPct, glassBonesSec, sick, bacteria, growth, position). Tìm hiểu ở repo `D:\SOURCE_CODE\integrate-game` (CONTEXT.md, `debug-tools\isle-ows.cjs`).
  - Token: `%APPDATA%\pvn-isle-overlay\pvn-isle-overlay.settings.json` → `overlayToken` = `enc1:` + base64 của Electron safeStorage (`v10` + nonce 12 + bản mã + tag 16, AES-256-GCM), khoá = `Local State` → `os_crypt.encrypted_key` (`DPAPI` + DPAPI CurrentUser). AES-GCM qua `bcrypt.dll` (P/Invoke, .NET 4 không có `AesGcm`). `/ows` chỉ cần `Authorization: Bearer` (không cần HMAC; KHÔNG nhúng HMAC key vào tool). Cần PVN Overlay đã đăng nhập 1 lần trên máy đó.
  - **1 token = 1 kết nối**: bị đá (close 4000 "replaced", tức PVN Overlay đang mở) → `Kicked`, **dừng hẳn** (không nối lại để khỏi đá nhau), log 1 dòng; lỗi khác → nối lại 1→15 s. Người dùng: sau này chỉ mở tool, không mở PVN Overlay.
  - Tab "Bản đồ & overlay": ô "Kết nối PVN" (`settings.txt` `pvnlive`, mặc định 1) + dòng trạng thái (loài, %, lần/s); ô "Hiện overlay chỉ số" (`vitalsov`, mặc định 1). `PvnLive.Handle` tách số liệu thành `Vitals` (regex, không thư viện JSON) → `PvnLive.Last`.
  - **Hiển thị = cửa sổ WinForms riêng `VitalsOverlay.cs`, KHÔNG làm trên HTML/bản đồ web** (người dùng: web sẽ nặng; ban đầu đã làm trong `map_template.html` rồi gỡ ra). Tự vẽ GDI (TextRenderer), timer 250 ms, chỉ vẽ lại khi có frame mới / đổi trạng thái mất tín hiệu. TOPMOST + `WS_EX_LAYERED|TOOLWINDOW|NOACTIVATE`, khoá = `WS_EX_TRANSPARENT` (bấm xuyên) – **dùng chung khoá Ctrl+F9 và ô "Độ đậm" với radar**; chỉ gọi `SetWindowPos` khi mất cờ TOPMOST. Mở khoá: kéo (HTCAPTION), lăn chuột đổi cỡ 0,6–2,5; lưu `vitalspos=x,y,cỡ` trong `settings.txt` (ra ngoài mọi màn hình → về mặc định: mép trái, 55 % chiều cao). Ẩn khi chưa có dữ liệu / tắt kết nối.
  - Nội dung: thanh Máu, Thể lực, Đói, Khát luôn hiện; Blood (khi chảy máu hoặc thiếu), Oxy (khi thiếu); < 25 % số đỏ. Ô "Hiện số thật (31/50) thay vì %" (`vitalsnum`, mặc định 0; max ≥ 100 → số nguyên, nhỏ hơn → 1 chữ số lẻ). **Gãy xương: 3 chỗ (chân/thân/đầu)**, mỗi chỗ đang gãy ("Gãy chân", % đỏ) hoặc chưa hồi xong `recoverPct` < 100 ("Xương đầu") = 1 thanh riêng (luôn %). Dòng đỏ: "Chảy máu −X/s (hết sau ~m:ss)" (tốc độ đo trên ~10 s blood), Xương giòn m:ss, Ốm, Vi khuẩn, debuff khác. > 5 s không có frame → tối + "PVN: mất tín hiệu". Kiểm chứng: WS thật nhận 16 frame/4 s (rồi bị PVN Overlay đang mở đá → dừng đúng); overlay chụp bằng `DrawToBitmap` với dữ liệu giả (khoá / mở khoá cỡ 1,4 / mất tín hiệu).
  - Gợi ý (chưa làm): so giá trị WS với `attr0…` của `TIAttributeSetDinosaur` trong replay để đặt tên chỉ số.
- **Máu đối thủ khi đánh nhau (combat)** (2026-11-05, `PvnCombat.cs`): HTTP `GET https://islepilot.eu/api/overlay/combatlog` mỗi 2 s (khi bật kết nối PVN + ô combat + `pvnLive.Ok` = đang trong game). Hiện **trong overlay chỉ số** (`VitalsOverlay`, mục "⚔ Đối thủ (N)"): loài · tên · % trưởng thành; thanh máu + số (`hpAfter`), "hạ gục" nếu bạn giết, "máu ?" nếu con đó chỉ đánh bạn (không học được máu). Ô "Khi đánh nhau: hiện máu đối thủ" (`settings.txt` `vitalscombat`). Nút đổi nhịp gọi **2 s ↔ 0,5 s** (`PvnCombat.PollMs`, `settings.txt` `combatms`; 0,5 s mượt hơn nhưng gọi server nhiều gấp 4).
  - **Khác /ows và heatmap: combatlog BẮT BUỘC ký HMAC.** `X-Overlay-Client: PVN`, `X-Overlay-Ts: <ms>`, `X-Overlay-Sig = HMAC_SHA256(key, "<ts>.<METHOD>.<path+query>")` hex (không gồm body) + Bearer token. **Khoá HMAC KHÔNG để trong mã nguồn** (repo tự build + release công khai → lộ). `PvnSign.HmacKey` lúc mở tool **chỉ đọc `pvn_hmac.txt`** (64 hex, tức thì). Việc dò bản PVN-Overlay (nặng) để cho **nút "Quét khoá PVN"** (`PvnSign.Scan`, luồng nền): dò `app.asar`, tìm thấy → **lưu `pvn_hmac.txt`** (lần sau tự có, vẫn dùng được nếu sau này gỡ PVN-Overlay). Chưa có khoá → ô combat + nút nhịp gọi mờ (`UpdateCombatEnabled`, nhãn "Chưa có khoá PVN → bấm Quét khoá PVN"). Khoá = khoá trong repo `integrate-game` (khớp hệt). Phần khác không ảnh hưởng.
    - **Dò nhiều nơi** (`AsarCandidates`, máy bạn bè cài khác chỗ – 2026-11-05 bạn bè báo không bật được combat): lười (yield), thử đường dẫn file trước (nhanh ~25 ms khi cài chuẩn), chỉ quét tiến trình nếu không ra (~0,8 s → vì vậy để nút, không tự chạy lúc mở). Gốc: `%LOCALAPPDATA%\Programs`, `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%LOCALAPPDATA%` × tên {`PVN-Overlay`, `pvn-isle-overlay`, `isle-overlay`, `IsleVN-Overlay`, `PVN Overlay`}; rồi quét thư mục con tên chứa pvn/isle/overlay; cuối cùng lấy thư mục của tiến trình overlay đang chạy (`Process.MainModule`).
    - Đọc asar không cần công cụ ngoài (`ExtractAsar`): header = 2 Pickle (`u32 p0=4, u32 headerSize`; rồi `u32, u32 strLen, <JSON cây thư mục>`), vùng data bắt đầu ở `8 + headerSize`; mục file `"…cjs":{"size":N,"integrity":{…},"offset":"OFF"}` — **offset nằm sau object `integrity` lồng nhau** nên phải quét đúng cặp ngoặc (`ObjectAfter`), rồi đọc `fsize` byte tại `dataStart + offset`.
  - **Cấu trúc combatlog** (envelope `{enabled, staff, hits:[…50 đòn lịch sử…], nextBefore}`; mỗi hit **phẳng**): `id, at` (ISO UTC), `attackerSteamId/Name/Species/Growth`, `victimSteamId/Name/Species/Growth`, `cause, ability, damage, hpBefore, hpAfter, distanceM, killed`. **Chỉ đòn liên quan tới bạn** (so `attackerSteamId`/`victimSteamId` với steamId của mình = `PvnToken.SteamId()` từ settings PVN). Con đối thủ = bên không phải bạn; biết máu nó (`hpAfter`) chỉ khi **bạn** là attacker. Trả cả lịch sử nhiều ngày → **lọc theo `at`** (chỉ đòn ≤ 10 s = đang combat; nếu không lần poll đầu tưởng cả 50 trận đang diễn ra), gộp theo SteamId, dedup theo `id`. `integrate-game` CONTEXT.md mục 5e/6 mô tả thêm.
- **Thanh la bàn** (2026-11-05, `Compass.cs`): cửa sổ WinForms riêng (giống `VitalsOverlay`: TOPMOST, bấm xuyên khi khoá, **chung Ctrl+F9 + độ đậm**, kéo/lăn chuột khi mở khoá, `settings.txt` `compasspos`, `compass` = ô "Hiện thanh la bàn" ở tab "Bản đồ & overlay", mặc định bật; `compassfov` = khung sáng "trong màn hình", mặc định 90). Mặc định giữa phía trên màn hình. Giữa thanh = **hướng camera** của bạn = `RemoteViewYaw` (1 byte, ×360/256, yaw Unreal → la bàn +90; kiểm chứng fast7: khi chạy, lệch so với hướng thân 0 ± 15°; không có thì dùng hướng thân). Thanh ±90°, vạch 15°, chữ B/ĐB/Đ…; chấm = khủng long người chơi ≤ 1000 m (đỏ < 50 m, cam < 150 m) + nhãn tên + m; ngoài ±90° → mũi tên ◀ ▶ ở mép (con gần nhất, "+N", "(sau lưng)" khi > 150°). `Replay.CompassSnapshot` chạy trên luồng đọc trực tiếp (chỉ khi đang quay), giao diện chỉ vẽ lại khi có dữ liệu mới; > 3 s không có dữ liệu → tối, > 20 s → ẩn. Ở < ~3 m hướng không có nghĩa (vị trí là tâm thân). Kiểm chứng: cắt fast7 tới 49:13 → Henry 2 m lệch 4° (giữa thanh); 50:08 → Henry sau lưng (mũi tên).
- **Thú AI** (2026-11-04): class `/Game/TheIsle/Core/Characters/Animals/<Loài>/BP_<Loài>` (Deer, Boar, Rabbit, Chicken, Crab, Seaturtle… – fast1: 7 hươu, 7 lợn, 3 gà, 3 cua, 2 thỏ) → lớp `animal` (chết → `corpse`). Bản đồ: chấm xanh lá `--animal`, nhãn tên tiếng Việt (`VI_ANIMAL`/`viName`) + % + khoảng cách; có trong "Gần bạn nhất", overlay, ô lớp bên tool (`LayerDefs` "animal"). AI dino (khủng long không SteamId) vẫn bỏ.
- **Phím zoom** (2026-11-04): **Ctrl+F10** phóng to / **Ctrl+F11** thu nhỏ (`RegisterHotKey`, giữ phím được) → `LiveServer.PushEvent("zoom","in|out")` (SSE `event: zoom`) → `zoomStep`: overlay đổi `ovR` (lưu localStorage), bản đồ zoom quanh tâm. Không cần mở khoá overlay.
- **Tầm server gửi + "mất dấu"** (2026-11-04): server chỉ replicate khủng long trong tầm (đo từ replay: Pteranodon ~300 m, Allosaurus ~420 m, Deinosuchus ~430–500 m), ra ngoài là kênh đóng (`e`) → không còn dữ liệu, KHÔNG thể thấy xa hơn. Bản đồ giữ vị trí cuối (`ghostAt`, `GHOST_S` = 180 s (người dùng: quá 3 phút thì xoá)): vòng nét đứt mờ + nhãn mờ "mất dấu m:ss trước · ~N m"; bỏ qua nếu cùng tên người chơi đang hiện ở kênh khác **hoặc có lần mất dấu mới hơn** (mỗi người chỉ 1 chấm – sửa lỗi trùng khi ra/vào tầm nhiều lần); chỉ cho lớp "player" (chết → thành xác).
- **Khoảng cách trên nhãn** (2026-11-04): dòng 2 của nhãn mỗi con (trừ bạn) có thêm "· N m" (khoảng cách 3D tới bạn ở thời điểm đang xem; tính lại mỗi lần vẽ nên tự cập nhật theo dữ liệu trực tiếp).
- **Nhãn trên bản đồ** (2026-11-04): mỗi chấm luôn có nhãn 2 dòng – tên người chơi (bạn = "Bạn", vàng) / loài · % trưởng thành (không hiện giới tính, không SteamID); `placeLabels` (2026-11-04, người dùng thấy nhãn trôi xa chấm): thử chỗ gần trước (phải, trái, trên, dưới, 4 góc, rồi dịch ≤ 3 dòng), né cả nhãn đã đặt lẫn **mọi chấm**, ưu tiên trong khung; vạch màu cùng màu chấm **ở phía gần chấm** (nhãn bên trái chấm → vạch mép phải, chữ căn phải; 2026-11-05: trước luôn ở mép trái nên nhãn trái trông như của chỗ khác) + đường nối cùng màu (viền đen) khi nhãn không sát chấm; mũi tên của bạn (`meArrow`) là vật cản (nhãn không đè).
- **Cắt replay** (xem lại trong game) – **đã chạy được trong game (2026-11-04)**:
  - Giao diện (2026-11-04): **tab riêng "Cắt replay"** (tab 1 = "Đọc replay & bản đồ"; ô log dùng chung dưới 2 tab). Tab cắt có danh sách replay riêng (`cutList`, chọn 1 file, đồng bộ trong `LoadList`), dòng tên file · độ dài, "Từ [ ] Đến [ ] [Cắt]" (Enter cũng cắt). Chuột phải ở tab 1 → "Cắt đoạn…" = sang tab cắt, chọn sẵn file đó. Thời gian: `52` (phút), `52:30`, `1:05:00`; trống "Đến" = tới hết. Báo lỗi: chọn ≠ 1 file, file đang quay, sai định dạng, bắt đầu ≥ độ dài, kết thúc ≤ bắt đầu; file đã có → hỏi ghi đè. Ghi `.tmp` rồi đổi tên.
  - **Gộp chunk** (thử nghiệm, 2026-11-04): `DinoReplayReader.exe <file> -merge [-o <file mới>]` → `<tên>_gop.replay`: nối các chunk dữ liệu 0,125 s liền nhau thành chunk ≤ 20 s, luôn ngắt ở checkpoint (→ ~15 s/chunk, như thông số mặc định). Mục đích: máy chưa chỉnh `localReplay.*` (bạn bè) xem mượt. Kiểm chứng: events/positions/dinosaurs/players.csv + phần actors của data.js giống hệt file gốc. **Cắt (GUI + `-cut`) luôn gộp chunk** (tham số `mergeMs` của `ReplayCut.Cut`, mặc định 20000; `-nomerge` để tắt). Kiểm chứng: `-cut 0 -nomerge` = file gốc từng byte; `-cut 0` = `-merge` file gốc từng byte; dinosaurs/players.csv giống file gốc. Độ mượt trong game chưa được so.
  - Dòng lệnh: `DinoReplayReader.exe <file> -cut <từ> [đến] [-keeptime] [-o <file mới>]` (cùng định dạng thời gian).
  - **Giờ trong header file cắt lùi 30 phút** như AHK (`TsShiftMin`, `TsShiftTicks`, 2026-11-05): so header gốc với giờ thật (giờ sửa file − độ dài); nếu file gốc đã lùi sẵn (file AHK đổi tên) thì không lùi thêm. Vì vậy `-cut 0 -nomerge` không còn giống file gốc từng byte (khác 8 byte timestamp).
  - File mới **cạnh file gốc** (thường là Demos → game thấy ngay): `<tên>_cat_52-00_den_het.replay` / `_den_58-30`. Giờ sửa file đặt = giờ thật kết thúc đoạn (để cột "Thời gian quay" đúng).
  - Bắt đầu từ **checkpoint gần nhất ≤ giờ cắt** (checkpoint mỗi 15 s). Mặc định đánh lại giờ từ 0:00 (sửa giờ chunk, giờ từng frame, giờ frame trong checkpoint = `DataStart + 8 + PacketOffset + 4`, LengthInMS, timestamp header + giờ cắt); `-keeptime` giữ giờ gốc. `-chunks` liệt kê chunk, `<file> -cpdump <n>` xem đầu checkpoint thứ n.
  - **Game mở replay là phát từ khối dữ liệu đầu, KHÔNG đọc checkpoint (chỉ đọc khi tua)** → chỉ chép checkpoint thì màn hình đen (đã thử). Vì vậy checkpoint đầu đoạn được **chuyển thành 1 frame dữ liệu** (chunk dữ liệu mới ngay sau chunk Header): bảng NetGUID → NetExportGuids (outer trước, bỏ mục đường dẫn rỗng), bảng NetFieldExportGroup → NetFieldExports, rồi frame của checkpoint; checkpoint đó bị bỏ khỏi file mới (các checkpoint sau giữ lại để tua). Bỏ qua danh sách startup actor đã xoá (vài vật thể bản đồ có thể hiện lại).
  - Cấu trúc checkpoint (bản này): `i64 PacketOffset` (tính từ sau chính nó → frame), `i32 level`, `i32 n + FString` startup actor đã xoá, `i32 n` × (`packed guid, packed outer, u8 bExported, FString | packed chỉ số bảng tên, u8 flags(1=NoLoad)`), `u32 n` × group (`FString path, packed idx, packed num, num × field export`), rồi frame thường.
  - Frame từ checkpoint đặt **ở đầu khối dữ liệu thật đầu tiên** (sửa len/mem của khối đó), thời điểm frame = **0,001 s**. Frame ở đúng 0,000 s bị game **bỏ qua** (log: packet đầu tiên được xử lý thuộc frame thứ 2, "invalid seen level index", "unreliable bunch before open"). Bản 0,001 s: **xem được trong game**.
  - **Log game**: `%LOCALAPPDATA%\TheIsle\Saved\Logs\TheIsle*.log` (`LogDemo`, `LogNetTraffic`, `LogLocalFileReplay`) – xem ở đây khi replay lỗi. Game **tua được** (`GotoTimeInSeconds` trong log).
  - Lệnh chẩn đoán: `-frames <n>` (tóm tắt frame đầu, export), `-bunchstats` (cờ bunch của frame đầu).
  - Kiểm chứng: cắt từ 0 → giống hệt file gốc từng byte; file cắt từ 51:45 đọc bằng tool 0 lỗi, khủng long cuối khớp file gốc; file 0–2 phút xem được trong game. `demo5_tu52p.replay` (từ 51:45) xem được trong game.

### Kết quả: `output\<tên replay>\`
| File | Nội dung |
|---|---|
| `summary.txt` | Báo cáo tiếng Việt: server (số người, AI, FPS, giờ trong game), khủng long trong tầm nhìn (★ của bạn, người chơi, giới tính, % trưởng thành, vị trí, **khoảng cách + hướng** so với bạn), danh sách người chơi server, đếm đối tượng |
| `dinosaurs.csv`, `players.csv` | Bảng |
| `map.html` + `data.js` + `version.js` | Bản đồ (xem §5) |
| `events.csv`, `positions.csv` | Dữ liệu thô, **chỉ khi `-full`** / tick "Xuất thêm dữ liệu thô" (rất nặng) |

### Cấu trúc thư mục
```
IsleReplayReader\
  DinoReplayReader.exe      bản build (WinExe)
  map_template.html         khuôn bản đồ (được copy thành output\*\map.html, và phục vụ ở /live/map.html)
  maps\gateway-v0.21.7\     base.webp, water.webp, land.webp (7800×7817 px) – tải từ islepilot.eu (PVN Overlay). Dùng cá nhân, không chia sẻ lại.
  output\                   kết quả
  src\*.cs                  MÃ NGUỒN (bản chuẩn – sửa ở đây)
  DocNhanh.bat, DocDayDu.bat
```
Chuyển thư mục tool đi nơi khác: chép **cả thư mục** (exe cần `map_template.html` và `maps\` bên cạnh).

### Git / Release (cập nhật 2026-11-05)
- Repo: **https://github.com/skydark3004/dino_helper** (gốc repo = thư mục `IsleReplayReader\`; script AHK ở thư mục cha KHÔNG nằm trong repo). `.gitignore`: `output/`, `pvn_cookie.dat`, `pvn_hmac.txt`, `*.old*.exe`, `settings.txt`, `version.txt`, `perf_log*.txt`, `version_yesterday/`.
- **Bạn bè cập nhật bằng `CapNhat.bat`** (2026-11-05; bạn bè dùng `git clone`, `git pull` bị chặn vì tool tự ghi vào file có trong repo): bat tự chép sang %TEMP% rồi chạy (file bat có thể bị thay lúc cập nhật), đợi tắt tool, sao lưu `gastro_learned.txt` + `map_layers.txt`, `git fetch` + `git reset --hard @{u}` (fallback `origin/master`), chép lại 2 file đó. Không đụng file không theo dõi (settings.txt, cookie, output). Chữ không dấu (ASCII, CRLF). **File tool tự ghi KHÔNG được nằm trong repo**: `gastro_learned.txt` (điểm sỏi của máy đó) + `map_layers.txt` trong `.gitignore`; điểm sỏi đi kèm repo nằm ở `gastro_seed.txt` (chỉ đọc; `GastroStore` nạp seed + learned, bỏ điểm trùng < 15 m). `DinoReplayReader.exe` CÓ trong repo → phải build + commit exe mới. Kiểm chứng: repo giả (bare) + bản clone có gastro/map_layers đã sửa → bat cập nhật được, giữ điểm, status sạch.
- **2026-11-05: ĐÃ XOÁ `.github/workflows/release.yml`** (repo sẽ chuyển **private** → auto-release + link tải công khai không còn dùng). Phân phối cho bạn bè **bằng tay** (chép thư mục / zip). `CleanupOld` xoá `.old*.exe` khi mở tool vẫn chạy. `version.txt` không còn ai ghi (tool tự build → coi như bản tự build).
- `Updater.cs` + `BuildUpdateTab` **còn trong mã nhưng không gọi** (đã bỏ tab Cập nhật). Muốn bật cập nhật lại thì cần nguồn tải khác (repo private không cho tải release ẩn danh).
- Build thủ công: xem mục "Build" dưới. Thêm/sửa file nguồn trong `src\*.cs` là đủ (build gộp `*.cs`).

### Build (máy không có Python / .NET SDK, chỉ có csc .NET Framework 4 → **C# 5**: không `$""`, không `?.`, không `catch … when`, không `=>` cho property)
```powershell
cd <thư mục tool>\src
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /nowarn:649 /target:winexe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll `
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Security.dll /out:..\DinoReplayReader.exe *.cs
```
Nếu tool đang chạy, exe bị khoá: build ra tên khác rồi **đổi tên** exe cũ (`DinoReplayReader.oldHHmmss.exe`) và đặt bản mới vào; xoá bản `.old*` sau khi tắt tool.
Sau khi đổi `map_template.html`: copy đè vào `output\*\map.html` (các file đó chỉ là bản copy của khuôn).

### Mã nguồn (`src\`)
| File | Vai trò |
|---|---|
| `Bits.cs` | `ByteReader` (có kiểm tra biên → `EndOfStreamException`), `BitReader` (LSB trước; chế độ `Soft`/`Failed` để giải mã thử **không ném exception** – exception trong .NET rất chậm) |
| `Replay.cs` | Header file, duyệt chunk, frame; **đọc nối tiếp** (`LoadFile` + `ParseMore`, nhớ `resumeChunk/resumeFrame`, chỉ đọc lại header + đầu chunk đang dở + phần đuôi mới; `FrameComplete` kiểm tra frame đủ dữ liệu **trước** khi xử lý); NetFieldExports, NetGUID |
| `Packets.cs` | Packet → bunch header |
| `Actors.cs` | Actor channel, content block, thuộc tính (RepLayout), giải mã giá trị, `ReplicatedMovement`, external data, `Ev()` (ghi sự kiện/vị trí, tăng `Actor.Ver`) |
| `Report.cs` | `summary.txt`, CSV, khoảng cách/hướng, `FindMe`, `WriteLiveMap` |
| `MapExport.cs` | JSON bản đồ (cache JSON từng actor theo `Ver`, đường đi chỉ nối thêm điểm mới), ghi `data.js`/`version.js` (ghi tạm rồi `File.Replace`) |
| `Server.cs` | Máy chủ HTTP mini (TcpListener, **chỉ 127.0.0.1**): phục vụ file trong thư mục tool (chặn path traversal), `/events/<tên>` = Server-Sent Events đẩy dữ liệu, `/live/...` = replay đang quay |
| `Gui.cs` | WinForms: danh sách, trích xuất (1 file), xoá, tự theo dõi (FileSystemWatcher + timer 1 s), đẩy dữ liệu |
| `Cut.cs` | Cắt replay (`-cut`), gộp chunk (`-merge`), liệt kê chunk (`-chunks`) |
| `Alerts.cs` | Phát hiện khủng long người chơi mới vào vùng (`CheckNewDinos`), ô thông báo góc màn hình (`AlertToast`), âm báo |
| `Overlay.cs` | `EdgeOverlay`: mở/tìm/giữ cửa sổ overlay radar (TOPMOST, trong suốt, bấm xuyên, cắt thanh tiêu đề, nhớ vị trí) |
| `PvnHeat.cs` | Bản đồ nhiệt PVN: script lấy cookie, lưu cookie (DPAPI), gọi API heatmap |
| `PvnLive.cs` | Chỉ số khủng long của bạn: giải mã token PVN Overlay (DPAPI + AES-GCM bcrypt), WebSocket `/ows`, tự nối lại, `Vitals` |
| `VitalsOverlay.cs` | Cửa sổ overlay chỉ số (WinForms tự vẽ, luôn nổi, bấm xuyên khi khoá, kéo/đổi cỡ khi mở khoá); mục "Đối thủ" khi combat |
| `Compass.cs` | Thanh la bàn: hướng camera của bạn + khủng long người chơi xung quanh (`CompassSnapshot`, `CompassOverlay`) |
| `PvnCombat.cs` | Máu đối thủ khi đánh nhau: ký HMAC (`pvn_hmac.txt`), gọi combatlog mỗi 2 s, lọc đòn liên quan tới bạn |
| `Main.cs` | Điểm vào (không tham số → GUI; có → dòng lệnh + `AttachConsole`), `ExtractReplay`, chế độ thử `-simulate <bytes>` |

## 4. Định dạng replay đã giải mã (quan trọng)

Unreal **LocalFileReplay**, không nén, không mã hoá. Kiểm chứng: ~60.000 packet đọc khớp bit, 0 lỗi.
- File header: magic `0x1CA2E27F`, ver 7, custom versions (n×20 byte), `LengthInMS`, NetworkVersion, Changelist, FriendlyName (FString), **`bIsLive` (uint32)**, **Timestamp** (int64 tick .NET, offset ~566), bCompressed, bEncrypted, key.
- Chunk: `type(u32) size(i32)`; 0 Header (có `/Game/TheIsle/Maps/Game/Gateway/Gateway`, "UE5" 5.6), 1 ReplayData (`start,end,len,mem` rồi frame), 2 Checkpoint, 3 Event. Data chunk 15 s.
- Header demo: NetworkVersion 19, EngineNetworkVersion 42, flags = ClientRecorded | StreamingFixes.
- Frame: level(i32), time(f32), NetFieldExports, NetExportGuids, streaming levels, u64, **external data**, packets (`seenLevel packed, size i32`, 0 = hết).
- Bunch: **MaxPacket = 16384 bit**; partial có thêm 1 bit **bPartialCustomExportsFinal**; ChIndex packed; tên kênh "#102" = Actor.
- Thuộc tính: **có bit checksum** đầu payload; handle packed (−1), numBits packed. Subobject header: **không có bit destroy**, có bit `bActorIsOuter`.
- Vị trí: `ReplicatedMovement` = cờ (4/3/2 bit) + QuantizedVector UE5 (7 bit info: số bit/thành phần + cờ scale) + rotator byte (hoặc short) + velocity…; hệ số vị trí khác nhau theo class → **tự dò** bằng cách so với vị trí spawn (1/10/100). Spawn dùng packed vector scale 10.
- Đơn vị = cm Unreal = **giống toạ độ minimap PVN**. **Bắc = −Y, Đông = +X** (kiểm chứng: đi về Nam thì Y tăng). Yaw Unreal 0 = Đông, 90 = Nam → la bàn = yaw + 90.
- Khủng long của bạn = actor có thuộc tính `Controller` (chỉ gửi cho chủ). Tên người chơi qua `PlayerState` → `PlayerNamePrivate`.
- Chỉ số `TIAttributeSetDinosaur` (attr0…) **chưa biết tên** (máu/thể lực/…).
- Replay chỉ có những gì **ở gần bạn** (trừ danh sách người chơi server).
- Game ghi file khi đang quay: rất dày (~0,15 s/lần, 1–2 KB; mỗi 15 s +~430 KB checkpoint) sau khi người dùng chỉnh setting game "1s".

## 5. Bản đồ (`map_template.html`)

- Lớp hiển thị (người dùng chọn giữ lại): **Khủng long của bạn, Người chơi khác, Xác, Cá, Thịt/nội tạng, Địa danh, Ảnh bản đồ Gateway, Vũng bùn**. Đã bỏ cây, trái cây, hang, tổ, ptero, AI dino.
- Ảnh nền Gateway: 3 lớp webp gộp **1 lần** thành canvas 4096 px (gần) + 2048 px (xa), rồi bỏ ảnh gốc (tránh lag). Hiệu chuẩn 2 điểm của PVN:
  `a: u .9320964895641363 v .3053043426801558 = (534057.925, -267245.9)`, `b: u .5356221651318197 v .4496109559223209 = (87931.248, -104086.204)`. 84 địa danh + 15 vũng bùn (`GATEWAY.mud`, nhóm "Mud Pools" của PVN, chỉ có điểm tâm) lấy từ PVN; replay không chứa vị trí bùn (bùn là vật liệu địa hình).
- **Không vẽ vệt đường đi** (2026-11-04, người dùng muốn nhẹ; dữ liệu `t` vẫn giữ cho thanh thời gian/hướng/mất dấu).
- Bạn = **mũi tên vàng** theo hướng nhìn (2026-11-04: nhỏ lại, hệ số 1,05; **không có nhãn** – bỏ chữ "Bạn"/loài/% theo yêu cầu); vòng 100/250/500/1000 m; thanh thời gian mặc định ở **thời điểm mới nhất**.
- **Dưới nước / trên cạn / địa hình khi rê chuột** (2026-11-05, ô thông tin của khủng long/thú, `placeInfo`):
  - **Đang bơi**: replay có `ReplicatedMovementMode` (chế độ di chuyển Unreal: 1 đi, 3 rơi/nhảy, **4 bơi**, 5 bay; chỉ gửi khi đổi, có cả cho khủng long của bạn). Tool lưu lịch sử (`Actor.ModeHist`) → `data.js` `"mm":[[giờ,mode],…]`; trang `modeAt`. Kiểm chứng: ếch bơi 93,2–99,4 s; T-Rex của bạn nhảy (3 → 1 sau ~0,3 s). Chưa có replay khủng long bơi.
  - **Vùng nước**: mặt nạ từ kênh alpha `water.webp` (lớp nước PVN), dựng 1 lần trong `buildMapCanvases` ở nửa độ phân giải (`WM_PX` 3900, ~2,9 m/điểm, 15 MB) → `isWater`, `nearestWater` (vòng nở dần, ≤ 600 m) → "Trên cạn · nước gần nhất N m (hướng radar)". Khớp mép nước trên ảnh. Vũng nhỏ PVN không vẽ (ếch bơi cách vùng nước PVN 35 m) → ưu tiên "đang bơi" từ replay. Không phân biệt nước nông/sâu (vùng nước mà không bơi = "nước nông / đầm lầy").
  - **Địa hình ~10 m (ƯỚC LƯỢNG)** `terrainAt`: đọc vùng nhỏ từ `gw.detail` (ảnh nền 4096 px đã tối 25%) khi rê chuột, mỗi điểm xét cửa sổ 5×5 (`TERRAIN_W` 2): sáng (L>90) + ít xanh (g−(r+b)/2 < 9) = đá/cát; độ sần sd > `TERRAIN_SD` 14 hoặc tối (L<41) = cây rậm (tán + bóng); còn lại = trống (cỏ, bụi thấp) → tỷ lệ %. Cửa sổ 3×3 xếp ngọn cây nắng chiếu thành "trống" → dùng 5×5. Lưu tạm theo ô 3 m (`terrainCache`).
  - **Bụi cây / nấp: KHÔNG có trong replay** (game không gửi; `bHidden` chỉ của thịt rơi/trái cây; `Waterbody` chỉ của cá = mã khối nước). Tán cây che bụi thấp → ảnh nền không biết được.
  - Overlay đang khoá thì bấm xuyên → không rê chuột được (dùng bản đồ web hoặc mở khoá Ctrl+F9).
  - **Bố cục ô thông tin** (2026-11-05, người dùng thấy bản cũ khó hiểu): dòng 1 tên · loài; dòng 2 (mờ) loại (người chơi / thú AI / xác…) · giới tính (chỉ khủng long) · % trưởng thành; bảng nhãn | giá trị: "Cách bạn", "Chỗ đứng" (dưới nước – đang bơi / trong nước nông, đầm lầy / trên cạn · cụm từ địa hình `terrainWord`: rừng cây rậm ≥ 70 %, rừng thưa ≥ 35 %, đá/cát ≥ 50 %, còn lại bãi trống), "Nước gần nó / gần bạn" (m + hướng trên radar TỪ CHỖ CON ĐÓ); chú thích "* ước tính từ ảnh bản đồ…"; dòng cuối mờ: bấm để ghim · X Y Z. **Đã bỏ** "trên radar: … · phía sau bên trái" (người dùng: không cần).
- **Chỉ đường**: bấm chấm/tên để ghim → khoảng cách, hướng **theo radar** (Bắc ở trên: "trên-phải"…), mũi tên "theo radar" + "theo hướng bạn nhìn", "xoay sang phải ~X°", địa danh gần nhất. (Người dùng thấy "hướng đồng hồ" khó dùng vì game không có.)
- **Hiển thị mượt** (2026-11-04, người dùng: "radar vs map hiển thị mượt nhất"):
  - Đang quay + xem thời điểm mới nhất (`follow`): **vẽ liên tục** bằng rAF, tối đa ~60 khung/s (`MIN_FRAME_MS` 12). Thời gian hiển thị = đồng hồ trực tiếp (`live`, `liveTime`): chạy đều theo đồng hồ máy, trễ sau dữ liệu mới nhất `liveLag()` = trung bình + 2×độ lệch của khoảng cách giữa các lần nhận (0,15…1,5 s), không bao giờ lùi, không vượt `tmax` (tool gửi `"tmax"` = `MaxTime` = frame cuối đã đọc). Kéo thanh thời gian lùi / bấm Phát → `follow=false`; kéo về cuối → bám lại.
  - `posAt` / `headingAt` **nội suy** giữa 2 mẫu (tìm nhị phân `sampleIdx`). Mẫu chỉ lưu khi đi ≥ 0,3 m nên khoảng trống dài = đứng yên → chỉ trượt trong `INTERP_S` = 0,5 s cuối trước mẫu sau; nhảy > 50 m thì không trượt. Hướng xoay theo góc ngắn nhất.
  - **2 lớp canvas**: `#bg` (ảnh bản đồ, lưới, địa danh, bùn, sỏi) to hơn màn hình `BG_MARGIN` = 384 px mỗi phía, chỉ vẽ lại khi `bgKey` đổi (zoom, lớp, cỡ, dữ liệu sỏi…) hoặc dịch quá 80 % lề; còn lại chỉ `style.transform = translate` (GPU). `#cv` trong suốt: chấm, nhãn, vòng, đường chỉ dẫn, số toạ độ lưới. Kiểm chứng: dịch bằng CSS = vẽ lại từ đầu, từng điểm ảnh.
  - Nhãn giữ chỗ khung trước nếu còn trống (`labelSlot`) → không nhảy trái/phải; độ rộng chữ lưu sẵn (`textW`). Thanh bên (HTML) chỉ cập nhật 5 lần/s khi vẽ liên tục; overlay không cập nhật thanh bên.
  - **Lỗi gây "giật" thật sự (đã sửa)**: `buildMapCanvases` đặt `im.src = ""` để giải phóng ảnh gốc → trình duyệt bắn `onerror` → handler "thử lại sau 2 s" (thêm 2026-11-04) tải + dựng lại ảnh 4096 px → vòng lặp vô hạn, trang treo ~4 s mỗi vài giây (cả bản đồ web lẫn overlay). Sửa: gỡ handler trước khi xoá src, `onerror`/`buildMapCanvases` bỏ qua khi `gw.ready`. Sau sửa (dữ liệu thật, game đang chạy): 60 khung/s, khung chậm nhất 27–79 ms (trước ~4000 ms), dữ liệu đến đều ~0,14 s. Tìm ra nhờ CDP `Profiler` (drawImage/fillRect chiếm 8/14 s).
  - Đo khi game đang chạy (GPU 93 % do game): JS mỗi khung ~1 ms (overlay 0,8 ms), trung vị 16,7 ms/khung; khựng còn lại là do Edge chờ GPU. Cửa sổ Edge bị game che hoàn toàn → Edge tự dừng vẽ (overlay TOPMOST nên không bị). Thử đo: Edge `--remote-debugging-port` + Node (WebSocket CDP) + `--disable-features=CalculateNativeWinOcclusion`; `--virtual-time-budget` chỉ ~8 khung/s, không dùng để đo độ mượt.
- Cập nhật trực tiếp: mở qua `http://` → nhận **SSE** (tức thì); mở `file://` → kiểm tra `version.js` mỗi 250 ms rồi nạp `data.js`. Khi `/live` chuyển file mới: bỏ mục tiêu ghim.
- Nguồn POI/ảnh/hiệu chuẩn: app **PVN Overlay** (Electron, `resources/app.asar`, bundle `index-*.js`; `sZ="/maps/gateway-v0.21.7"`, `aZ` = hiệu chuẩn, `rZ` = POI; base URL `https://islepilot.eu`).

## 6. Hiệu năng (đo 2026-11-03)
- Trích xuất lần đầu: 4,7 MB ≈ 1 s; 30 MB ≈ 3 s.
- Cập nhật trực tiếp: 3–12 ms/lần; game ghi file → bản đồ nhận ≈ **28 ms trung bình**.
- **Tối ưu vòng cập nhật trực tiếp (2026-11-05)**: tool dùng 18 % một nhân khi đang quay (~7 lần/s × ~32 ms). Đo bằng chương trình riêng (build `src\*.cs` + file đo, `/main:Bench`, ghi dần bản copy replay đang quay 3,3 KB/lần) → ~2,3 ms/lần:
  - **Mở file 15,6 ms** (Windows Defender quét lại file game vừa ghi), đọc thật chỉ 0,3 ms → `LoadFile` **giữ file mở** (`liveFs`, `FileShare.ReadWrite|Delete`, bufferSize 1) khi `bIsLive`; tự đóng khi quay xong, khi đường dẫn đổi, khi file ở đường dẫn nhỏ hơn file đang giữ (bị tạo lại); `CloseFile()` ở `Gui.DropLiveReader` / khi bỏ bộ đọc (luồng đọc đang chạy thì nó tự đóng lúc xong). Kiểm chứng: AHK đổi tên + mở ghi header khi tool đang giữ → được.
  - **Duyệt cả ~4.400 actor nhiều lần mỗi lần** (`FindMe`, `PlayerStates`, `CheckNewDinos`, `WriteMap` với `Species`/`Contains` chuỗi class) → `ClassifyNew()` phân loại 1 lần (`Actor.Kind`, `SpeciesName`; class gán 1 lần lúc tạo, `AllActors` chỉ dài thêm), giữ danh sách `dinoActors`, `playerStateActors`, `mapActorIdx` theo thứ tự `AllActors`. `WriteMap` so trường (`JsonVer/JsonCat/JsonPlayer/JsonClose`) thay vì ghép chuỗi khoá, nhãn chỉ tính khi dựng lại. Kết quả: khủng long mới 2,9 → 0,02 ms, JSON bản đồ 9,9 → 1,1 ms.
  - Còn lại: báo cáo đầy đủ (`Finish`) mỗi 15 s ~65–100 ms.
- **Máy bạn bè "giật 1–2 s mới cập nhật" (2026-11-05)** – 2 nguyên nhân, đã xử lý cả hai:
  - **Game ghi replay thưa**: bản đồ chỉ nhanh bằng `localReplay.ChunkUploadDelayInSeconds` (Engine.ini; máy người dùng 0,125). Chưa chạy `IsleReplayConfig.bat` hoặc chọn mục 3 (trước đây mặc định **1 s**, nay 0,125) → dữ liệu mỗi 1–2 s. Tool đo nhịp thật (`TrackWriteInterval`: trung vị khoảng cách giữa các lần `MaxTime` tăng, 20 mẫu), hiện "· game ghi mỗi X s" trên dòng trạng thái, > 0,6 s → cảnh báo 1 lần trong log kèm giá trị Engine.ini (`ReplayChunkSetting`) và cách sửa (chưa đặt nhanh → chạy bat chọn 2; đã đặt nhanh → khởi động lại game). `HUONG_DAN.txt` bước 3 ghi rõ phải chạy bat chọn 2. **Engine.ini chưa đặt = game ghi theo khối ~15 s** (log bạn bè 2026-11-05: mọi khoảng trống 14,7–15,1 s). Trung vị không bắt được (mỗi khối tool đọc qua vài lần sát nhau → trung vị < 0,6 s) → thêm điều kiện: ≥ 3 lần trống > 5 s trong 90 s cũng cảnh báo (`WarnSlowWrites`); dòng `[game] … không có dữ liệu` ghi rõ nguyên nhân Engine.ini khi chưa đặt nhanh.
  - **FileSystemWatcher không báo** (một số máy) → tool chỉ đọc theo `liveTimer` 1 s. Nay thêm `liveFastTimer` 100 ms: hỏi kích thước qua handle đang giữ (`Replay.HeldLength`, không mở lại file) → không phụ thuộc watcher. Kiểm chứng (giao diện thật, ghi lại replay theo đúng từng chunk/thời điểm): tắt watcher + game ghi 0,15 s → trước 1,03 s/lần, sau 0,16 s/lần.
  - **Log chỗ chậm `perf_log.txt`** cạnh exe (`Perf.cs`, `PerfLog.Write(key, msg, val)`; > 2 MB → `perf_log.old.txt`; `.gitignore`). **Mặc định TẮT**; bật/tắt bằng `IsleReplayConfig.bat` **mục 5** → `settings.txt` cạnh exe `perflog=1/0` (bat lấy thư mục của mình qua `set "DINO_DIR=%~dp0"` vì phần PowerShell chạy bằng `iex`, không có `$PSScriptRoot`; giữ các dòng cài đặt khác). Tool đọc lại khi `settings.txt` đổi (`RefreshPerfSetting`, mỗi tick `liveTimer` 1 s) → áp dụng ngay không cần mở lại; ô log báo "Ghi log chỗ chậm: BẬT/TẮT"; bật giữa chừng → ghi dòng thông tin máy (`PerfHeader`). Tắt: `Write/Flush` không làm gì (cả `/log` của trang). Kiểm chứng giao diện thật: tắt → game dừng 4,5 s không tạo file; bật → ghi; tắt lại → không ghi thêm. (Thử bat bằng `cmd /c x.bat < input` KHÔNG dùng được – cả bản gốc cũng chạy sai; thử bằng cách lấy hàm ra `iex`.) **Chống spam (gộp)**: mỗi key ghi ngay lần đầu, các lần sau trong 30 s chỉ đếm + nhớ lần nặng nhất (`val`), hết 30 s ghi 1 dòng "↳ N lần nữa trong 30 s, nặng nhất: …" (`PerfLog.Flush` gọi mỗi 1 s từ `liveTimer`) → chậm liên tục cũng chỉ ~1 dòng/30 s mỗi loại; key null = luôn ghi (dòng hiếm). Trang dùng cùng cơ chế (`perfLog/perfFlush`, `perfW`); "trễ" (gọi mỗi lần nhận dữ liệu) chỉ 1 lần/5 phút. Máy chủ chặn `/log` > 2 lần/s. Ô log tool: tối đa 1 dòng `[chậm]` / 20 s (kèm "+N dòng khác trong perf_log.txt"). Kiểm chứng: 200 lần chậm liên tục → 2 dòng; trang 100 lần treo → 1 dòng + 1 tóm tắt. Ghi: `[tool]` bắt đầu theo dõi (bản, Engine.ini, số nhân, Windows), đọc lần đầu (MB, ms), **cập nhật > 150 ms** (đọc file / giải mã + KB mới / báo khủng long / dữ liệu bản đồ hoặc báo cáo đầy đủ / gửi trang + đoán nguyên nhân); `[game]` > 3 s không có dữ liệu mới, ghi replay thưa; `[trang]` (trang gửi `GET /log?m=` mỗi 3 s, mỗi loại ≤ 1 lần/10 s, bỏ qua khi trang bị ẩn): mở trang + GPU (WebGL renderer), khung hình treo > 250 ms, vẽ 1 khung > 50 ms (có vẽ lại nền?), xử lý dữ liệu > 60 ms, > 3 s không nhận dữ liệu, dữ liệu đến mỗi > 0,6 s (trễ), dựng ảnh bản đồ lần đầu (không tính là treo). Kiểm chứng đầu-cuối: game giả dừng 4,5 s → `[game] 4.7 s…` + `[trang] 4.4 s không nhận dữ liệu…`; trang vẫn 60 khung/s khi không có dữ liệu.
  - Bản đồ: trần trễ `liveLag` 1,5 → **3,5 s** (dữ liệu mỗi 1–2 s cần ~2,3 s; trước bị chặn 1,5 s → 10 % khung đứng hình); sắp hết dữ liệu thì **chậm dần** (`ease`) thay vì dừng khựng. Giả lập 1–2 s và 0,15 s: 0 khung đứng.
  - Kiểm chứng: đọc nối tiếp (giữ file mở) = đọc cả file (JSON, actor, thông báo); `-simulate 8192` giống hệt; exe cũ vs mới trên 3 replay: summary/CSV/data.js giống từng byte.
- Kiểm chứng tính đúng: `-simulate 1536` / `-simulate 8192` (ghi dần file ra file tạm, đọc nối tiếp) phải cho `events/positions/dinosaurs/players.csv` + `data.js` **giống hệt từng byte** với đọc cả file. Luôn chạy lại bài này sau khi sửa parser.

## 7. Cách kiểm thử đã dùng
- Chạy trên **bản copy** replay trong thư mục tạm; không ghi vào thư mục Demos thật.
- Biến môi trường thử nghiệm: `ISLE_DEMOS_DIR` (đổi thư mục Demos), `ISLE_TEST=1` (lỗi chỉ ghi log, không hiện hộp thoại).
- GUI: nạp exe bằng reflection trong PowerShell, đặt form ra ngoài màn hình, `DrawToBitmap` để chụp; **giữ form mở** khi cần máy chủ (đóng → socket bị GC).
- Bản đồ: Edge headless (`--screenshot`, `--virtual-time-budget` cho file://; `--timeout` cho http vì SSE không bao giờ "rảnh").

## 8. Lưu ý / vấn đề còn mở
- **Game xoá file replay khi khởi động lại** (đã thấy thư mục Demos trống sau khi mở lại game). Đề xuất (chưa làm): script AHK chuyển replay đã đổi tên sang thư mục riêng (vd `Documents\TheIsleReplays`) và tool liệt kê thêm thư mục đó.
- Replay chỉ mở xem được trong game (không phải video); muốn video thì OBS.
- Mở 2 bản game cùng lúc: không khuyến khích (Easy Anti-Cheat, rủi ro ban).
- Game cập nhật → định dạng / ảnh bản đồ (`gateway-v0.21.7`) có thể đổi.
- Hướng đông/tây suy từ trục Unreal (chỉ kiểm chứng hướng Nam).
- Thư mục cũ đã từng nằm trong OneDrive; người dùng sẽ chuyển ra ngoài.

## 9. Sở thích của người dùng
- Trả lời tiếng Việt, ngắn gọn, đi thẳng vào việc; muốn tối ưu hiệu năng tối đa.
- Bản đồ gọn, ít lớp; chỉ dẫn dễ đối chiếu với radar PVN trong game.
- Mỗi lần chỉ trích xuất 1 file; báo lỗi thay vì âm thầm làm khác đi.
