@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /win32icon:LanFiles.ico /out:LanFiles.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Security.dll /r:System.Web.Extensions.dll LanFiles.cs
