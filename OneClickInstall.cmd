@echo off
setlocal DisableDelayedExpansion
if not exist "%~dp0ADB_APK_Tool.exe" (
  echo ADB_APK_Tool.exe was not found. Extract the complete ZIP first.
  pause
  exit /b 1
)
start "" "%~dp0ADB_APK_Tool.exe" --one-click
