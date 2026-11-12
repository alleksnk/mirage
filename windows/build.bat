@echo off
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF_DIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF

echo Compiling Mirage...
"%CSC%" /target:winexe /out:Mirage.exe /win32icon:config\mirage.ico /win32manifest:app.manifest /lib:"%WPF_DIR%" /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\RuntimeLogger.cs src\ProxyHelper.cs src\Settings.cs src\HostsHelper.cs src\SmartProxyServer.cs src\EngineProcess.cs src\Diagnostics.cs src\ActivityMonitor.cs src\Program.cs

if %ERRORLEVEL% EQU 0 (
    echo [SUCCESS] Mirage.exe built successfully.
) else (
    echo [ERROR] Compilation failed.
)
