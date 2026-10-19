# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
    MiTVSwitch 命令行版 —— 小米 / 红米电视与智能显示器信号源切换

.DESCRIPTION
    不需要编译，直接用 PowerShell 调用设备的 MiTV Assistant 接口。

    地址来源（优先级从高到低）：
      1. 环境变量 MITV_IP
      2. 配置文件 %APPDATA%\MiTVSwitch\host.txt（托盘程序也写这里）

    切源优先走 changesource2（按编号），它能切到 DP / USB-C；
    设备不支持时自动退回按名字的 changesource。

.EXAMPLE
    .\switch.ps1 hdmi1
    .\switch.ps1 dp
    .\switch.ps1 sources
    .\switch.ps1 id dp
    .\switch.ps1 vol+
    .\switch.ps1 comp on
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
    Write-Host "  方式一：运行托盘程序，右键图标 → 设置显示器地址"
    Write-Host "  方式二：新建文件 $conf ，内容写设备 IP"
    Write-Host "  方式三：`$env:MITV_IP = '192.168.1.100'"
    Write-Host ""
    exit 1
}

$root = "http://${ip}:6095"

function Invoke-Tv {
    param([string]$Path)

    $wc = New-Object System.Net.WebClient
    # 关键：置空代理。Windows 的系统代理经常把发往局域网的请求也劫走，
    # 表现就是「公网能通、设备连不上」。
    $wc.Proxy = $null
    try {
        return $wc.DownloadString($root + $Path)
    }
    catch {
        throw "请求失败：$($_.Exception.Message)"
    }
}

function Get-Sources {
    $resp = Invoke-Tv "/controller?action=getsources"
    if ($resp -notmatch '"success"') { return @() }

    $list = @()
    foreach ($m in [regex]::Matches($resp, '"sourceId"\s*:\s*(\d+)\s*,\s*"sourceName"\s*:\s*"([^"]*)"')) {
        $id = [int]$m.Groups[1].Value
        $name = $m.Groups[2].Value
        # 老固件会把 29 报成 DVI（MStar 电视输入表的遗留），按编号纠正
        if ($id -eq 29) { $name = "DP" }
        elseif ($id -eq 30) { $name = "USBC" }
        $list += [pscustomobject]@{ Id = $id; Name = $name }
    }
    return $list
}

function Resolve-SourceId {
    param([string]$Key, $List)

    $want = ($Key.ToUpper() -replace '[^A-Z0-9]', '')
    if ($List.Count -eq 0) { return $null }

    if ($want -eq "DP" -or $want -eq "DP1") {
        if ($List | Where-Object { $_.Id -eq 29 }) { return 29 }
    }
    if ($want -eq "USBC" -or $want -eq "TYPEC") {
        if ($List | Where-Object { $_.Id -eq 30 }) { return 30 }
    }

    $exact = $List | Where-Object { ($_.Name.ToUpper() -replace '[^A-Z0-9]', '') -eq $want } | Select-Object -First 1
    if ($exact) { return $exact.Id }

    $loose = $List | Where-Object { ($_.Name.ToUpper() -replace '[^A-Z0-9]', '').StartsWith($want) } | Select-Object -First 1
    if ($loose) { return $loose.Id }

    return $null
}

function Invoke-Switch {
    param([string]$Key, [string]$Label)

    $list = Get-Sources
    $id = Resolve-SourceId $Key $list

    if ($null -ne $id) {
        $resp = Invoke-Tv "/controller?action=changesource2&sourceid=$id"
        if ($resp -match '"success"') {
            Write-Host "OK  $Label（按编号 $id）" -ForegroundColor Green
            exit 0
        }
    }

    $resp = Invoke-Tv "/controller?action=changesource&source=$Key"
    if ($resp -match '"success"') {
        Write-Host "OK  $Label（按名字）" -ForegroundColor Green
        exit 0
    }

    if ($list.Count -gt 0) {
        $avail = ($list | ForEach-Object { $_.Name }) -join "、"
        Write-Host "FAIL  $Label —— 设备没有这个输入，当前可用：$avail" -ForegroundColor Red
    }
    else {
        Write-Host "FAIL  $Label  $resp" -ForegroundColor Red
    }
    exit 1
}

function Invoke-Key {
    param([string]$Key)
    $null = Invoke-Tv "/controller?action=keyevent&keycode=$Key"
}

function Invoke-StepVolume {
    param([bool]$Up)
    $key = if ($Up) { "volumeup" } else { "volumedown" }
    for ($i = 0; $i -lt 2; $i++) {
        Invoke-Key $key
        Start-Sleep -Milliseconds 60
    }
    Write-Host "OK  音量 $(if ($Up) { '+' } else { '−' })" -ForegroundColor Green
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
        Invoke-Switch $Source "已切到 $Source"
    }
    "sources" {
        $list = Get-Sources
        if ($list.Count -eq 0) {
            Write-Host "设备没有返回信号源列表。" -ForegroundColor Yellow
            exit 1
        }
        Write-Host "信号源列表："
        foreach ($s in $list) { Write-Host ("  {0,-8} 编号 {1}" -f $s.Name, $s.Id) }
    }
    "id" {
        if (-not $Source) { Write-Host "用法: .\switch.ps1 id <源名>（如 dp / hdmi1 / usbc）" ; exit 1 }
        $id = Resolve-SourceId $Source (Get-Sources)
        if ($null -ne $id) { Write-Host "$($Source.ToUpper()) → 编号 $id" }
        else {
            Write-Host "$($Source.ToUpper()) → 设备没有这个输入" -ForegroundColor Yellow
            exit 1
        }
    }
    "status" {
        Write-Host "── isalive ──"
        Invoke-Tv "/request?action=isalive"
        Write-Host ""
        Write-Host "── 信号源 ──"
        Invoke-Tv "/controller?action=getsources"
        Write-Host ""
        Write-Host "── 设备信息 ──"
        Invoke-Tv "/controller?action=getsysteminfo"
    }
    "vol"    { Invoke-Tv "/controller?action=getvolume" }
    "vol+"   { Invoke-StepVolume $true }
    "vup"    { Invoke-StepVolume $true }
    "vol-"   { Invoke-StepVolume $false }
    "vdn"    { Invoke-StepVolume $false }
    "comp" {
        $on = $Source
        if ($on -ne "on" -and $on -ne "off") {
            Write-Host "用法: .\switch.ps1 comp on|off" ; exit 1
        }
        # 没有对应接口，只能走按键宏：
        # menu 打开游戏栏 → 右移 5 次到模式项 → enter 进入 → right 开 / left 关
        Write-Host "切换中，显示器会黑一下并换 EDID…"
        Invoke-Key "menu";  Start-Sleep -Milliseconds 700
        foreach ($i in 1..5) { Invoke-Key "right"; Start-Sleep -Milliseconds 100 }
        Invoke-Key "enter"; Start-Sleep -Milliseconds 300
        if ($on -eq "on") { Invoke-Key "right" } else { Invoke-Key "left" }
        Invoke-Key "back"
        Invoke-Key "back"
        Write-Host "OK  竞技模式 320Hz 已$(if ($on -eq 'on') { '开启' } else { '关闭' })" -ForegroundColor Green
    }
    default {
        Write-Host "用法: .\switch.ps1 {hdmi1|hdmi2|hdmi3|dp|usbc|raw <源名>|sources|id <源名>|status|vol|vol+|vol-|comp on|off}"
        exit 1
    }
}
