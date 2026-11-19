# 压感链路自检：OTDWintab 目录联接 → Wacom 接管 → System32 shim → 共享内存实时数据
# 用法：pwsh -File tools\check-wintab-chain.ps1 [-DataDir "D:\app\OpenTabletDriver\data"] [-Seconds 3]
#
# 为什么要查这个：shim 从 %LOCALAPPDATA%\OTDWintab\feel.json 读压力曲线，也往同一目录写诊断日志。
# 一旦这个目录是指向已删构建的“死联接”，压力放大直接失效、日志也全灭，从外部看就是“没压感”。

param(
    [string]$DataDir = 'D:\app\OpenTabletDriver\data',
    [int]$Seconds = 3
)

$ErrorActionPreference = 'Continue'
$link = Join-Path $env:LOCALAPPDATA 'OTDWintab'

function Write-Section($text) { Write-Output ''; Write-Output "=== $text ===" }

Write-Section '1. OTDWintab 目录联接'
$item = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
if ($null -eq $item) {
    Write-Output "缺失：$link"
} elseif ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    $target = $item.Target
    Write-Output "联接 -> $target"
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Output "目标不存在（死联接），重建到 $DataDir"
        try {
            [IO.Directory]::Delete($link)
            cmd /c mklink /J "$link" "$DataDir" | Out-Null
            Write-Output "已重建：$((Get-Item -LiteralPath $link -Force).Target)"
        } catch {
            Write-Output "重建失败：$($_.Exception.Message)"
        }
    } else {
        Write-Output '目标存在，正常'
    }
} else {
    Write-Output '是普通目录（不是联接），shim 与主程序可能看到两份 feel.json'
}
$feel = Join-Path $link 'feel.json'
if (Test-Path -LiteralPath $feel) {
    $cfg = Get-Content -LiteralPath $feel -Raw | ConvertFrom-Json
    Write-Output ("feel.json MaxPressure={0} DeadZone={1} Elasticity={2} InputMode={3}" -f $cfg.MaxPressure, $cfg.DeadZone, $cfg.Elasticity, $cfg.InputMode)
} else {
    Write-Output 'feel.json 不可读 → 压力放大不会生效'
}

Write-Section '2. Wacom 接管'
Get-Service | Where-Object { $_.Name -match 'wacom|wtablet' } | Select-Object Name, Status, StartType | Format-Table -AutoSize
Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'wacom|wtablet' } |
    Select-Object Name, State, StartMode | Format-Table -AutoSize
Get-PnpDevice -Class USBDevice -Status OK -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match 'VID_056A' } |
    ForEach-Object {
        [pscustomobject]@{
            Name    = $_.FriendlyName
            Service = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
            Id      = $_.InstanceId
        }
    } | Format-Table -AutoSize -Wrap
$pens = Get-PnpDevice -Status OK -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -match '手写笔|数字化器|pen|digitizer' }
Write-Output ("系统笔设备数量：{0}（0 说明板子没有走 Ink，压力只能来自 Wintab shim）" -f @($pens).Count)

Write-Section '3. System32 shim'
$sys = 'C:\Windows\System32\Wintab32.dll'
if (Test-Path -LiteralPath $sys) {
    $h = (Get-FileHash -LiteralPath $sys -Algorithm SHA256).Hash.Substring(0, 16)
    $f = Get-Item -LiteralPath $sys
    Write-Output ("{0}  {1} 字节  {2}" -f $h, $f.Length, $f.LastWriteTime)
} else {
    Write-Output 'System32 里没有 Wintab32.dll：PS/SAI 会退回系统笔'
}
$pack = Join-Path $DataDir 'Wintab32.dll'
if (Test-Path -LiteralPath $pack) {
    $hp = (Get-FileHash -LiteralPath $pack -Algorithm SHA256).Hash.Substring(0, 16)
    Write-Output ("随包 {0}  {1}" -f $hp, $pack)
}

Write-Section "4. 共享内存采样 ${Seconds}s（请用笔压住板面）"
Add-Type -TypeDefinition @'
using System;
using System.IO.MemoryMappedFiles;
public static class OtdWintabChainProbe {
  public static string Run(int ms){
    try{
      var m=MemoryMappedFile.OpenExisting(@"Local\OpenTabletDriverWintab", MemoryMappedFileRights.ReadWrite);
      var v=m.CreateViewAccessor(0,0);
      int wi=(int)v.ReadUInt32(28);
      int maxP=0, pressed=0, n=0, minX=int.MaxValue, maxX=0;
      var sw=System.Diagnostics.Stopwatch.StartNew();
      while(sw.ElapsedMilliseconds<ms){
        int cur=(int)v.ReadUInt32(28);
        for(int i=wi;i<cur;i++){
          int at=32+(i%512)*48;
          int p=v.ReadInt32(at+16), x=v.ReadInt32(at+8);
          if(p>maxP)maxP=p; if(p>0)pressed++; n++;
          if(x<minX)minX=x; if(x>maxX)maxX=x;
        }
        wi=cur; System.Threading.Thread.Sleep(5);
      }
      v.Dispose(); m.Dispose();
      return string.Format("packets={0} rate={1:0.0}/s maxP={2}/32767 ({3:0.0}%) pressed={4} x={5}..{6}",
        n, n*1000.0/ms, maxP, maxP/32767.0*100, pressed, minX, maxX);
    }catch(Exception e){ return "读取失败：" + e.Message; }
  }
}
'@
[OtdWintabChainProbe]::Run($Seconds * 1000)

Write-Section '5. 诊断日志'
Get-ChildItem -LiteralPath $link -Filter 'wintab-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 5 Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Output '判读：包率 0~1/s 或 maxP=0 → OTD 输出管线没在发；包率 >200/s 且 maxP>0 → 链路正常，问题在应用读取侧'
