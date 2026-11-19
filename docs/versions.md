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

## 五、2026-11-18 现场记录：PS 无压感基线

时间：2026-11-18 16:25 左右（本机，Windows）。此时 System32 与随包是同一份 shim。

| 项目 | 值 |
|---|---|
| `C:\Windows\System32\Wintab32.dll` | 198,144 字节 `5EACB0033C`（build 串 `20261116-sai-map`） |
| `D:\app\OpenTabletDriver\WintabNative\Wintab32.dll` | 同上 `5EACB0033C` |
| 运行中的应用 | `D:\app\OpenTabletDriver\OpenTabletDriver.Slim.exe`（v32，`EC246326…`），PID 16460 |
| 数位板 | Wacom PTH-660，digitizer 44800×29600，penMax 8191，wintabMax 32767 |
| 应用发布的共享内存 | `Local\OpenTabletDriverWintab`，24,640 字节（= 32 + 512×48 + 32，尾部含 SharedMapping），实测 `version = 1` |
| 应用发布的映射 | `MAPPING to shim tablet=(0,2200)+44800x25200 screen=(0,0)+3840x2160 slope=0.086,0.086` |

实测结果（同一份 DLL、同一个运行中的应用，同日验证）：

| 应用 | 压力 | 定位 | 消费方式（取自 shim 日志） |
|---|---|---|---|
| PaintTool SAI 2 | **正常** | **正常** | 点播式 `WTPacket(serial)`；`msgs≈packets`；队列 `q=3/3…4/4` |
| Photoshop CC 2019 | **无压感** | 不可用 | 拉取式 `WTPacketsGet`；`packets=0`；队列 `q=0/0…1/1`；曾出现"画不出线"与按下即卡住 |
| PhotoCraft | **正常** | 正常 | `mask=0x15C2 len=32 pressure_at=20` |

已知未解环节：应用写出的映射（in_org=(0,2200)、in_ext=44800×25200、sys_ext=3840×2160）被 shim 丢弃——`screen_mapping()` 开头就是 `if version < 2 { return None }`，而应用写的是 `version = 1`。因此 `PK_X/PK_Y` 退回整板换算（`y = 29600 − pen_y`），与 PS/SAI 上下文自己声明的 `in=(0,0)×(44800,25200)` 不一致：y 越界 4400，且整幅被拉伸 17.6%。

结论：SAI 与 PhotoCraft 可正常使用；PS 的问题不在"包里没有压力"（同一份包实测 `np` 0→32767 都在），而在 PS 进程侧。


## 六、2026-11-18 16:30–16:45 三次实验的实测结论（失败，勿复用）

这一轮为 PS 无压感连做了三版 shim：`20261118-ctxmap`（`E1CC09952F`）、`20261118-map1`（`9A0611F19B`）、`20261118-npresp`（`61EC12BCD3`），全部回滚。

**关键教训：不要再从 `_archive/wintab/wintab-shim/src/lib.rs` 编 shim。**

| 事实 | 证据 |
|---|---|
| 该源码的 `Axis` 结构带 64 字节 `name` 字段，`WTInfo` 返回 **84 字节** | 直接调 DLL：`cat=100 idx=12/13/15 → 84B`，首字段是名字字符（`88`='X'） |
| 标准 Wintab `AXIS` 是 **16 字节**（`axMin,axMax,axUnits,axResolution`）；仓库 `shim/src/lib.rs` 的 `Axis` 正是这四字段 | 仓库源码第 605 行；其产物 `D3D68905D9` 实测 `cat=100 idx=15 → 16B, axMin=0, axMax=32767` |
| 所以用 archive 源码编出的版本，应用在偏移 0/4 读到的 `axMin/axMax` 是**名字字节和 0**，压力轴失去量程 | 这就是“PS 一沾笔就是恒定最粗”的直接来源 |
| 部署基线与旧时代 DLL 的 `WTInfo` **全表 0 差异**（cat 0/1/2/3/4/99/100/101/200/201/410 × idx 0..22） | 逐项比对 342 项，无一条不同 |
| PS 从不查 `CSR_NPRESPONSE`(idx=11)；它反复查的是 `cat=201 idx=15`（647 次）与 `idx=20`（642 次） | 全部 PS 会话日志统计，`idx=11` 命中 0 行 |
| PS 侧投递统计两代相同 | `PEAK msgs≈170/s gets≈190/s packets=0 q=0..2`，z80 与 sai-map 两版一致 |

