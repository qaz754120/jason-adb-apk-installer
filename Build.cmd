@echo off
setlocal DisableDelayedExpansion
set "ADBTOOL_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%ADBTOOL_CSC%" set "ADBTOOL_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%ADBTOOL_CSC%" (
  echo Microsoft .NET Framework C# compiler was not found.
  echo Use the prebuilt ADB_APK_Tool.exe or enable .NET Framework via Windows support.
  pause
  exit /b 1
)
"%ADBTOOL_CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 /win32manifest:"%~dp0src\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /out:"%~dp0ADB_APK_Tool.exe" "%~dp0src\Core.cs" "%~dp0src\Program.cs"
if errorlevel 1 (
  echo Build failed. See the errors above.
  pause
  exit /b 1
)
echo Build complete: ADB_APK_Tool.exe
pause
