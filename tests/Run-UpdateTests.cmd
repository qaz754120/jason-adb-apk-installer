@echo off
rem Copyright (c) 2026 Jason1993余. See LICENSE.
setlocal DisableDelayedExpansion
set "UPDATE_TEST_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%UPDATE_TEST_CSC%" set "UPDATE_TEST_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%~dp0..\ADB_APK_Tool.exe" goto missingbuild
if not exist "%~dp0..\JasonUpdater.exe" goto missingbuild
if not exist "%~dp0..\obj" mkdir "%~dp0..\obj"
"%UPDATE_TEST_CSC%" /nologo /target:exe /optimize+ /utf8output /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Runtime.Serialization.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /out:"%~dp0..\obj\UpdateCoreTests.exe" "%~dp0..\src\UpdateTrust.cs" "%~dp0..\src\Updates.cs" "%~dp0UpdateCoreTests.cs"
if errorlevel 1 exit /b 1
"%~dp0..\obj\UpdateCoreTests.exe" "%~dp0..\ADB_APK_Tool.exe" "%~dp0..\JasonUpdater.exe"
exit /b %errorlevel%
:missingbuild
echo Run Build.cmd from the project root first.
exit /b 1