**另两条被证伪的方向**

- `screen_mapping()` 的 `version < 2` 门槛放宽（`map1`）：应用确实写了 `tablet=(0,2200)+44800x25200`，包体 y 也变成 `25200-(pen_y-2200)`（实测 `pen_y=18119 → dword6=9281`），但 SAI 直接画不出，回滚。
- 「光标侧轴查询」（`cat=201 idx=15/16`）补轴：`idx=15` 在光标命名空间是 `CSR_PHYSID`，与 `DVC_NPRESSURE` 撞号，被前面的 arm 吃掉，返回的仍是 `0x6B808549`。旧时代 DLL 同样返回该值，故此项无行为空缺。

**PS 无压感仍未解决。** 现在的定论：同一份包里 `np@32` 实测在 0→32767 之间变化、`axMax` 正确为 32767、投递节奏与旧时代一致——即数据面已无可改之处，剩下的必须靠 PS 内实测（画笔预设、压感开关、Wintab 与 Tablet PC 的优先级）才能继续。


## 七、2026-11-18 19:00 之后的实测（PS 压力路径定位，重要）

### 7.1 数据侧已被彻底证明无问题（我自己读共享内存 + shim 全量日志）

- `Local\OpenTabletDriverWintab` 头实测：`magic=0x4F545754`、**`version=2`**（不是早期记录的 1）、`enabled=1`、`width=44800`、`height=29600`、`maxP=32767`、`writeIndex` 递增。
- 包结构 48 字节，字段顺序与 `OpenTabletDriver.Wintab/WintabSharedBuffer.cs::SharedPacket` 完全一致：
  `Sequence / Flags / X / Y / Pressure / TiltX / TiltY / Rotation / Buttons / Cursor / TimeMs / InRange`。
  实测第 0 包：`X=12517 Y=19779 Pressure=0 TiltX=28 TiltY=4 Rotation=-1 Buttons=0 InRange=1`。
- PS 单笔交付统计（`getlog` 全量）：**1942 个按下帧、1850 个不同 np、10…32475**，序列 `2497 2144 1003 1504 2356 … 8825`，标准单调上升曲线。
- 结论：**数据面、包结构、偏移、量程、投递节奏均已排除**。

### 7.2 压力槽位实验（全部无效，勿再重复）

| 实验 | 开关/改法 | 结果 |
|---|---|---|
| 把 np 复制到 36/40/44 | `spreadpressure` v1 | PS 无压力 |
| 把 np 复制到 28…48 全段 | `spreadpressure` v2 | PS **线形变化**，但那是**倾角**（40/44/48 是 `pkOrientation` 三轴，`PK_ORIENTATION` 占 12 字节）被当成倾斜用，不是压力 |
| 只填 28（`pkZ`） | `zpressure` | 无 |
| 只填 48（倾角 Z） | `datapressure` | 无 |
| 压力冻结 16384 | `constantpressure.txt=16384` | 无 |
| 压力冻结 32767 | `constantpressure.txt=32767` | **无** → 说明早期“force-pressure 满压变最粗”不可复现，不能当证据 |
| `pkX/pkY` 归零 | `zero-xy.txt` | PS 光标照样跟笔走 → **PS 的位置来自系统指针，不是我们的包** |
| 旧时代 DLL（`7F0621EAE5`）装回 | — | **同样没有压感** → 排除“新代码写坏” |
| `pkChanged` 改按差量上报 | `08848047C1` | 无变化（但这一版是**规范修好**，应保留） |

### 7.3 真正有效的两个发现（关键线索）

- **`CSR_NPBUTTON` 从 `0` 改成 `1`**（`7EAEEDBD1B`）后，PS **第一次开始读压力**：由“完全没有”变成“恒定满值不变化”。
  → 说明 PS 是靠 `csrNpButton` 判断“这支笔有没有压力按钮”的，0 等于“没有压力笔”。
- 在 `CSR_NPBUTTON=1` 的前提下，`CSR_NPBTNMARKS` 返回 `{0,1}` 时 PS 表现为**二值**（永远满压）；返回 32 字节全 0 时又变回**完全不读**。即 PS 用“按钮标记”参与压力换算。
- 把 `CSR_NPRESPONSE` 从 1024 字节全 0 换成恒等曲线（`4D4F0A535A`，256 档 0→32767）后，PS 又变回**没有压力输出**。
- 同一份代码只把响应表标度从 `0…32767` 改成 `0…255`（`0993BFA526` + `curvescale.txt=byte`），PS 变成**恒定最大压力**。
  → 三个数据点合起来：响应表的**表值大小**直接决定 PS 的力度——表值偏大就满压、偏小就读成 0。说明 PS 把表值当成“逻辑压力”直接用，而不是按 0..32767 归一再乘；也说明它索引表的方式与我们写表的方式不一致。

