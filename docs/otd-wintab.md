# OpenTabletDriver Wintab — Wacom PTH-660 免驱压感方案

> **上游项目**：[OpenTabletDriver / OpenTabletDriver](https://github.com/OpenTabletDriver/OpenTabletDriver)
> 本仓库是上游 0.6.x 的一个定制分支，只针对 Windows + Wacom PTH-660 增加了 Wintab 输出链路和手感面板。
> 上游按 **LGPL-3.0** 发布，本仓库的修改同样遵循 LGPL-3.0，见 [LICENSE](LICENSE)。

## 这个仓库解决什么问题

上游 OTD 是免驱的：它直接从 HID 读板子数据，用 Windows 指针 API 移动光标。但 Windows 的指针 API 没有「压力」字段，所以 **不装任何东西时，板子能画、能点，却没有压感**。

上游的官方建议是用社区插件 `Windows Ink`，它依赖 **VMulti 虚拟设备驱动**（内核驱动，需要安装、可能要重启）。

这里走另一条路：**一个自写的、用户态的 `Wintab32.dll` 中间层**。

```
OTD daemon
   └─ 插件 OpenTabletDriver.Wintab（OutputMode）
        └─ 共享内存（Local\OpenTabletDriverWintab）
             └─ Wintab32.dll（shim，放进 C:\Windows\System32）
                  └─ WinTab 应用（Photoshop CC 2019、ProCraft…）
```

结果是：不装任何内核驱动，Photoshop / ProCraft 能拿到 **0–32767 的真实压感**，并且压力曲线、死区、满压放大都在中间层里调。

## 目录

| 路径 | 内容 |
|---|---|
| `plugin/` | OTD 输出模式插件 `OpenTabletDriver.Wintab` 源码（`Wintab (No Driver)`） |
| `shim/` | `Wintab32.dll` 的 Rust 源码（WinTab API 实现 + 共享内存消费 + 手感处理） |
| `ux-patch/` | 改过的上游文件：手感面板、模式开关、日志降噪、插件防卸载等 |
| `bin/` | 现成二进制：`Wintab32.dll`（x64, shim）与 `OpenTabletDriver.Wintab.dll`（插件） |
| `docs/wintab-mode-notes.md` | 调试与踩坑记录（含映射、丢焦、日志爆炸、崩溃定位） |

## 关键改动

- **输出模式插件**：把绝对坐标和压力写进共享内存，指针/点击仍交给 OTD 自己的平台指针。
- **Wintab32 shim**：实现 WinTab API（`WTOpen/WTPackets/WTInfo/...`），从共享内存取最新包；笔离开感应区会被强制判离场（解决「切窗口回来只能点出一个点」）；新样本到达即覆盖旧包，不积压。
- **手感面板**（并入 OTD 第一个分组，不是独立窗口）：压力死区、弹性（压力曲线）、笔触稳定、压力校准（松手前峰值 → 等比放大到 100%）。值存在 `%LOCALAPPDATA%\OTDWintab\feel.json`。
- **单一模式开关**：窗口底部「模式」下拉，`Windows Ink` / `WinTab`；选 WinTab 时才检查并提示把 `Wintab32.dll` 装进 System32。上游「输出」页那个下拉已移除，避免两处切换。
- **窗口行为**：点关闭最小化到托盘，真退出走托盘/菜单的 `Quit`。
- **日志只留 Warning 及以上**，写入改成有界队列（曾因无界队列把 daemon 撑到 14.7 GB）。
- **内置插件不可卸载**：`OpenTabletDriver.Wintab` 在 daemon 层和 UI 层都被锁住——删掉它就没有压感了。

## 构建

需要 .NET SDK 8、Rust（stable）。

```powershell
# 插件 + 界面（在 OpenTabletDriver 源码树根目录）
dotnet build OpenTabletDriver.Wintab\OpenTabletDriver.Wintab.csproj -c Release
dotnet publish OpenTabletDriver.UX.Wpf\OpenTabletDriver.UX.Wpf.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o dist
dotnet publish OpenTabletDriver.Daemon\OpenTabletDriver.Daemon.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o dist

# shim
cd shim
cargo build --release   # 产物 target\release\Wintab32.dll
```

`ux-patch/` 里的文件按文件名对回上游对应路径（例如 `FeelGroup.cs` → `OpenTabletDriver.UX/Controls/`，`LogFile.cs` → `OpenTabletDriver.Daemon/`）。

## 使用

1. 先起 `OpenTabletDriver.Daemon.exe`，再起 `OpenTabletDriver.UX.Wpf.exe`。
2. 底部「模式」选 `WinTab`，按提示把 `bin\Wintab32.dll`（或包内 `WintabNative\Wintab32.dll`）装进 `C:\Windows\System32`，然后重启 Photoshop。
3. 手感页调整死区/弹性/稳定，校准区用最大力气画一笔得到「保留峰值」。

## 已知限制

- **SAI 2** 不消费本方案的压力（`pkNormalPressure` 不被读取，补发 `WT_CSRCHANGE` 也无效），暂未处理。
- **Windows Ink 模式**在这套里只是「关掉手感处理、压力直通」；真正的 Windows Ink 输出仍需 VMulti 虚拟设备驱动，本仓库不提供。
- 仅 x64。`SysWOW64` 的 32 位 `Wintab32.dll` 未替换，32 位宿主程序用不了本方案。
