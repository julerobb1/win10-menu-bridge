@echo off
rem Builds Win10MenuBridge.exe from the source next to it. Uses only the .NET Framework compiler that ships with Windows.
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:app.manifest /out:Win10MenuBridge.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Runtime.dll" /r:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" /r:"%WINDIR%\System32\WinMetadata\Windows.Media.winmd" Win10MenuBridge.cs
