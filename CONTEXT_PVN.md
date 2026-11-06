# CONTEXT — isle-overlay (TheIsleVietnam / PVN Overlay)

Ghi chú điều tra & công việc. Cập nhật: 2026-11-05.
Mục tiêu ban đầu: hiển thị người chơi khác trên map. Kết luận: **không lấy được vị trí người lạ**
từ dữ liệu hợp lệ; chi tiết ở phần 7.

---

## 1. Tổng quan dự án
- App overlay Electron cho game **The Isle**, dùng với server **TheIsleVietnam / islepilot.eu**.
- `package.json`: name `isle-overlay`, main `electron/main.cjs`, type module (nên file Electron là `.cjs`).
- Thư mục dữ liệu runtime: `%APPDATA%\pvn-isle-overlay` (đặt bằng `app.setPath` trong main.cjs).
  - settings: `%APPDATA%\pvn-isle-overlay\pvn-isle-overlay.settings.json`
- Frontend React chỉ có **bản build** trong `dist/` (không có source `src/`). Muốn sửa phần vẽ phải có repo frontend.
- Khi khởi động app tự: tắt các overlay cũ (`TheIsleOverlay.exe`, `IsleLiveMap.exe`, `IsleVN-Gacha.exe`…)
  và xoá thư mục cũ `%APPDATA%\isle-overlay`.

## 2. Môi trường dev đã dựng trên máy này
- Cài Node.js LTS (26.x) qua `winget install OpenJS.NodeJS.LTS`.
- `npm install` + `npm install --save-dev electron` (Electron 45.2.1).
- Đã thêm `allowScripts` cho `koffi` và `uiohook-napi` (npm 11 chặn install-script mặc định).
- Gói electron không tự tải binary → phải chạy `node node_modules/electron/install.js` một lần.
- Thêm script `start` vào package.json.
- **Chạy:** `cd D:\SOURCE_CODE\integrate-game` → `npm start`
  - Bật ghi log WS: `$env:ISLE_WS_LOG = "1"; npm start`

## 3. Thay đổi code đã thực hiện
- **electron/players-state.cjs** (file mới): chuẩn hóa frame `players` (nếu server gửi). Hiện **thừa**,
  vì vị trí bạn bè đã đi qua `map.markers` sẵn có. Có thể gỡ nếu muốn gọn.
- **electron/main.cjs**:
  - require + nối `createPlayersState`; xử lý nhánh `frame.t === "players"`; gửi `overlay:players`.
  - `clearLive()` xoá luôn players.
  - Ghi log WS: hàm `logWsFrame()` + env `ISLE_WS_LOG=1` → ghi `%APPDATA%\pvn-isle-overlay\ows-frames.log`
    (OPEN/OUT/IN/CLOSE, và POLL cho /me, /map). Token KHÔNG bị ghi (nằm trong header handshake).
  - Chặn báo cáo ở dev: `REPORTING_ENABLED = app.isPackaged`. Ở mode dev (chạy `electron .`),
    **tắt HUD ping và cheat-monitor** (không gửi về server/Discord). Bản đóng gói vẫn gửi bình thường.
- **electron/preload.cjs**: thêm `getPlayers()` và `onPlayers(cb)`.

## 4. Xác thực (auth) HTTP/WS
- **Bearer token** (định danh tài khoản): lấy khi đăng nhập Steam (deep link `?sid=&token=`),
  lưu trong settings, **mã hóa bằng safeStorage**. Electron 45 Windows dùng khóa AES trong file
  `Local State` của `userData` (khóa đó mới được DPAPI bảo vệ) — nên muốn giải mã phải set
  `userData = %APPDATA%\pvn-isle-overlay`. Gửi qua header `Authorization: Bearer`.
  WebSocket `/ows` dùng cùng token này ở header handshake.
- **Chữ ký HMAC** (chỉ cho `islepilot.eu/api/overlay/*`), xem `electron/overlay-sign.cjs`:
  - `X-Overlay-Client: PVN`, `X-Overlay-Ts: <ms>`, `X-Overlay-Sig: HMAC_SHA256(key, "<ts>.<METHOD>.<path+query>")`
  - key 64 hex (256-bit) ở `electron/overlay-key.generated.cjs`. Chữ ký **không gồm body**.
  - ⇒ Gọi `/api/overlay/*` cần CẢ token + HMAC. Token trần KHÔNG đủ.
