@echo off
setlocal
rem ============================================================
rem  CapNhat.bat - cap nhat Dino Replay Reader len ban moi nhat
rem  Nhap dup la xong. Giu lai: diem soi tu ghi, cai dat, cookie.
rem  Tool tu goi file nay (che do __auto) moi lan mo: cap nhat xong
rem  thi tu mo lai tool. Loi / mat mang -> van mo ban dang co.
rem ============================================================

rem Chay ban sao trong thu muc TEMP (file nay co the bi thay trong luc cap nhat)
if /i not "%~1"=="__run" (
    copy /y "%~f0" "%TEMP%\dino_capnhat_run.bat" >nul
    call "%TEMP%\dino_capnhat_run.bat" __run "%~dp0" "%~1"
    exit /b
)

cd /d "%~2"
set FAIL=
set AUTO=
if /i "%~3"=="__auto" set AUTO=1
title Cap nhat Dino Replay Reader
echo.
echo  === CAP NHAT DINO REPLAY READER ===
echo  Thu muc: %CD%
echo.

where git >nul 2>&1
if errorlevel 1 (
    echo  [LOI] May chua cai Git. Tai o https://git-scm.com/download/win roi chay lai.
    set FAIL=1
    goto :end
)
git rev-parse --git-dir >nul 2>&1
if errorlevel 1 (
    echo  [LOI] Thu muc nay khong phai thu muc tai bang git clone.
    set FAIL=1
    goto :end
)

rem May lap trinh (sua ma nguon chua commit): khong cap nhat de khoi mat code
if defined AUTO (
    for /f "delims=" %%S in ('git status --porcelain -- src') do set DEVCHG=1
)
if defined DEVCHG (
    echo  Co thay doi chua commit trong src - bo qua cap nhat.
    goto :end
)

:checktool
tasklist /fi "imagename eq DinoReplayReader.exe" 2>nul | find /i "DinoReplayReader.exe" >nul
if not errorlevel 1 (
    if defined AUTO (
        rem tool vua tu thoat de cap nhat: doi no tat han
        ping -n 2 127.0.0.1 >nul
    ) else (
        echo  Dino Replay Reader dang mo. Hay TAT tool, roi bam phim bat ky de tiep tuc...
        pause >nul
    )
    goto :checktool
)

rem Sao luu file rieng cua may nay
set "BK=%TEMP%\dino_backup"
if exist "%BK%" rd /s /q "%BK%"
mkdir "%BK%"
for %%F in (gastro_learned.txt map_layers.txt) do (
    if exist "%%F" copy /y "%%F" "%BK%\%%F" >nul
)

echo  Dang tai ban moi...
git fetch origin
if errorlevel 1 (
    echo.
    echo  [LOI] Khong tai duoc. Kiem tra mang / tai khoan GitHub roi chay lai.
    set FAIL=1
    goto :end
)

rem Bo moi thay doi tren may (chi file cua tool, KHONG dong vao settings, cookie, output)
git reset --hard @{u} >nul 2>&1
if errorlevel 1 git reset --hard origin/master
if errorlevel 1 (
    set FAIL=1
)

:restore
for %%F in (gastro_learned.txt map_layers.txt) do (
    if exist "%BK%\%%F" copy /y "%BK%\%%F" "%%F" >nul
)

if defined FAIL (
    echo  [LOI] Khong cap nhat duoc. Chup man hinh nay gui cho nguoi gui tool.
    goto :end
)
echo.
echo  XONG! Ban hien tai:
git log -1 --format="   %%h  %%ad  %%s" --date=format:"%%d/%%m/%%Y %%H:%%M"

:end
echo.
if defined AUTO (
    if defined FAIL (
        echo  Mo ban dang co sau 5 giay...
        ping -n 6 127.0.0.1 >nul
    )
    start "" "%~2DinoReplayReader.exe" -noupdate
    exit /b
)
if not defined FAIL echo  Mo lai DinoReplayReader.exe de dung ban moi.
pause
