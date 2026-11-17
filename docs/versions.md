# 版本台账 — OTD Slim

> 2026-11-17 整理目录时建立。以后每推出一个确认可用的版本，在这里登记一行。
> 凡是"能正常用"的版本，产物放 `release/`；其余历史产物在 `_archive/`，未删除。

## 一、正常在用的版本（主力）

| 版本 | 可执行文件 | 大小(字节) | SHA256 前16位 | 随包 Wintab32.dll |
|---|---|---|---|---|
| **v32** | `release/v32/OpenTabletDriver.Slim.exe` | 45,256,086 | `EC246326D3503801` | `data/Wintab32.dll` 198,144 字节 `5EACB0033C59751F` |
| **v34** | `release/v34/OpenTabletDriver.Slim.exe` | 45,264,278 | `D8E107F38F06BA82` | `data/Wintab32.dll` 198,144 字节 `5EACB0033C59751F` |

- 各自带一个原始压缩包：`release/v32/OTD-Slim-v32.zip`、`release/v34/OTD-Slim-v34.zip`。
- 现场安装目录 `D:\app\OpenTabletDriver\`：
  - `OpenTabletDriver.Slim.exe` —— 是 **v34**（SHA `D8E107F38F06…`）
  - `OpenTabletDriver.Slim.v32.exe.bak` —— 是 **v32** 的备份（原名 `OpenTabletDriver.Slim.exe-`，已改名带上版本标识；SHA `EC246326D35038…`，与 `release/v32` 中的 exe 完全一致）

## 二、两个主力版本的共同已知问题

**SAI 2 窗口定位偏下**

- 现象：v32、v34 在 SAI 2 里，SAI 自己的窗口被摆到屏幕偏下的位置；两个版本都会出现。
- 影响范围：窗口摆放位置，与用 v32 还是 v34 无关。
- 状态：未修复，暂无修复版本。排查方向（待验证）：OTD 启动后上报的显示器/工作区尺寸，SAI 会按它决定窗口位置。

## 三、历史版本（已归档，未删除）

| 版本 | 产物位置 | 说明 |
|---|---|---|
| 早期无名构建 | `_archive/builds/build3` … `build50`、`build10`… | 11-16 当天连续试做，只留目录 |
| v21–v25、v27–v29、v31 | `_archive/builds/dist-vNN`（v26、v30 只有 `build-vNN`/`publish-vNN`） | 中间迭代，未作为主力 |
| v33 | `_archive/builds/dist-v33` | v32 之后第一轮改动（触摸钩子那条线的起点，记录上 PS 压感出现异常） |
| v35–v39 | `_archive/builds/build-v35`…`build-v39`、`publish-v35`…`publish-v39` | 继续迭代，未出独立 dist |

补充说明：

- `_archive/builds/` 里同时保留了各版本的 `build-vNN`、`publish-vNN`、`buildNN`、`publish-vNN`、`dist`、`dist-final`、`release`、`_base`、`_backup`、`src-nomenu`，按名字即可对回。
- 完整版（非 Slim）那一整套的分发产物在 `_archive/dists/`。
- 上游 0.6.x 原始源码在 `_archive/sources/OpenTabletDriver-0.6.x`（Slim 是从它裁出来的）。

## 四、待收尾（重启后处理）

`slim/` 下仍有 26 个旧构建目录 + `slim/src/OpenTabletDriver.Slim/bin` 被当时还在运行的旧实例锁住（跨会话进程，API 终止被系统拒绝），暂时只能留在原处：

```
build3 build4 build5 build6 build7 build12 build15 build16 build18 build20 build24 build30 build33 build37
dist publish publish-pre publish-v3 publish-v4 publish-v5 publish-v11 publish-v14 publish-v15 publish-v16 publish-v17
release
slim/src/OpenTabletDriver.Slim/bin
```

重启电脑后运行 `tools/finish-archive.ps1` 即可把它们并入 `_archive/builds/` 并删除空的 `slim/`。
