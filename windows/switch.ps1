# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
    MiTVSwitch 命令行版 —— 小米 / 红米电视与智能显示器信号源切换

.DESCRIPTION
    不需要编译，直接用 PowerShell 调用设备的 MiTV Assistant 接口。

    地址来源（优先级从高到低）：
      1. 环境变量 MITV_IP
      2. 配置文件 %APPDATA%\MiTVSwitch\host.txt（菜单栏程序也写这里）

.EXAMPLE
    .\switch.ps1 hdmi1
    .\switch.ps1 raw HDMI3
    .\switch.ps1 status
#>
param(
    [Parameter(Position = 0)][string]$Action = "status",
    [Parameter(Position = 1)][string]$Source
)

$ErrorActionPreference = "Stop"

$conf = Join-Path $env:APPDATA "MiTVSwitch\host.txt"

if ($env:MITV_IP) {
    $ip = $env:MITV_IP.Trim()
}
elseif (Test-Path $conf) {
    $ip = (Get-Content $conf -Raw).Trim()
}
else {
    $ip = ""
}

if ([string]::IsNullOrWhiteSpace($ip)) {
    Write-Host "未设置设备地址。" -ForegroundColor Red
    Write-Host ""
    Write-Host "  方式一：运行菜单栏程序，右键托盘图标 → 设置显示器地址"
    Write-Host "  方式二：新建文件 $conf ，内容写设备 IP"
    Write-Host "  方式三：`$env:MITV_IP = '192.168.1.100'"
    Write-Host ""
    exit 1
}

$base = "http://${ip}:6095/controller?action="

function Invoke-TvApi {
    param([string]$Query)

    $wc = New-Object System.Net.WebClient
    # 关键：置空代理。Windows 的系统代理经常把发往局域网的请求也劫走，
    # 表现就是「公网能通、设备连不上」。
    $wc.Proxy = $null
    try {
        return $wc.DownloadString($base + $Query)
    }
    catch {
        throw "请求失败：$($_.Exception.Message)"
    }
}

function Invoke-Switch {
    param([string]$Key, [string]$Label)

    $resp = Invoke-TvApi "changesource&source=$Key"
    if ($resp -match '"success"') {
        Write-Host "OK  $Label" -ForegroundColor Green
        exit 0
    }
    Write-Host "FAIL  $Label  $resp" -ForegroundColor Red
    exit 1
}

switch ($Action.ToLower()) {
    "hdmi1"  { Invoke-Switch "HDMI1" "已切到 HDMI 1" }
    "1"      { Invoke-Switch "HDMI1" "已切到 HDMI 1" }
    "hdmi2"  { Invoke-Switch "HDMI2" "已切到 HDMI 2" }
    "2"      { Invoke-Switch "HDMI2" "已切到 HDMI 2" }
    "hdmi3"  { Invoke-Switch "HDMI3" "已切到 HDMI 3" }
    "3"      { Invoke-Switch "HDMI3" "已切到 HDMI 3" }
    "dp"     { Invoke-Switch "DP"    "已切到 DisplayPort" }
    "usbc"   { Invoke-Switch "USBC"  "已切到 USB-C" }
    "raw" {
        if (-not $Source) { Write-Host "用法: .\switch.ps1 raw <源名>" ; exit 1 }
        Invoke-Switch $Source "已发送源名 $Source"
    }
    "status" { Invoke-TvApi "getsysteminfo" }
    "vol"    { Invoke-TvApi "getvolume" }
    default {
        Write-Host "用法: .\switch.ps1 {hdmi1|hdmi2|hdmi3|dp|usbc|raw <源名>|status|vol}"
        exit 1
    }
}
