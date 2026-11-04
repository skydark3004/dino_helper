@echo off
chcp 65001 >nul
rem Kéo thả file .replay vào file này để trích xuất dữ liệu.
rem Kết quả nằm trong thư mục output\<tên replay>
if "%~1"=="" (
  echo Keo tha file .replay vao file nay.
  start "" "%LOCALAPPDATA%\TheIsle\Saved\Demos"
  pause
  exit /b
)
"%~dp0DinoReplayReader.exe" "%~1"
start "" "%~dp0output\%~n1"
if exist "%~dp0output\%~n1\map.html" start "" "%~dp0output\%~n1\map.html"
pause
