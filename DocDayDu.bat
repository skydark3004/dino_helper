@echo off
chcp 65001 >nul
rem Giống DocNhanh.bat nhưng xuất thêm dữ liệu thô (positions.csv, events.csv - rất nặng).
if "%~1"=="" (
  echo Keo tha file .replay vao file nay.
  pause
  exit /b
)
"%~dp0DinoReplayReader.exe" "%~1" -full
start "" "%~dp0output\%~n1"
pause
