# OpenTabletDriver — Wacom PTH-660 免驱压感方案 / OTD Slim 精简版

> 上游：[OpenTabletDriver / OpenTabletDriver](https://github.com/OpenTabletDriver/OpenTabletDriver)（LGPL-3.0，本仓库修改同样遵循，见 [LICENSE](LICENSE)）
> 仓库：`R19988088/OpenTabletDriver_wintab`，分支 `main`

## 这个目录里有什么

| 路径 | 内容 |
|---|---|
| `src/` | **OTD Slim 精简版**源码（Avalonia 单进程；去掉插件系统、设置层、RPC、自动更新），当前主线，说明见 [docs/otd-slim.md](docs/otd-slim.md) |
| `plugin/` | 完整版 0.6.x 上的 Wintab 输出模式插件 `OpenTabletDriver.Wintab` |
| `shim/` | 用户态 `Wintab32.dll` 的 Rust 实现（WinTab API + 共享内存消费 + 手感处理） |
| `ux-patch/` | 完整版改过的上游文件（手感面板、模式开关、日志降噪等） |
| `bin/` | 现成二进制：`Wintab32.dll`（x64 shim）、`OpenTabletDriver.Wintab.dll` |
| `release/` | 可直接使用的成品（**v32 / v34**），见 [docs/versions.md](docs/versions.md) |
| `docs/` | 版本台账、Slim 设计说明、Wintab 调试与踩坑记录 |
| `tools/` | 构建、安装、诊断、归档收尾脚本 |
| `_archive/` | 归档区，内部**自带一个独立 git 仓库**，只跟踪旧版 `old-versions/`（v32/v33/v34）；其余是旧分发包、上游源码、历代 shim 备份（不进主仓库） |

完整版的原理、构建与使用说明：见 [docs/otd-wintab.md](docs/otd-wintab.md)。

## 当前正常使用的版本

| 版本 | 位置 | 说明 |
|---|---|---|
| **v32** | `release/v32/` | 正常使用；`D:\app\OpenTabletDriver\OpenTabletDriver.Slim.exe-` 就是它 |
| **v34** | `release/v34/` | 正常使用；`D:\app\OpenTabletDriver\OpenTabletDriver.Slim.exe` 就是它 |

**已知问题（v32、v34 都有）：SAI 2 窗口定位偏下** —— 在 SAI 2 里窗口会被摆到屏幕偏下的位置，暂无修复版本。

版本策略：**只保留最近三个版本**（v32 / v33 / v34）。v33 作为 v32 与 v34 之间的中间状态留在旧版仓库；更早的 v21–v31 及所有 `build*`/`publish*` 中间构建已删除。台账见 [docs/versions.md](docs/versions.md)，旧版仓库用 `git -C _archive tag` 查看。

## 快速构建（Slim）

```powershell
dotnet publish src\OpenTabletDriver.Slim -c Release -r win-x64 -o dist `
  /p:PublishTrimmed=true /p:TrimMode=partial /p:DebugType=none
```

裁剪后剔除 `libSkiaSharp.pdb`、`libHarfBuzzSharp.pdb`、`mscordaccore*`、`mscordbi`、`DiaSymReader`、`createdump`、`msquic`，展开体积约 42.8 MB。
`Wintab32.dll` 随包发到 `data\`。

## 目录约定

- `release/` 只放**确认能用**的成品，一个版本一个目录，自带 `README.md` 说明哈希与已知问题。
- 版本只留最近三个：最新的放 `release/`，之前的进 `_archive`（独立 git 仓库，一个版本一个 tag）；更早的和中间构建直接删，不占空间。
- `_archive/`、`release/`、`slim/` 不进主仓库（见 `.gitignore`），二进制走 GitHub Releases 或本地 `release/`。