### 7.4 规范原文（Wacom 官方 Wintab Reference，2026-11-18 查证）

- `WTMgrCsrPressureBtnMarksEx`：`lpNMarks` 是**两个 UINT**，`[0]` 释放阈值、`[1]` 按下阈值；**按下阈值必须大于释放阈值**，否则该按钮的事件生成被关闭。阈值“不能太靠近量程两端”。
- `WTMgrCsrPressureResponse`：响应曲线是 **256 条 UINT**，每一条是一个**逻辑压力值**；“数组下标均匀铺在输入量程上；物理值先映射到 0-255 再作为下标取逻辑压力”。
- `PACKET.pkNormalPressure`：**驱动应输出“已应用曲线后的逻辑压力”**（应用看到的只有逻辑压力）。

据此，`{0,1}` 的按下阈值 1 几乎等于“一碰就按满”，这就是 PS 出现“恒定最大压力”的原因；而曲线由驱动自己应用，应用不该再做归一。

### 7.6 关键结论（2026-11-18 晚）：regression 已定位到「后加的那些默认值」

- **回归结论（实测）**：把 shim 退回**最早一版** `710622e`（`714C06945A`）后，用户回报「**ps有压感**」——PS 侧 Wintab 压感是好的，问题出在 `710622e` 之后加入的改动里。
- **`2b513fd` 那 4 处改动的隔离（逐项加，已验证）**：
  | 版本 | 内容 | hash | PS | SAI |
  |---|---|---|---|---|
  | `710622e` | 原始（`device=0`、`sys_ext=0`、Z 量程 1、无 Y 翻转、4ms） | `714C06945A` | **有压感** | **无压感** |
  | `710622e`+sysExt | 只把 `sys_ext` 设成桌面尺寸、`sys_sens=65536` | `253F219E51` | 有压感 | **有压感，但 Y 轴反了** |
  | `710622e`+sysExt+flipy | 再加 `packet_y = device_extent().1 - pen.y` | `912D4A5B84` | **有压感** | **有压感、Y 轴正确** |
