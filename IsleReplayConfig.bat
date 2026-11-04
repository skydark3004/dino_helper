<# : phan batch - goi PowerShell chay phan con lai cua chinh file nay
@echo off
set "DINO_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ((Get-Content -LiteralPath '%~f0' -Raw -Encoding UTF8))" & exit /b
#>

# ===== Cấu hình replay cho The Isle =====
# Đổi Engine.ini giữa các bộ thông số replay, rồi (tùy chọn) mở game qua Steam.

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Ini      = "$env:LOCALAPPDATA\TheIsle\Saved\Config\WindowsClient\Engine.ini"
$AppId    = 376210
$GameExes = 'TheIsleClient-Win64-Shipping', 'TheIsle'
# cài đặt của Dino Replay Reader (cùng thư mục với file .bat này và DinoReplayReader.exe)
$Settings = Join-Path $env:DINO_DIR 'settings.txt'

function Get-PerfLog {
    if (Test-Path $Settings) { foreach ($l in Get-Content $Settings) { if ($l -match '^\s*perflog\s*=\s*1\s*$') { return $true } } }
    return $false
}

function Set-PerfLog([bool]$On) {
    # giữ nguyên các dòng cài đặt khác của tool (vd autoupdatecheck=...)
    $lines = @()
    if (Test-Path $Settings) { $lines = @(Get-Content $Settings | Where-Object { $_ -notmatch '^\s*perflog\s*=' }) }
    $lines += 'perflog=' + $(if ($On) { '1' } else { '0' })
    Set-Content $Settings $lines -Encoding ascii
    if ($On) {
        Write-Host "`nĐã BẬT ghi log chỗ chậm -> perf_log.txt (cạnh DinoReplayReader.exe)." -ForegroundColor Green
        Write-Host "Tool đang chạy cũng tự áp dụng sau ~1 giây. Gửi file perf_log.txt cho người hỗ trợ khi bị giật."
    } else {
        Write-Host "`nĐã TẮT ghi log chỗ chậm." -ForegroundColor Green
    }
}

function Show-Current {
    Write-Host "`n--- Engine.ini hiện tại ---" -ForegroundColor Cyan
    if (Test-Path $Ini) {
        Get-Content $Ini | ForEach-Object { Write-Host "  $_" }
        Write-Host ("  (Read-only: {0})" -f (Get-Item $Ini).IsReadOnly)
    } else {
        Write-Host "  Chưa có file (game sẽ tự tạo khi mở)."
    }
    Write-Host ("`n--- Ghi log chỗ chậm (Dino Replay Reader): {0} ---" -f $(if (Get-PerfLog) { 'BẬT' } else { 'TẮT' })) -ForegroundColor Cyan
}

function Apply-Profile([string[]]$CVars, [bool]$Lock) {
    if (Get-Process -Name $GameExes -ErrorAction SilentlyContinue) {
        Write-Host "`nGame đang chạy. Tắt game rồi chạy lại script." -ForegroundColor Red
        return $false
    }
    $text = ''
    if (Test-Path $Ini) {
        Set-ItemProperty $Ini IsReadOnly $false
        $text = Get-Content $Ini -Raw
    }
    # Bỏ mục [ConsoleVariables] cũ, giữ nguyên các mục khác (ví dụ CachedClientID)
    $text = [regex]::Replace("$text", '(?ms)^\[ConsoleVariables\]\r?\n.*?(?=^\[|\z)', '').TrimEnd()
    if ($CVars.Count -gt 0) {
        $text += "`r`n`r`n[ConsoleVariables]`r`n" + ($CVars -join "`r`n")
    }
    Set-Content $Ini ($text.TrimStart() + "`r`n") -NoNewline -Encoding ascii
    # Khóa file để game không xóa cấu hình khi thoát
    if ($Lock) { Set-ItemProperty $Ini IsReadOnly $true }
    Write-Host "`nĐã áp dụng." -ForegroundColor Green
    Show-Current
    return $true
}

function Read-Number([string]$Prompt, [string]$Default) {
    while ($true) {
        $v = Read-Host "$Prompt [Enter = $Default]"
        if ([string]::IsNullOrWhiteSpace($v)) { return $Default }
        $v = $v.Replace(',', '.')
        $n = 0.0
        if ([double]::TryParse($v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$n) -and $n -ge 0) { return $v }
        Write-Host "  Nhập một số >= 0, ví dụ 0.5" -ForegroundColor Yellow
    }
}

while ($true) {
    Write-Host ""
    Write-Host "===== The Isle - cấu hình replay (F2) =====" -ForegroundColor Cyan
    Write-Host "  1. Mặc định  - thông số gốc của game, file tên demoN"
    Write-Host "  2. Nhanh     - ghi chunk 0.125s, nạp chunk 0.1s, file tên fastN, giữ 100 replay"
    Write-Host "  3. Tùy chỉnh - tự nhập thông số (giữ 100 replay)"
    Write-Host "  4. Xem cấu hình hiện tại"
    Write-Host ("  5. Bật/tắt ghi log chỗ chậm (perf_log.txt) - hiện: {0}" -f $(if (Get-PerfLog) { 'BẬT' } else { 'TẮT' }))
    Write-Host "  0. Thoát"
    $choice = Read-Host "Chọn"

    $ok = $null
    switch ($choice) {
        '5' { Set-PerfLog (-not (Get-PerfLog)) } # không cần tắt game, không hỏi mở game
        '1' { $ok = Apply-Profile @() $false }
        '2' {
            $ok = Apply-Profile @(
                'localReplay.ChunkUploadDelayInSeconds=0.125',
                'localReplay.MinLoadNextChunkDelaySeconds=0.1',
                'demo.ReplayStreamerAutoDemoPrefix=fast',
                'demo.MaxNumberOfAutoReplays=100'
            ) $true
        }
        '3' {
            $chunk  = Read-Number 'Bao lâu ghi một chunk xuống file (giây; bản đồ trực tiếp cập nhật theo nhịp này)' '0.125'
            $load   = Read-Number 'Khoảng cách nạp chunk khi xem lại (giây)' '0.1'
            $prefix = Read-Host 'Tiền tố tên file [Enter = custom]'
            if ([string]::IsNullOrWhiteSpace($prefix)) { $prefix = 'custom' }
            $prefix = $prefix -replace '[^A-Za-z0-9_-]', ''
            $ok = Apply-Profile @(
                "localReplay.ChunkUploadDelayInSeconds=$chunk",
                "localReplay.MinLoadNextChunkDelaySeconds=$load",
                "demo.ReplayStreamerAutoDemoPrefix=$prefix",
                'demo.MaxNumberOfAutoReplays=100'
            ) $true
        }
        '4' { Show-Current }
        '0' { exit }
        default { Write-Host "Lựa chọn không hợp lệ." -ForegroundColor Yellow }
    }

    if ($ok -eq $true) {
        $run = Read-Host "`nMở game luôn? (Y/N)"
        if ($run -match '^[Yy]') {
            Start-Process "steam://rungameid/$AppId"
            exit
        }
    }
}
