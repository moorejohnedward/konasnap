@echo off
title Build KonaSnap
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" goto nocompiler

rem Windows UI Automation libraries (used to find panels inside windows and read their text)
call :find UIAutomationClient UIAC
call :find UIAutomationTypes UIAT
call :find WindowsBase WBASE
if not defined UIAC goto nolibs
if not defined UIAT goto nolibs
if not defined WBASE goto nolibs

rem Close a running copy so the new one can replace it (skipped on GitHub's build servers)
if not defined CI (
  taskkill /im KonaSnap.exe /f >nul 2>&1
  timeout /t 1 >nul
)

echo Building KonaSnap...
"%CSC%" /nologo /target:winexe /optimize+ /out:KonaSnap.exe /win32icon:KonaSnap.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.dll /r:"%UIAC%" /r:"%UIAT%" /r:"%WBASE%" KonaSnap.cs
if errorlevel 1 goto failed

if defined CI exit /b 0
echo.
echo Built KonaSnap.exe. Starting it now...
start "" "%~dp0KonaSnap.exe"
timeout /t 3 >nul
exit /b 0

:find
set "%2="
if exist "%FW%\WPF\%1.dll" (
  set "%2=%FW%\WPF\%1.dll"
  goto :eof
)
for /d %%D in ("%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\%1\v4.0_*") do (
  if exist "%%~D\%1.dll" set "%2=%%~D\%1.dll"
)
goto :eof

:nocompiler
echo Could not find the C# compiler that comes with Windows, .NET Framework 4.
if not defined CI pause
exit /b 1

:nolibs
echo Could not find the Windows UI Automation libraries (UIAutomationClient, UIAutomationTypes, WindowsBase).
echo They come with .NET Framework 4. Copy this message to Claude.
if not defined CI pause
exit /b 1

:failed
echo.
echo Build failed. Copy the messages above to Claude.
if not defined CI pause
exit /b 1
