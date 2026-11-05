@echo off
setlocal
rem ============================================================
rem  CapNhat.bat - cap nhat Dino Replay Reader len ban moi nhat
rem  Nhap dup la xong. Giu lai: diem soi tu ghi, cai dat, cookie.
rem ============================================================

rem Chay ban sao trong thu muc TEMP (file nay co the bi thay trong luc cap nhat)
if /i not "%~1"=="__run" (
    copy /y "%~f0" "%TEMP%\dino_capnhat_run.bat" >nul
    call "%TEMP%\dino_capnhat_run.bat" __run "%~dp0"
    exit /b
)

cd /d "%~2"
set FAIL=
title Cap nhat Dino Replay Reader
echo.
echo  === CAP NHAT DINO REPLAY READER ===
echo  Thu muc: %CD%
echo.

where git >nul 2>&1
if errorlevel 1 (
    echo  [LOI] May chua cai Git. Tai o https://git-scm.com/download/win roi chay lai.
    goto :end
)
git rev-parse --git-dir >nul 2>&1
if errorlevel 1 (
    echo  [LOI] Thu muc nay khong phai thu muc tai bang git clone.
    goto :end
)

:checktool
tasklist /fi "imagename eq DinoReplayReader.exe" 2>nul | find /i "DinoReplayReader.exe" >nul
if not errorlevel 1 (
    echo  Dino Replay Reader dang mo. Hay TAT tool, roi bam phim bat ky de tiep tuc...
    pause >nul
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
echo.
echo  Mo lai DinoReplayReader.exe de dung ban moi.

:end
echo.
pause