@echo off
rem Copyright (c) 2026 Jason1993余. See LICENSE.
setlocal DisableDelayedExpansion
set "ADBTOOL_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%ADBTOOL_CSC%" set "ADBTOOL_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%ADBTOOL_CSC%" (
  echo Microsoft .NET Framework C# compiler was not found.
  echo Use the prebuilt EXE files or install a supported .NET Framework runtime.
  pause
  exit /b 1
)
"%ADBTOOL_CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 /win32manifest:"%~dp0src\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /out:"%~dp0ADB_APK_Tool.exe" "%~dp0src\Core.cs" "%~dp0src\Program.cs" "%~dp0src\UpdateUi.cs" "%~dp0src\Updates.cs" "%~dp0src\UpdateTrust.cs"
if errorlevel 1 goto buildfailed
"%ADBTOOL_CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 /win32manifest:"%~dp0src\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /out:"%~dp0JasonUpdater.exe" "%~dp0src\UpdaterProgram.cs" "%~dp0src\Updates.cs" "%~dp0src\UpdateTrust.cs"
if errorlevel 1 goto buildfailed
echo Build complete: ADB_APK_Tool.exe and JasonUpdater.exe
pause
exit /b 0
:buildfailed
echo Build failed. See the errors above.
pause
exit /b 1