- **api.theislevietnam.com/v1/ping-hud**: header `X-Api-Key` cố định (hardcode main.cjs).
- **/v1/*** (serverlist, hud_version, notifications, player-count): không token, chỉ `X-Overlay-Client`.
- **Một token = một kết nối /ows.** Kết nối mới đá kết nối cũ (close code `4000 "replaced"`).
  Không chạy 2 overlay cùng token song song.

## 5. Dữ liệu lấy được

### 5a. WebSocket `/ows` — chỉ 1 loại frame `t:"live"`, ~4 frame/giây, CHỈ dino của chính mình
Các trường trong `d`:
- Nhận dạng: `steamId`, `className` (vd `BP_Tyrannosaurus_C`), `isFemale`, `growth`, `hasDino`
- Vị trí: `position.x/y/z/yaw`
- Sinh tồn (đều có `max…`): `health`, `stamina`, `hunger`, `thirst`, `blood`, `food`, `oxygen`
- Dinh dưỡng: `nutrition.carb / protein / lipid`
- Tình trạng: `bacteria`(số), `sick`(bool), `debuffs`(mảng chuỗi, vd `["bleeding"]`), `glassBonesSec`(số giây),
  `bones.{legs,body,head}.{fractured:bool, recoverPct:số 0–100}`
- Ngoại hình: `skin.*` (RGB: body, markings, flank, underbelly, detail1, maledisplay, eyes, teeth, mouth, claws)
- Frame `troll` / `ticket` / audio binary: chỉ khi admin kích hoạt, không có khi chơi thường.

### 5b. HTTP `/api/overlay/me` — mỗi 10s, thông tin của mình (overlay-poll.cjs)
Gồm mọi thứ ở live (trừ skin RGB → thay bằng `palette` hex + `skinPattern`), cộng:
`name`, `discordId`, `playtimeSec`, `lastSeenAt`, `server`, `serverPlayers`(số người online), `online`,
`species`, `mutations`(active/parent/elderA/elderB), `prime`(eligible/elder/tiến độ/10 quest), `currencies`.

### 5c. HTTP `/api/overlay/map` — mỗi 10s, dữ liệu bản đồ
- `markers`: người chơi hiện trên map = **bạn + bạn bè/group** (mỗi marker có x/y/yaw/label/self; marker self kèm `path` 30 điểm).
- `friends`: `{locked, sameSpecies}` cấu hình hiện bạn bè.
- `pois`(170): tên, màu, shape, size, primeType, weather, effects, luật vùng (vd "No PvP").
- `categories`(7): Locations, Sanctuaries, Migration, Patrol, Water, Landmarks, Mud Pools.
- `tiles`, `calibration` (đổi tọa độ game → ảnh), `liveMapEnabled`, `allowed`.
- Hiện TẮT/RỖNG: `entities`, `ai`/`aiEnabled`, `bounties`, `bountyNearby`, `foodSpawnsEnabled`, `diet`, `plantsVersion`.

### 5d. Các API khác (giao diện gọi khi mở tab — CHƯA soi nội dung trừ combatlog)
`/api/overlay/killfeed`, `/api/overlay/combatlog`, `/quests(+claim)`, `/battlepass(+claim/premium)`,
`/shop`, `/garage(+park/slay)`, `/skin(+presets)`; `/v1/serverlist`, `/v1/player-count`, `/v1/notifications`,
`/v1/hud_version`; `/api/servers`, `/api/voice/ticket`.

### 5e. combatlog — nguồn DUY NHẤT có thông tin dino khác, nhưng chỉ đòn liên quan tới mình
- Giao diện gọi `setInterval` **2 giây**. Mỗi `hit` có: `id`, `at`, `attacker{SteamId,Name,Species}`,
  `victim{SteamId,Name,Species}`, `damage`, `hpBefore`, `hpAfter`, `distanceM`, `ability`, `killed`.
- ⚠️ Lọc "chỉ đòn của mình" chạy **ở client** (`ig()`). CẦN KIỂM TRA server có trả về toàn server không
  (nếu có → lỗ hổng, báo islepilot.eu).

### 5f. Nhúng sẵn trong bản build `dist/`
- 938 điểm thức ăn cố định, 39 loại (Gastro 221, Deer 87, Fish 70, Boar 64, SaltRock 25…).
  Lớp này bị ẩn vì server gửi `foodSpawnsEnabled:false`.

## 6. Combat & cơ chế sinh tồn (đo từ số liệu thật trên frame `/ows live`)

Lưu ý: `/ows` KHÔNG có frame "combat" riêng. Mọi diễn biến trận đánh được **suy ra từ chỉ số của chính mình**
đổi qua từng frame (~4 frame/giây). Thông tin về đối thủ (ai đánh, loài, khoảng cách) chỉ có ở **combatlog** (mục 5e),
và chỉ các đòn liên quan tới mình.

### 6a. Diễn biến một trận đánh (quan sát thực tế)
- **Trúng đòn cắn**: `health` tụt **phát một** (vd 53.85 → 49.65 ≈ sát thương đòn đó). Không có trường "damage" trong live;
  muốn biết sát thương từng đòn thì xem combatlog (`damage`, `hpBefore`, `hpAfter`).
- **Trong lúc đánh nhau**: thường kèm `debuffs=["bleeding"]` (xem 6b).
- **Chết**: `health` về `0`, rồi frame sau `hasDino:false` (hoặc respawn thành dino mới với `className` khác,
  các `max…` reset theo growth mới). Đã quan sát: T-Rex chết (hp→0) → hồi sinh thành Carnotaurus.
- **Ăn xác/con mồi**: `hunger` (và/hoặc `food`, `nutrition.*`) **tăng từng bước** (vd hunger +3 mỗi ~3.5s khi đang ăn).

### 6b. Chảy máu & thanh máu riêng (blood)
- **blood là tài nguyên riêng, có số** (`blood`/`maxBlood`) → chính là "thanh máu riêng" trong game. Vẽ theo `blood/maxBlood`.
- Khi `debuffs=["bleeding"]`: `blood` tụt **mượt, liên tục** (đo được ~0.11/giây trong 1 lần);
  `health` tụt theo **từng nấc** ~4–5 HP mỗi ~5s (giữa nấc còn hồi nhẹ). Hai thanh không trùng nhịp.
- Hết chảy máu: `debuffs` về `[]`, nhưng `blood` KHÔNG hồi ngay — ở mức thấp (vd còn 33/54) rồi **hồi dần**.
- `maxBlood` KHÔNG cố định, tăng theo `growth`; dino con thường `maxBlood == maxHealth`.
- **bleeding = cờ chuỗi** (có/không, không có số cường độ). Muốn biết chảy nhanh/chậm → tính chênh `blood` giữa frame.
- Ví dụ số liệu thật: bite lúc 11:11:34 → `["bleeding"]`; blood 54.31 → 52.11 trong ~20s (~0.11/s);
  ngừng chảy (`[]`) khi blood còn ~33/54, rồi hồi dần.

### 6c. Gãy xương & glass bones
- **Gãy xương**: mỗi bộ phận `legs/body/head` có `fractured`(bool) + `recoverPct`(số 0→100 hồi dần).
- **glassBones** (giòn xương sau respawn): `glassBonesSec`(số giây, đếm ngược về 0).
- Chưa bắt được giá trị lúc gãy thật (mới thấy `ok/100` và `glass 0s`) — không demo được gãy xương.

## 7. KHÔNG lấy được
- Vị trí/chỉ số **người lạ**: server không gửi. Chỉ có bạn bè/group trong `map.markers` (10s/lần).
  Lúc kiểm tra `markers` chỉ có mình (không có ai cùng group online).
- Xác, AI, con mồi, thức ăn realtime: server đang tắt.
- Muốn có → chỉ còn đọc bộ nhớ game / chặn traffic game = cách hack, dễ bị EAC ban,
  và đúng loại công cụ mà `cheat-monitor.cjs` của chính app đang bắt. **Không làm.**
- Nếu server muốn: nhờ islepilot.eu (plugin IslePilot) thêm dữ liệu vào `/map` hoặc gửi WS. Overlay gần như không cần sửa.

## 8. Công cụ debug (đã copy vào `debug-tools/` để khỏi mất khi temp bị xoá)
Chạy bằng Electron của repo. Trước khi chạy: tắt PVN-Overlay thật (vì 1 token = 1 kết nối /ows).
```
$e = "node_modules\electron\dist\electron.exe"
& $e debug-tools\isle-req.cjs /api/overlay/me          # gửi request đã ký HMAC+Bearer, in status+body (không in token)
& $e debug-tools\isle-req.cjs /api/overlay/combatlog
& $e debug-tools\isle-ows.cjs 20000                    # tap /ows 20s, in loại frame + 1 mẫu mỗi loại
& $e debug-tools\isle-vitals.cjs 12000                 # in sinh tồn/dinh dưỡng/tình trạng từng frame
& $e debug-tools\isle-bleed.cjs 60000                  # chỉ in khi health/blood/debuffs/bones đổi, tự reconnect
```
Các script tự giải mã token local + tự ký HMAC; **không in token ra**.

## 9. Cảnh báo bảo mật (nên xử lý phía server)
- Discord webhook URL hardcode trong `cheat-monitor.cjs` → ai giải nén app cũng spam/giả mạo được. Nên đổi + đẩy qua API server.
- `X-Api-Key` và HMAC key đều nằm trong app phát hành → không chứng minh được "overlay chính thức". Lớp bảo vệ thật chỉ là Bearer token theo tài khoản.
- `cheat-monitor.cjs`: mục `onefile_*\app.dll` (Nuitka) dễ báo nhầm nhưng bị xếp màu đỏ "phát hiện hack";
  file/thư mục sót lại vẫn báo đỏ mỗi 5 phút; vẫn quét cả khi chưa đăng nhập.
