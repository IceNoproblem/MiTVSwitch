@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo 未找到 dotnet 命令。
    echo 请先安装 .NET 8 SDK: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

echo.
echo [1/2] 正在编译（首次会联网下载依赖，需要几分钟）...
echo.
dotnet publish MiTVSwitch.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o ..\dist\win-x64

if errorlevel 1 (
    echo.
    echo 编译失败，请检查上面的错误信息。
    pause
    exit /b 1
)

echo.
echo [2/2] 完成！
echo.
echo 可执行文件: %~dp0..\dist\win-x64\MiTVSwitch.exe
echo 这是自包含单文件版本，拷到任何 Windows 10/11 x64 上都能直接双击运行，
echo 目标机器不需要安装 .NET 运行时。
echo.
pause