- **结论**：SAI 需要 `sys_ext`（桌面尺寸）才会把包映射到屏幕并读压力；`device=0xFFFFFFFF`、Z 量程 `-1023..2047`、`sens=65536` 这些“官方默认值”**不是必需的**，它们很可能就是 PS 压感丢失的原因（`lcDevice` 从 0 变成 -1，PS 查不到设备轴信息→压力恒 0）。
- **已发布**：这一版就是 **v0.8**（提交 `97f318b`，tag `v0.8` 已推到 GitHub），`shim/assets` 里的 `Wintab32.dll` / `Wintab32_x64.dll` 就是用户实测过的 `912D4A5B84`，x86 为同源 `F3406C085A`。
- **版本备份**：`_archive\wintab\shim-builds\` 下按 hash 存放 `Wintab32-<hash>-<描述>-20261118.dll` 与同名 `.rs` 源码，可直接回滚。

### 7.7 遗留（已不适用，保留作历史）

1. 按规范把响应曲线**在驱动侧应用**到 `pkNormalPressure`（目前直接输出原始压力）。
2. 逐项对照真 Wacom 驱动：`WTInfo(cat=201) idx=1/4/9/10/11` 的**字节长度与数值**（我们三项已实测：name 32 字节、buttons=3、NPBUTTON=0、NPBTNMARKS={0,1}、NPRESPONSE=1024 字节）。
3. 现场 DLL 现状：`912D4A5B84`（老版 + sysExt + Y 翻转），`System32` 与 `WintabNative` 均已同步。
4. 今天试过并已排除的组合（在**新版**基线上均无效）：NPBUTTON=1+marks{0,1}+0 曲线（→ 恒定满压）、NPBUTTON=1+marks全0（→ 完全不读）、NPBUTTON=1+marks{0,1}+满量程曲线（→ 读成 0）、NPBUTTON=1+marks{0,1}+0..255 曲线（→ 恒定满压）、NPBUTTON=0+marks{0,1}+满量程曲线（→ 无压力）。**根因是 `2b513fd` 引入的 LOGCONTEXT 默认值，不是这三张表。**



### 7.8 多显示器：虚拟桌面 + 热插拔识别（2026-11-19）

- `lcSysExt` / `lcSysOrg` 从 `SM_CXSCREEN`（只认主屏）改成**所有显示器矩形的并集**（虚拟桌面），
  副屏在左边时原点为负值。SAI 这类通过 `lcSysExt` 映射的应用才能覆盖到第二块屏。
- 识别：`EnumDisplayMonitors` + `GetMonitorInfoW`，拓扑变化时写一行
  `DISPLAY monitors=N desktop=WxH origin=(x,y) | [i] \\.\DISPLAYn WxH at (x,y) primary`。
- 自动刷新：通知线程每 1 秒重新测量（只有变化才写日志），`WTOpen` 时也重新测量，
  所以插拔屏幕不需要重启 PS/SAI。
- 面板状态：`WintabSetup` 的随包哈希原来是一次性 `Lazy` 缓存，换了 `WintabNative\Wintab32.dll`
  之后界面会一直停在「版本不是本程序的」直到重启程序；改成按文件身份（路径+修改时间+大小）缓存，
  每秒刷新就会自己变回「已安装」。

**发布踩坑（必须记住）**：`dotnet publish` 只加 `EnableCompressionInSingleFile=true` 时，
`libSkiaSharp.dll` / `libHarfBuzzSharp.dll` 会留在 exe 外面，单文件拷到别处启动直接
`System.TypeInitializationException: SkiaSharp.SKImageInfo … DllNotFoundException`（0xe0434352）。
单文件必须同时带 `IncludeNativeLibrariesForSelfExtract=true`：
`dotnet publish src\OpenTabletDriver.Slim\OpenTabletDriver.Slim.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist`
（46,940,767 字节，目录里的两个 `.pdb` 是符号，发布后删掉。）

当前现场：exe `592AE8F6140B6391`，shim `3BC7C21E15`（x64）/ `98686D88A5`（x86）。
### 7.9 热插拔之后映射不跟随（2026-11-19）

现象：双屏开着时能画，关掉一块屏后板子只能用一半区域。

三个原因叠在一起：
1. `WindowsPointer` 的 `ScreenSize` / `Origin` 是构造函数里读一次的 `readonly`，注入坐标一直按旧虚拟桌面归一化；
   拔屏后 `MOUSEEVENTF_VIRTUALDESK` 的坐标被夹到旧范围，表现就是半个板子没反应。
2. `mapping.json` 的显示器区域是**绝对像素矩形**，屏没了/变小了没人重算；区域比剩下来的屏还大，
   多出来的部分全被裁掉 ⇒ 只剩一半能用。
3. 会重算的只有映射页自己的 2 秒轮询，那个计时器在离开页面时就 `Stop()` 了，所以"界面刷新了、映射没跟上"。

修法（`SlimHost.WatchDisplays()`，1 秒比对一次拓扑）：
- `WindowsPointer.RefreshScreen()` 重读虚拟桌面尺寸与原点；
- `MappingConfig.RefitToDisplays()`：那块屏没了 → 退回主屏铺满；原来就是铺满屏幕/比屏幕大的 → 直接铺满新屏
  （不能按比例缩，32:9 压成 16:9 会变成中间一条，还是半个板子）；只有普通子区域才只把中心挪回屏内；
- 改过就 `Save()` + `ApplyMapping()` 重新下发 Area，并 `DisplaysChanged` 通知界面重画；
- 映射页不再自己轮询，改订阅 `DisplaysChanged`，避免和 Host 抢着写 `mapping.json`。

自检命令：`OpenTabletDriver.Slim.exe --displaycheck`（只读，写在 exe 旁边的 `displaycheck.txt`），
实测输出（单屏 3840x2160，模拟"双屏铺满 7680x2160 后拔掉右屏"）：

```
显示器数量  : 1
  DISPLAY1  3840x2160 at (0,0)  主屏
虚拟桌面    : 3840x2160
当前映射    : display=3840x2160@1920,1080 device=\\.\DISPLAY1
区域在屏内  : 是
模拟拔屏后  : changed=True display=3840x2160@1920,1080 tablet=224.0x126.0
```

当前现场：exe `D4635874ECB5CC3F`（46,957,132 字节），shim `3BC7C21E15`。