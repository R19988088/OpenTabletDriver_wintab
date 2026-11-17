# 版本台账 — OTD Slim

> 2026-11-17 整理目录时建立。以后每推出一个确认可用的版本，在这里登记一行。
> 策略：**只保留最近三个版本**（更早的按"没用"删除）——能用的放 `release/`，其余进 `_archive` 的独立旧版仓库。

## 一、正常在用的版本（主力）

| 版本 | 可执行文件 | 大小(字节) | SHA256 前16位 | 随包 Wintab32.dll |
|---|---|---|---|---|
| **v32** | `release/v32/OpenTabletDriver.Slim.exe` | 45,256,086 | `EC246326D3503801` | `data/Wintab32.dll` 198,144 字节 `5EACB0033C59751F` |
| **v34** | `release/v34/OpenTabletDriver.Slim.exe` | 45,264,278 | `D8E107F38F06BA82` | `data/Wintab32.dll` 198,144 字节 `5EACB0033C59751F` |

- 各自带原始压缩包：`release/v32/OTD-Slim-v32.zip`、`release/v34/OTD-Slim-v34.zip`。
- 现场安装目录 `D:\app\OpenTabletDriver\`：
  - `OpenTabletDriver.Slim.exe` —— 是 **v34**（SHA `D8E107F38F06…`）
  - `OpenTabletDriver.Slim.v32.exe.bak` —— 是 **v32** 的备份（原名 `OpenTabletDriver.Slim.exe-`，SHA `EC246326D35038…`，与 `release/v32` 完全一致）

## 二、两个主力版本的共同已知问题

**SAI 2 窗口定位偏下**

- 现象：v32、v34 在 SAI 2 里，SAI 自己的窗口被摆到屏幕偏下的位置；两个版本都会出现。
- 影响范围：窗口摆放位置，与用 v32 还是 v34 无关。
- 状态：未修复，暂无修复版本。排查方向（待验证）：OTD 启动后上报的显示器/工作区尺寸，SAI 会按它决定窗口位置。

## 三、旧版仓库（只留最后三个版本）

`_archive` 本身是一个独立 git 仓库，只跟踪 `old-versions/`：

| 版本 | 位置 | 指纹 | 说明 |
|---|---|---|---|
| v32 | `_archive/old-versions/v32` + tag `v32` | `EC246326D3503801` | 主力，与 `release/v32` 同一份 |
| v33 | `_archive/old-versions/v33` + tag `v33` | `C433B744AF900D38` | v32 与 v34 之间的中间状态（触摸钩子那条线的起点），未作主力 |
| v34 | `_archive/old-versions/v34` + tag `v34` | `D8E107F38F06BA82` | 主力，与 `release/v34` 同一份 |

```powershell
git -C "E:\000 app\OpenTabletDriver\_archive" log --oneline --decorate
git -C "E:\000 app\OpenTabletDriver\_archive" checkout v33
```

仓库约 57 MB（三个 exe 差异很小，git 做了增量压缩）。

已删除：v21–v31 的成品、以及当时所有的 `build*` / `publish*` / `dist` 中间构建目录（共约 23 GB）、早期版本的 `OTD-Slim-*.zip`。

## 四、待收尾（重启后处理）

`slim/` 下仍有 26 个旧构建目录 + `slim/src/OpenTabletDriver.Slim/bin` 被当时还在运行的旧实例锁住（跨会话进程，API 终止被系统拒绝），约 8.6 GB。重启电脑后运行 `tools/finish-archive.ps1` 会**直接删除**它们（不再归档），然后删掉空的 `slim/`。
