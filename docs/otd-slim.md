# OTD Slim

精简版 OpenTabletDriver 骨架。目标：只保留能驱动数位板所需的最底层，去掉插件系统、设置层、RPC、自动更新，UI 换成 Avalonia，最终整包展开体积压在 50MB 以内。

当前状态：**里程碑二完成** —— 底层能起来，窗口能显示，能真实检测到数位板，Wintab 输出可以真实点击。

## 目录结构

```
slim/
└── src/
    ├── Directory.Build.props              # net8.0 + 版本号
    ├── OpenTabletDriver/                  # core：设备发现、管线、驱动
    ├── OpenTabletDriver.Native/           # Win32 / WinUSB / HID 原生互操作
    ├── OpenTabletDriver.Plugin/           # 契约层（接口/特性/输出模式基类）
    ├── OpenTabletDriver.Configurations/   # 363 条平板配置 + 报文解析器
    ├── OpenTabletDriver.Wintab/           # 内置 Wintab 输出（共享内存 + 指针）
    └── OpenTabletDriver.Slim/             # Avalonia 应用（单进程）
        ├── Program.cs                     # 入口，支持 --selftest / --buttontest / --autoclick / --realtest
        ├── App.cs / MainWindow.cs         # 自检窗口（含点击测试区）
        ├── SlimHost.cs                    # 启动编排：core + 配置 + Wintab + 按键管线
        ├── PenButtonPipeline.cs           # 按键管线（替代 Desktop 的 BindingHandler）
        └── WindowsPointer.cs              # 内置 SendInput 绝对指针
```

## 移植了什么

| 模块 | 状态 | 说明 |
|---|---|---|
| core（设备发现 / 报文管线 / 驱动） | 保留 | 原样搬运，包含 HidSharp 与 WinUSB 两条设备后端 |
| Native（Win32 / WinUSB / HID） | 保留 | Windows 部分原样搬运 |
| Plugin（契约层） | **部分保留** | 只留接口、特性、`OutputMode` 基类；插件加载器、`PluginManager`、插件目录扫描全部没搬 |
| Configurations | 保留 | 363 条平板配置 + 解析器，PTH-660 等都在 |
| Wintab 输出 | 保留并内置 | `Wintab32.dll` 随包发到 `WintabNative/`，不再是可选插件 |
| Desktop（设置 / RPC / 更新 / 插件管理） | **剔除** | 用 `SlimHost` 最小化替代 |
| UX（Eto + WPF） | **剔除** | 换成 Avalonia 单窗口 |
| Daemon / Console / Benchmarks / Tests | **剔除** | 改为单进程，UI 与驱动同进程，省掉一份 .NET 运行时 |

## 体积实测（裁剪发布，展开不压缩）

```
dotnet publish src\OpenTabletDriver.Slim -c Release -r win-x64 -o dist ^
  /p:PublishTrimmed=true /p:TrimMode=partial /p:DebugType=none
```

| 口径 | 体积 |
|---|---|
| publish 原始产出（含 2 个第三方 pdb） | 149.15 MB |
| 删掉 libSkiaSharp.pdb / libHarfBuzzSharp.pdb | 49.06 MB |
| 再删 mscordaccore×2 / mscordbi / DiaSymReader / createdump / msquic | **42.81 MB（112 个文件）** |

构成大头：libSkiaSharp 11.09、av_libglesv2 5.14、coreclr 4.76、CoreLib 2.89、HarfBuzz 1.73、clrjit 1.70、Avalonia.Base 1.55。Wintab32.dll 只有 192KB。

裁剪版实测可用：`--selftest` 仍能读到 363 条配置并识别 Wacom PTH-660，`--buttontest` 通过，窗口也能正常起来。

## 点击问题的根因与修复

现象：开启 Wintab 输出后光标会跟着笔动，但按笔尖没有任何点击。

根因：原版 OTD 的点击不在 core 里，而在 `OpenTabletDriver.Desktop\Binding\BindingHandler.cs` ——
它是管线里一个 `PostTransform` 元素，负责读 `ITabletReport.PenButtons` / `IEraserReport`，
再把按下/抬起翻译成鼠标事件。精简版剔掉了整个 Desktop，这一层就断掉了：
报文里只剩位置和压力被消费，按键位没人读，于是「能动不能点」。

修复：新增 `PenButtonPipeline.cs`（`PostTransform` 元素），把按键层内建化。**注意语义**：

| 报文信号 | 动作 | 鼠标效果 |
|---|---|---|
| `Pressure > 0` | `Tip` | 左键 |
| `IEraserReport.Eraser` + `Pressure > 0` | `Eraser` | 左键 |
| `PenButtons[0]` | `BarrelButton1` | 右键 |
| `PenButtons[1]` | `BarrelButton2` | 中键 |
| （若有第三键） | `BarrelButton3` | 无映射（Wintab 内仍会置位） |

**笔尖不在 `PenButtons` 里。** 原版 `BindingHandler` 用 `ThresholdBindingState` 按压力阈值判定笔尖，
`PenButtons` 只装侧键；Wacom IntuosV2 解析器也只给 `[bit1, bit2]` 两个侧键位。
一开始按「`PenButtons[0]` = 笔尖」实现，结果压力一路涨到 4972 而成不了点击，
这也是"看起来能动、其实点不了"的真实原因。

细节：
- 只在状态变化时触发（边沿触发），避免每帧重复按下/抬起
- 收到 `OutOfRangeReport`（笔离开感应区）时释放所有仍按住的键，防止卡住左键
- 按键走 `WintabPointer`（它同时实现 `IPenActionHandler`），所以鼠标点击和 Wintab 共享内存里的按键状态同步更新

PTH-660 的报文确认：`IntuosV2Report` 里 `PenButtons = [bit1, bit2]`，索引 0 就是笔尖，映射正确。

一处关键机制：`OutputMode.Elements` 为空时管线是 Passthrough，`Read()` 什么都不做；
挂上 `Elements` 之后 entry 才走 `Internal → PreTransform → self → PostTransform → OnOutput`。
所以按键管线必须在 `StartOutput` 里挂进 `mode.Elements`，光实现类不接线是没有效果的。

## 点击链路的验证方式

不是"看起来应该能点"，而是三层实测：

1. `--buttontest`：用替身指针（只记账、不碰真实鼠标）跑完整管线，喂一帧"笔尖按下"和一帧"抬起"
2. 窗口内的「点击测试区」：真实点击落在窗口上才计数，并写入 `clickpad.txt`
3. `--autoclick`：应用自己把内置指针移到测试区中心、发一次真实 `SendInput` 左键，写完 `selfclick.txt` 后退出

实测结果：

```
启动状态    : 运行中（测试替身指针）
按键管线    : 按键：按下 1 / 抬起 1
替身收到    : 按下 1 / 抬起 1 / 移动 2 / 刷新 2
结论        : 点击链路通

点击计数      : 1
结论          : 真实 SendInput 点击已落在窗口内
```

## 运行

```powershell
# 带窗口
.\OpenTabletDriver.Slim.exe

# 直接打开某一页（截图/排查用）
.\OpenTabletDriver.Slim.exe --page=映射
dotnet run --project src\OpenTabletDriver.Slim

# 命令行自检（不弹窗，结果写入 selftest.txt）
src\OpenTabletDriver.Slim\bin\Release\net8.0\OpenTabletDriver.Slim.exe --selftest

# 点击链路自检（替身指针，不碰真实鼠标，结果写入 buttontest.txt）
src\OpenTabletDriver.Slim\bin\Release\net8.0\OpenTabletDriver.Slim.exe --buttontest

# feel.json 读写自检（读 → 写 0.12 → 再读 → 还原，结果写入 feeltest.txt）
src\OpenTabletDriver.Slim\bin\Release\net8.0\OpenTabletDriver.Slim.exe --feeltest

# 真实点击自测（应用自己点自己的测试区，写完 selfclick.txt 后退出）
src\OpenTabletDriver.Slim\bin\Release\net8.0\OpenTabletDriver.Slim.exe --autoclick
```

## 当前实测（本机 Windows 11 / .NET 8）

```
配置条目      : 363
HID 设备      : 19
数位板        : Wacom PTH-660
Wintab        : 已内置 Wintab32.dll（192 KB）
输出          : 未启动
```

窗口内容：系统原生标题栏 + 左右两栏 —— 左侧导航（压力 / 映射 / 按键 / 设备 / 关于），右侧页面，
侧栏底部显示数位板名与运行状态。

## 界面（系统原生标题栏 · 左右两栏）

- 标题栏用**系统自带的那一条**（`SystemDecorations` 保持默认），圆角、投影、最小化/关闭按钮全部交给 DWM，
  应用一行边框和阴影都不画。之前试过自绘透明窗 + `BoxShadow`、`ExtendClientAreaToDecorationsHint`、
  直接去掉 `WS_CAPTION` 三种做法，都因为"假窗 / 重影 / 只有 Win11 才好看"被撤回了。
- 系统在"应用深色模式"下会把标题栏刷成深灰，所以 `WindowTheme.cs` 用 `DwmSetWindowAttribute`：
  关掉 `DWMWA_USE_IMMERSIVE_DARK_MODE`（属性 20，老系统 19），Win11 上再直接指定标题栏/文字颜色（35/36）。
  Win10 只认前面那个开关，效果同样是浅色标题栏。窗口显示后、首次激活、延迟 250ms 各设一次，
  避免 Avalonia 应用主题时又把它刷回去。
- 左右两栏：左侧 200px 导航（浅灰 `#F5F5F7`），右侧白色内容区 + `ScrollViewer`。
- 导航里「压力 / 映射」可用，「按键 / 设备 / 关于」是灰色占位。
- 浅色强制（`App.cs` 里 `RequestedThemeVariant = ThemeVariant.Light`），系统深色模式下也不会变暗。
- 调试用：`OpenTabletDriver.Slim.exe --page=映射` 直接打开某一页（截图和排查用）。

### 映射页（图形化，对应原版 OTD 的「显示器定位」+「数位板区域」）

原版的语义（`AbsoluteModeSettings` / `AreaSettings` / `DriverDaemon.SetAbsoluteModeSettings`）照搬，
但**界面上没有任何数值输入框**，全部靠拖：

| 区域 | 画布内容 | 拖拽结果 |
|---|---|---|
| 上画布 | 整个**虚拟桌面**，每一块显示器都单独画出来并标名字（`DISPLAY1（主）` 等） | 选区落在哪块屏、多大，就写进 `Display*`（虚拟桌面像素坐标，X/Y 是区域中心） |
| 下画布 | **整块数位板**（毫米，PTH-660 = 224×148） | 写进 `Tablet*`（毫米，X/Y 是区域中心，自动夹在板子范围内） |

- 交互：拖矩形内部 = 平移；拖四角 = 以中心缩放；命中判定按**屏幕像素**算（早先按域单位算，
  缩放后整个矩形都被判成"抓到角了"，一拖就乱跳，已修）。
- **拖动过程中只重绘、不写配置**，松手才 `Save()` + `ApplyMapping()` —— 笔在动的同时改映射会让光标自己推自己，
  表现就是抖；现在拖动全程映射不变，松手一次性生效。
- 页面里**没有显示器下拉框**：上画布本身就是整个虚拟桌面，哪块屏在哪一目了然，直接在上面拖。
- **边缘吸附**（`AreaEditor.SetSnapEdges`）：拖动时离屏幕边缘 10px 以内就贴齐，所以拖到边界正好铺满、不会越界；
  想跨屏就往下一块屏的边缘继续拖，选区会跟着扩大（整体仍被夹在虚拟桌面范围内）。
- 「锁定比例」：数位板区域宽高比跟随显示器区域（默认开，224×148 的板子会锁成 224×126 的 16:9）。
- 「铺满该显示器」：把选区重置成当前那块屏（`DisplayDevice`）的整块面积。
- 「按键在左 / 按键在右」：ExpressKey 在哪一侧。原版 OTD 没有这个选项，这里按 Wacom 驱动的左右手语义实现成
  **输入区域旋转 180°**（`ButtonsOnRight = true` 时 `mode.Input` 带 180° 旋转）。
- **显示器热插拔**：映射页每 2 秒重新枚举一次，插上第二块屏，下拉列表和上画布会自己长出新屏；
  配置里那块屏被拔掉时自动退回主显示器并铺满它。
- 写回 `%LOCALAPPDATA%\OTDWintab\mapping.json`（`DisplayDevice / DisplayWidth / DisplayHeight / DisplayX / DisplayY /
  TabletWidth / TabletHeight / TabletX / TabletY / ButtonsOnRight / LockAspectRatio / EnableClipping /
  DigitizerWidth / DigitizerHeight`），同样是源生成序列化，裁剪包里不会退默认值。
- 改动立刻热更新到输出管线：`SlimHost.ApplyMapping()` 设 `mode.Input` / `mode.Output` / `AreaClipping`，不用重启。

### 压力页（照旧版 feel-panel 实现）

| 控件 | 写回 `feel.json` | 说明 |
|---|---|---|
| 数位板输入模式 | `InputMode` | WinTab / Windows Ink 二选一 |
| 压力死区（0–30） | `DeadZone = 值 / 100` | |
| 弹性（1–100） | `Elasticity = 值 / 10` | |
| 笔触稳定（1–16） | `StrokeFilter` | |
| 按键位置（左/右） | `mapping.json` 的 `ButtonsOnRight` | ExpressKey 在哪一侧；右侧 = 输入区域转 180° |
| 压力校准 | `MaxPressure` | 松手时取「前 100ms 的峰值」，显示为「保留峰值 x%」 |

数值框沿用旧版 `DragValue` 的交互：框本身就是滑块 —— 左右拖动连续调值、点左右半边 ±1、滚轮 ±1。

校准区从共享内存 `Local\OpenTabletDriverWintab` 读最新一包（压力 +16、按键 +32、inRange +44，每包 48 字节），
笔在框里画一笔，线宽跟着实时压力变；抬笔后把峰值写回 `feel.json`。
左上角实时显示「压力 x%」。

新增文件：`Palette.cs`（配色）、`DragValueField.cs`、`CalibrationPad.cs`、`PressurePage.cs`、
`FeelConfig.cs`、`WintabSharedReader.cs`。

### 裁剪发布的一个坑（已修）

`feel.json` 原本用反射式 `JsonSerializer.Deserialize<T>` 读写。裁剪发布（`PublishTrimmed`）会把反射元数据裁掉，
**读的时候不报错、直接退回默认值** —— 界面里变成死区 0、弹性 60、笔触稳定 2、保留峰值 100%，
看起来像"配置没保存"，其实是序列化失效。

改成源生成上下文（`JsonSerializerContext`）后，裁剪版读写都正常。`--feeltest` 就是这条链路的自检：

```
配置文件    : %LOCALAPPDATA%\OTDWintab\feel.json
读取(前)    : Elasticity=4.6 StrokeFilter=2 MaxPressure=0.69 Mode=winTab
写入后读取  : DeadZone=0.120
结论        : feel.json 读写正常
```

## 已知未做

- 全局设置持久化：目前只有 `feel.json`（压力）和 `mapping.json`（映射）两份，按键映射还没地方存
- 按键 / 设备 / 关于三页界面（导航里已占位）
- Wintab 安装与注册（目前只是随包携带 `Wintab32.dll`，还没做系统级注册那一步）
- 发布脚本固化（目前是手工命令行；体积已实测，包装成 `build.ps1` 待做）

## 补齐基础功能时还要处理的

- **8 个 ExpressKey（`IAuxReport`）**：现在一条都没映射，按键按下去无反应。原版把它们映射成键盘按键，需要先有一份配置才能谈"映射"，或者先给一套固定快捷键
- **滚轮（`IWheelButtonReport` / `IAbsoluteWheelReport`）**：同样未映射
- **第三侧键**：PTH-660 只有 2 个侧键，`BarrelButton3` 目前是空映射（原版语义也是 no-op）
- **`%LOCALAPPDATA%\OTDWintab\` 下的 pen-only / pen-contact 开关**：`WintabPointer` 会读这两个文件决定"是否抑制内置指针"，本机两个文件都不存在，走的是"既移动光标也发点击"的默认路径。精简版是否保留这套文件开关、还是直接给死一个默认行为，待定

## 验证真实点击的一步（需要用笔做）

1. 启动 `dist\OpenTabletDriver.Slim.exe`
2. 点「启动 Wintab 输出」
3. 把笔点进窗口下方橙色「点击测试区」，计数应该增加；按侧键也应计数
4. 日志里会出现 `按键: 笔尖 按下 → 左键` 这类记录，说明报文按键位确实被读到了
## 按键页（`ButtonsPage.cs`）

左边用 `PenView` 画一支竖着的压感笔（笔尾橡皮擦、两个侧键、笔尖各有一个蓝点），右边是四行映射：

| 行 | 存在 | 默认 |
|---|---|---|
| 笔尖 | `buttons.json` 的 `Tip` | 左键单击 |
| 下侧键（靠笔尖） | `Barrel1` | 右键单击 |
| 上侧键（离笔尖远） | `Barrel2` | 双击 |
| 橡皮擦 | 固定 | 橡皮擦（不可改） |

点一下右侧按钮就循环换一个动作：左键单击 → 右键单击 → 中键单击 → 双击 → 无，改完立刻写盘并生效（`PenButtonPipeline` 和界面共用同一个 `ButtonsConfig` 实例）。

映射语义：左键 → `PenAction.Tip`、右键 → `BarrelButton1`、中键 → `BarrelButton2`、
橡皮擦 → `PenAction.Eraser`、双击 → 70ms 内两下 Tip；无 → 什么都不发。

每行右边还有一个 ＋：点一下进入录制（按钮变成「请按键…」），这时按下任意键盘组合（Ctrl/Shift/Alt/Win + 字母/数字/F 键等）或鼠标左/右/中键，
就把它记成自定义动作，按钮上直接显示具体按键（如 `Ctrl+Z`、`鼠标右键`），＋ 随之变成 ×，点 × 删除自定义、回到内置动作。
自定义值存在 `buttons.json` 的 `TipCustom` / `Barrel1Custom` / `Barrel2Custom`。

支持的形态（都存成给人看的文本）：
- 纯键盘组合：`Ctrl+Z`、`Ctrl+Shift+N`、`F5`
- 纯鼠标键：`鼠标右键`
- **修饰键 + 鼠标键**：`Alt+鼠标右键`、`Ctrl+鼠标中键`（Photoshop 里取色、临时橡皮这类就靠它）

执行顺序：先 `SendInput` 按住修饰键 → 再走原生指针按下鼠标键；抬起时先放鼠标键 → 再放开修饰键，
顺序反了应用会认不出组合。

## 侧栏底部（自启 + Wintab32.dll 安装）

- **开机自启**：开关直接读写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `OTD Slim` 值，内容是带引号的 exe 全路径。打开时会顺手清掉旧版 OTD 的自启项（名字或命令里带 `OpenTabletDriver` 的，`OpenTabletDriver.Daemon` / `OpenTabletDriver.UX` 都算），避免开机同时拉起两套驱动；程序启动时也会查一次，发现旧项就清掉。
- **Wintab32.dll 安装状态**：`WintabSetup.Detect()` 比对 `%WINDIR%\System32\Wintab32.dll` 与随包 `WintabNative\Wintab32.dll` 的 SHA256 内容，分四种状态——已安装（本程序）、版本不是本程序的、未安装、随包缺失。未安装/版本不符时该行显示成蓝色可点击的提示，点一下用 UAC 提权把同一目录的 DLL 复制进 System32；已安装时变成灰色静态文字。
- 调试开关：`OTD_FORCE_WINTAB_MISSING=1` 强制显示"未安装"；`OTD_WINTAB_SYSTEM_DIR=<目录>` 把安装目标换成别的目录，可以不弹 UAC 地走一遍"未安装 → 安装 → 自动刷新"。
- 安装顺序：先直接 `File.Copy`（程序本身以管理员身份跑时不用弹 UAC），没权限才提权；提权脚本把成功/失败原因写进 `%TEMP%\otd-wintab-install.txt` 读回来显示。
- 安装期间状态行固定为"正在安装…"，不被 1 秒定时刷新覆盖；装完回到 UI 线程重新检测并按真实状态刷新。
## 附加页（原 AutoHotKey 脚本）

原来另跑一个 `AutoHotKey.ahk`，现在这些功能全在程序里（`HotkeyEngine.cs`），一条后台线程装两个低级钩子（`WH_KEYBOARD_LL` / `WH_MOUSE_LL`），开关状态存 `%LOCALAPPDATA%\OTDWintab\tweaks.json`，页面上一勾立刻生效：

| 开关 | 行为 |
| --- | --- |
| 反向（自然）滚动 | 鼠标滚轮上下、左右全部反向（吞掉原事件后自己发反向滚轮） |
| 低延迟模式（0.5ms 计时） | `ntdll!NtSetTimerResolution(5000, true)`，关掉时释放 |
| Win 键需要长按 1.5 秒 | 轻点左 Win 直接吞掉；按住 1.5 秒才把 Win 按下、松开补抬起 |
| CapsLock 切换输入法 | CapsLock 改成发 `Ctrl+Space`，不再切大小写 |
| 任务栏滚轮控制音量 | 指针在 `Shell_TrayWnd` / `Shell_SecondaryTrayWnd` 上时滚轮发音量键 |
| 双击 Shift 复制文件名 | 400ms 内按两次 Shift：发 `Ctrl+C`，把剪贴板里的文件列表/路径只留文件名再写回去 |
| Alt + 右键拖动锁单方向 | 按住 Alt + 右键拖动时，先动过 5px 的方向被锁定，600ms 内保持单轴（PS 调笔刷尺寸不再飘） |
| Everything 不被抢剪贴板 | Everything 窗口里按空格/回车/单击后 1.2 秒内若剪贴板被改成路径，就恢复原内容 |

另外两个按钮：打开自启目录（`shell:startup`）、重启程序（退出当前进程再拉一个新的）。

要点：
- **必须处理"注入"的鼠标事件**：笔的移动和侧键都是本程序 `SendInput` 出去的，带 `LLMHF_INJECTED` 标记；早先一律跳过注入事件，导致 Alt+右键锁方向对笔完全失效。现在只有滚轮跳过注入（避免自己套自己），键鼠其余事件照常处理。
- 钩子挂了会往 `%LOCALAPPDATA%\OTDWintab\hotkeys.log` 记一行 `hooks keyboard=... mouse=... admin=... error=...`，之后每分钟一行 `events=<累计事件数>`。事件数还在涨说明钩子活着。`admin=True` 说明程序以管理员身份启动：低级钩子照样收得到全局输入，往低完整性窗口注入也没问题，只有"往更高完整性窗口（任务管理器之类）注入"会被 UIPI 拦；日常用不到管理员，只有装 Wintab32.dll 进 System32 那一下才需要，所以做成了按需提权。
- 和别的快捷键工具（AHK、AutoHotkey 脚本、键位改键软件）同时开会互相吃键，只留一边。
- 压力相关的一律留在「压力」页，附加页不碰 Wacom 注册表，也不重启 Wacom 服务。

## 钩子为什么容易"启动能用、几秒后全失效"

Windows 对低级钩子回调有超时限制（`LowLevelHooksTimeout`），回调里同步 `SendInput`、或者每次鼠标移动都调 `GetAsyncKeyState` / 查窗口类名，累计下来就会被系统**静默卸掉钩子**——表现就是刚启动那一下有效，接着全都没反应。所以：

- 回调里**只做判断和吞事件**，所有 `SendInput`、剪贴板读写都丢进后台队列线程（`Actions`）执行；
- 鼠标移动事件平时完全不解析结构体，只在 Alt+右键锁方向期间才处理；
- 任务栏矩形由后台每 2 秒缓存一次（主/副任务栏都算），回调里只做数值比较；
- 仍然加了 5 分钟看门狗：在钩子线程里 `UnhookWindowsHookEx` + 重新 `SetWindowsHookEx` 一次，防止系统再卸掉。

## 发布

```
dotnet publish src\OpenTabletDriver.Slim\OpenTabletDriver.Slim.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishTrimmed=true -p:TrimMode=partial -p:DebugType=none -o dist
```

发布后删掉：`*.pdb`、`createdump.exe`、`Microsoft.DiaSymReader.Native.amd64.dll`、`mscordbi.dll`、`msquic.dll`、`mscordaccore*.dll`，成品 **112 个文件 / 42.88 MB**。
如果 `slim\dist` 被残留进程占用导致覆盖失败，可把 `-p:OutputPath` 指到别的目录、`-o` 指到新目录再发布；当前成品在 `slim\release`。
## 附加页：静默提权启动（第 9 行）

- `StartupHigh.cs`：`schtasks /Create /TN "OTD Slim" /SC ONLOGON /RL HIGHEST /F`。
  **只有创建/删除这个任务时弹一次 UAC**，之后每次登录都是管理员权限、不再弹窗。
- 开关打开时会自动把 HKCU Run 里的普通自启项关掉，避免开两份。
- 代价：任务在「任务计划程序」里可见；安全软件可能拦或提示；账户不在管理员组时 `HIGHEST` 无效。
- 权限能解决的是：装 `Wintab32.dll`（System32 需要管理员）、写 HKLM、启停服务。
  它解决不了「全局钩子收不到输入」——低级钩子是桌面级的，不需要管理员。

## 附加页状态行（判断「功能有没有生效」）

每秒刷新一行：

    钩子：键盘已挂载 · 鼠标已挂载 · 事件 N · 滚动a · 音量b · Caps c · Win d · Shift e · 锁轴f

- `事件` 一直为 0：钩子没收到任何输入，多半是安全软件拦了全局钩子。
- `事件` 在涨但某项计数不涨：那个功能的判定条件没命中（任务栏矩形没取到、Everything 窗口类名不匹配等）。
- 检测到 `AutoHotkey*` 进程时，状态行会用强调色提示冲突：两套全局钩子会互相吞事件，只能留一套。
- 「重新挂载钩子」按钮会在线程里 Unhook 再 SetWindowsHookEx，不用重启程序。

## 发布

最新可运行包：`slim\publish-v2`（112 文件 / 42.90MB，来自 build10，含状态行 + 各功能计数 + 最高权限自启）。

旧的 `slim\publish`、`slim\release`、`slim\dist` 被残留实例锁住、内容已不完整，先跑 `cleanup.cmd` 再删。
以后发布建议每次换一个新输出目录（`-p:OutputPath=buildN -o publish-vN`），避免被运行中的实例锁住。
## 滚轮"被接管又滚不动"的真因（build13 修复）

`SendWheel` 里 `MouseData = (uint)(delta << 16)` 是错的。`MOUSEEVENTF_WHEEL` 的 `dwData`
直接就是增量本身（±120），左移 16 位会送出一个 7864320 的乱值，应用侧当成无效/异常滚轮忽略掉；
而钩子里已经 `return 1` 把原始滚轮吞掉了，于是**整机所有滚轮失效**。

修法：

- `MouseData = unchecked((uint)delta)`；
- 只在注入成功后吞事件（`SendWheel`/`SendKeyPair` 返回 `bool`），失败就 `CallNextHookEx` 放行，宁可没反转也不让滚动死掉；
- 自己的注入统一打 `dwExtraInfo = OtdMarker (0x4F54444C)`，钩子见到直接放行，避免"自己套自己"；
- 任务栏音量走同一套：注入成功才吞原始滚轮。

验证方式（隔离桌面可复现）：`scrolltest.ps1` 开一个 200 项的 ListBox 探针，`--wheeltest`
用程序自己的 `SendWheel` 注入 3 格，`TopIndex` 60 → 63 → 66，说明不靠 `mouse_event`、纯自己的
`INPUT` 结构也能真的滚起来。

## 顺带修掉：笔的"Alt+右键锁单方向"原先不生效

笔的坐标是先算再注入的，注入事件带 injected 标记，钩子里改 `lParam` 无效，所以钩子侧那套
`SetCursorPos` 修正只对物理鼠标有用。现在锁轴状态在注入前夹一次：
`HotkeyEngine.ClampAxis(x, y)` → `WindowsPointer.MoveTo` 先夹再 `Normalize`。

## 发布（续）

`publish-v3`（112 文件 / 42.90MB，build13/build14）。`publish-v2` 已被取代（滚轮是坏的）。
## 第二层真因：不能在钩子回调里同步 SendInput（build16 修复）

修好 `dwData` 之后仍然"滚动能用、功能无效"，原因是我把注入改成了**在低级钩子回调里同步调用 SendInput**。
回调里同步注入会被吞掉/不生效，`SendInput` 返回成功但事件没落地；我又加了"注入失败就放行原事件"的兜底，
于是表现恰好是：滚动恢复正常、但一点反转都没有。

正确做法（也是 AHK 的做法）：回调里只判断 + 吞事件，注入丢到后台队列线程去做。

- 滚轮：`Post(() => SendWheel(-delta, horizontal))`，队列线程注入；
- 任务栏音量：`Post(() => SendKeyPair(volumeKey))`；
- `OTD_WHEEL_DEBUG=1` 环境变量可放开"别人注入的滚轮"，用来在隔离桌面做端到端验证
  （我方标记 `dwExtraInfo` 始终跳过，不会自己套自己）。

端到端实测（build16 + 调试开关）：ListBox `TopIndex 60 → 57`，日志 `wheel d=-120 at 490,455 taskbar=False invert=True`，
向下滚 3 格、画面向上走 3 格，反向成立。

`publish-v4`（112 文件 / 42.90MB，build17）为这一版；`publish-v3` 的功能是"能滚但没反转"，已被取代。

## 底包：无附加页版本（2026-11-16，笔已验证可用）

`附加` 那一整页（Tweaks / TweaksPage / HotkeyEngine / StartupAuto / StartupHigh / WintabSetup）
是导致「笔动一下就停」的版本分界。把这一整套从代码里摘掉后，笔恢复正常。

判定过程（可复查）：

- `--hiddump` 裸读脚本（`hiddump.cmd` → `build46\hiddump.txt`）：60 秒 7351 条、间隔约 16ms、
  位置字节连续，证明**设备本身完全正常**；
- 正式程序同一接口 `raw-<pid>.log` 只有一条：`len=192 elapsed=250ms`，之后线程卡死；
  根因之一是 `HidSharpEndpointStream` 里 `stream.ReadTimeout = int.MaxValue`（无限等待）；
- 逐个查 `build3`～`publish-v17` 的 `OpenTabletDriver.Slim.dll`：**全部**含
  `TweaksPage / HotkeyEngine / Tweaks / StartupAuto / WintabSetup`，即从来没有「无附加」的现成构建，
  早前按 exe 字符串判断出的「旧版无附加」是错的（类型名在 dll 里，不在 exe）；
- 真正做出来的无附加构建：`src-nomenu` + `publish-nomenu`，
  产物核对 `TweaksPage=False HotkeyEngine=False WintabSetup=False StartupAuto=False`，
  导航只剩 压力 / 映射 / 按键 / 设备 / 关于，**笔连续可用**。

底包位置（源码 + 发布 + zip 三份都在）：

- `slim\_base\v18-nomenu_20261116\src`（760 文件，已排除 bin/obj）
- `slim\_base\v18-nomenu_20261116\publish`（112 文件 / 43.04MB）
- `slim\_base\v18-nomenu_20261116\OTD-Slim-no-menu.zip`（18.3MB）

昨天的完整版备份（含附加页，仅留档、不再作为发布基础）：

- `slim\_backup\v17_20261116_1650\src`（766 文件）
- `slim\_backup\v17_20261116_1650\OTD-Slim-build49.zip`

无附加版启动脚本：`reset-nomenu.cmd`（提权 → 杀光实例 → 写 `nomenu-run.log` → 启动 `publish-nomenu`）。

后续要加回功能时，建议顺序：单实例 → 关窗必退 → Wintab 安装行 → 自启开关 → 最后才是钩子那套，
每加一项都试一次笔；`最高权限开机启动（计划任务）` 与 `自启` 同时开启会产生第二个实例抢 HID 流，
这是最可疑的一项，优先单独验证。
## 目录结构（v20：一个文件夹 + 一个 exe + 一个 data）

用户要求「app 目录里只有一个程序文件夹、一个 exe、一个用户数据目录，不要散落」。

做法：

1. **单文件发布**：`PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true`，
   `DebugType=none`。产出目录里只有一个 `OpenTabletDriver.Slim.exe`（45,202,838 字节 ≈ 43.1MB），
   原来那 112 个 dll / json / pdb 全部打进 exe。曾出现的
   `libHarfBuzzSharp.pdb`(20.9MB)、`libSkiaSharp.pdb`(84.0MB) 是调试符号，直接删掉，
   打包前记得 `Get-ChildItem publish-single -Filter *.pdb | Remove-Item`。
2. **用户数据只放一处**：新增 `Paths.cs`，`Paths.DataDir` 优先取 **exe 旁边的 `data\`**
   （先真写一个 `.write-test` 探活，失败才退回 `%LOCALAPPDATA%\OTDWintab`）。
   `ButtonsConfig / FeelConfig / MappingConfig / Tweaks / SlimHost / HotkeyEngine /
   Instances / PenButtonPipeline` 里 8 处 `Path.Combine(LocalApplicationData,"OTDWintab",...)`
   全部改成 `Path.Combine(Paths.DataDir, ...)`，`Program.Main` 第一行 `_ = Paths.DataDir;` 提前解析。
3. **wintab32.dll 不动代码**：`wintab-shim\src\lib.rs` 里写死 `%LOCALAPPDATA%\OTDWintab`
   （第 69/107/982/1536/1617/1639 行）以及 `pen_injector.rs`、`peninject.rs`。没有改 Rust，
   改成 `Paths.LinkLegacyPath()`：启动时把 `%LOCALAPPDATA%\OTDWintab` 做成指向 `data\` 的
   **目录联接**（`cmd /c mklink /J`，不需要提权）。老目录里已有的 json/日志会先拷进 `data\`
   再删目录，然后建联接；已是联接则跳过。这样主程序、驱动、dll 三方看到的都是同一个物理目录。
4. **内嵌的 Wintab32.dll 释放位置**：`WintabSetup` 原来写到 `AppContext.BaseDirectory\WintabNative\Wintab32.dll`，
   exe 旁边会多出一个文件夹，改成 `Paths.DataDir\Wintab32.dll`；csproj 里
   `None ... Link="WintabNative\Wintab32.dll" CopyToPublishDirectory` 那一项已删除。
5. **exe 名字保持 `OpenTabletDriver.Slim.exe`**：`Instances.ProcessName`、`HotkeyEngine`、
   `TweaksPage` 里用 `GetProcessesByName("OpenTabletDriver.Slim")` 找/杀旧实例
   （"开第二个时第一个自动退出"就靠这个），改名会让这套逻辑失效，所以不做改名。

最终交付形态（`OTD-Slim-portable.zip`，18.0MB）：

```
OTD Slim\
├─ OpenTabletDriver.Slim.exe     ← 唯一程序文件，43.1MB
└─ data\                          ← 唯一用户数据目录（feel/mapping/buttons/tweaks.json + 各 pid 日志）
```

底包：`_base\v19-tweaks-off_20261116\`（src 765 文件 / publish 112 文件 43.10MB / zip），
附加页八开关默认全关、无计划任务、笔已验证。

## 权限模型（和 AHK 一样：单进程 + 一次 UAC）

- `app.manifest` = `asInvoker`（不是 requireAdministrator），双击不弹 UAC 提示的清单声明；
- `Program.Main` 最前：`if (!IsElevated() && TryElevate(args)) return;`，
  用 `ShellExecute` + `Verb="runas"` 重新拉起自己并追加 `--elevated` 防递归，本体退出；
- 自启只有 HKCU Run 一个入口（`StartupAuto`），历史计划任务 `schtasks /Delete /TN "OTD Slim" /F` 清掉；
- 对比 AHK：`AutoHotKey.ahk` 第 3–14 行也是一次 `Run *RunAs` 自提权 + 单进程，且从不打开 HID 设备；
  旧 OTD 的「计划任务 + Run」双入口才是产生两个实例抢 HID 流的根源。

## 附加功能回归顺序（v19 已回归，八开关默认全关）

`Tweaks / TweaksPage / HotkeyEngine / StartupAuto / WintabSetup / MainWindow` 从备份拷回，
`StartupHigh.cs` **不拷**（计划任务项彻底去掉），`Tweaks.cs` 默认值全改 `false`，
`HotkeyEngine` 增加 `AnyEnabled`：全关时 `Start()` 直接返回并写
`all tweaks off, hooks not installed`，从全关变开启才真正挂钩子。
用户已验证「页面在、钩子未挂」时笔正常。

逐个开启的复测顺序（每个开完都试一次笔）：

1. 反向（自然）滚动
2. 任务栏滚轮控制音量
3. Alt+右键锁轴
4. CapsLock 切输入法
5. Win 键长按
6. 双击 Shift
7. Everything 防护
8. 低延迟模式（0.5ms 计时，最后开，最可疑）
## 性能：上报率与延迟（v21）

### 官方基准值（这次查证的结果）

- Wacom 官方产品规格（Intuos Pro PTH-460/660/860 IPI PDF）：**Maximum report rate 200 pps**，
  压力 8192 级，分辨率 5080 lpi（0.005mm/点）→ 200pps 等于 **5ms 一包**。
  来源：https://cdn-media.wacom.com/-/media/files/downloads/product-support/ipis/pen-tablets/wacomintuospro-2023/ipi-en.pdf
- Microsoft 文档：Windows **系统计时器分辨率默认 15.625ms**，
  见 https://learn.microsoft.com/en-us/windows-hardware/test/assessments/results-for-the-idle-energy-efficiency-assessment
  → 任何 1~5ms 级的 `sleep` / 等待都会被放大到 15.6ms。200pps 落在 15.6ms 的节拍上，
  等于每 3~4 包只处理 1 包。

所以「提高刷新率」的正确说法是：**让软件链路的处理节拍跟上设备 200pps，而不是降低采样**。

### 这一版做了什么（都不降低采样）

1. `Perf.cs`：进程启动就 `timeBeginPeriod(1)`，把多媒体计时器固定在 **1ms**
   （微软文档里的标准档位；旧的「低延迟模式」开关用的是 0.5ms，功耗更高、收益不明显，不采用）。
2. **热路径不再落盘**：`PenButtonPipeline` 原本每条笔报告 `File.AppendAllText` 一行（上限 6000），
   `DeviceReader.RawTrace` 原本每次 `Read()` 写一行（上限 400）。
   实测 `AppendAllText` **0.377ms/次**，200pps 下等于**每秒 75ms 卡在磁盘 I/O**，
   而且会制造抖动 —— 「很卡」的第一嫌疑就是这里。
   现在默认关闭，排查时用环境变量 `OTD_PEN_TRACE=1` / `OTD_RAW_TRACE=1` 打开。
   （历史教训：旧版 `hiddump` 把每包都写进 txt，实测只有 122pps，
   其实是**写日志把读取线程拖慢**，设备并不慢。）
3. 新增真实速率日志：驱动读取层每秒写一行到 `data\rate-<pid>.log`，
   形如 `rate=198.0/s avg=5.05ms p95=6.20ms max=11.0ms total=…`，
   用它判断「设备给了多少」和「软件消化了多少」。
4. 读取线程本身就已是 `ThreadPriority.AboveNormal`（`DeviceReader` 构造里设的），未改。
5. `--rate` 测速模式：只读 HID、只统计，不参与单实例、不提权。
   `OpenTabletDriver.Slim.exe --rate --seconds=10`，结果写 `data\rate.txt`，
   给出 rate / mean / median / p95 / max 和 0-2/2-4/4-6/6-8/8-10/10-16/16-20/20+ms 直方图。
   笔在板面上方悬停或划动时跑，才有效；笔放桌上时只有约 0.7/s 的保活包（实测 4 条/6 秒）。

### 已知的、软件层以外的可疑项（按怀疑度）

1. **蓝牙连接**：PTH-660 走蓝牙时上报率明显低于 USB 的 200pps，参数会抖。优先 USB。
2. **Wacom 驱动/服务同时运行**：`WTabletServicePro` 目前 Stopped（启动类型 Automatic），
   一旦被 Wacom 自己的驱动接管 HID，就会和本程序抢流；确认它是停着的。
3. **Windows「提高指针精确度」**只影响鼠标加速，不影响 Wintab 数据；
   但应用若走 Windows Ink（而非 Wintab）就会落后一级，Photoshop 里应把 Windows Ink 关掉、用 Wintab。
4. **应用自身的笔刷平滑/防抖**（Photoshop 的 Smoothing、SAI 的防抖）会额外加 10~50ms，
   这和驱动无关，但用户体感就是「卡」。

### 待办（需要笔在板上，用户在场时 10 秒就能完成）

跑一次 `OpenTabletDriver.Slim.exe --rate --seconds=10`（笔悬停/划动），
把 `data\rate.txt` 的数字对一下：接近 200pps 说明设备正常，瓶颈在别处；
只有 60~130pps 说明仍被节拍或 I/O 拖慢。
## 自启 / 托盘（v22）

用户三条要求：1) 一定要能自启；2) 点关闭回托盘；3) 自启启动后直接进托盘不弹窗。

实现：

1. **托盘**（`Tray.cs`）：`TrayIcon` + 原生菜单「打开主界面 / 退出」。
   32×32 图标在运行时用 `WriteableBitmap` 画（圆角方块 + 白色笔形斜线），
   不额外带 .ico 资源；同一张位图也设为窗口图标。
2. **关闭 = 收进托盘**：`App` 里 `ShutdownMode = OnExplicitShutdown`，
   `MainWindow.Closing` 里 `if (!Program.QuitRequested) { e.Cancel = true; Hide(); return; }`。
   驱动/设备/钩子继续在后台跑，笔照常可用。真正退出只走托盘菜单「退出」，
   退出路径仍保留原来那个 300ms 强制 `Environment.Exit(0)` 兜底。
3. **自启直接进托盘**：Run 键写成 `"<exe>" --tray`，
   `Program` 解析 `--tray`/`--silent` → `Program.StartupHidden = true`，
   `MainWindow.Opened` 里在设备启动完之后 `ShowInTaskbar = false; Hide();`。
4. **自启自愈**：`StartupAuto.CommandLine` 固定为 `"<当前exe>" --tray`；
   `EnsurePath()` 每次启动都对一遍，路径或参数不对就重写
   （实测原来注册表里残留 `"D:\app\OpenTabletDriver\OpenTabletDriver.Slim.exe"` 无参数，
   换目录后自启必然指向旧位置，这条就是"自启不生效"的根因）。
5. 注意：程序要管理员权限，所以**登录时仍会弹一次 UAC**（除非改成免提权的托盘启动）。
   另外 HKCU Run 里还有一条 `Wacom++`（`wacom-pressure.exe --startup`），
   那是另一个压力注入器，和本驱动会抢同一路输入，建议只留一个。
## 免 UAC 运行（v23）

用户要求：DLL 装好之后就不该再弹 UAC。

`Program.Main` 的提权判断改成"只在真需要时才提权"：

```csharp
if (!probeMode && !IsElevated())
{
    var dllReady = WintabSetup.Detect() == WintabState.Ours;
    var others  = Instances.OthersRunning();

    if (!dllReady) { if (TryElevate(args)) return; }        // 要写 System32，必须管理员
    else if (others)
    {
        Instances.KillOthers();                              // 普通权限能杀的先杀掉
        Thread.Sleep(400);
        if (Instances.OthersRunning() && TryElevate(args))   // 高权限旧实例杀不掉才提权
            return;
    }
}
```

- `WintabSetup.Detect()` 用 SHA256 比 System32 里的 dll 和随包 dll：
  实测两者 `7F0621EA…5106DB` 一致 → 判为 `Ours` → **不再提权**。
- 需要装/覆盖 dll 时，`WintabSetup.Install()` 本来就有自己的 `runas` 分支，
  只给"复制 dll"这一步提权，不需要整个程序以管理员跑。
- 顺便加 `Instances.OthersRunning()`：只在真有别的实例时才考虑清场。
- 结果：DLL 已装 + 没有旧实例 = 开机自启全程零 UAC，直接进托盘。
  唯一还会弹 UAC 的场景是"上次的提权实例没退出"（普通权限杀不掉它），那一次清完就不再弹。

实测：普通权限直接跑 `--rate`（probe 模式不提权）能正常打开 PTH-660 的 HID 口并读到报告，
说明读取链路本身不需要管理员。

## 实测上报率（2026-11-16 18:55，普通权限裸读，无任何落盘）

```
reports=1704 rate=113.6/s  emptyReads=0
mean=8.90ms median=5.98ms p95=7.01ms max=2001.00ms
hist(ms): 0-2=108 2-4=445 4-6=521 6-8=618 8-10=2 10-16=3 16-20=0 20+=6
```

结论：

1. **稳定段约 6ms 一包（≈166pps）**，不是官方上限 200pps，但远高于 60Hz ——
   设备本身不慢，之前"只有 122pps"是 hiddump 自己写日志拖出来的。
2. 分布里有 445 次落在 2-4ms，说明**报告是成串到达的**（队列积压后一口气取回），
   这就是"笔跟手但偶尔一顿"的来源。旧版 v21 每条报告都 `AppendAllText`（0.377ms/次）
   正是制造这种积压的元凶，v21 起已默认关闭。
3. `20+=6` 和 `max=2001ms` 出现在开头，是 `SetFeature(0x02,0x02)` 之后设备初始化那一下，
   属正常冷启动空洞。
## SAI 里"下笔位置和光标不一致 / 有两套光标"（v24）

### 根因：同一时刻存在两条坐标链路，各自用了不同的映射

| 链路 | 坐标来源 | 映射方式 |
|---|---|---|
| 系统光标（OTD 自己的鼠标指针） | OTD `AbsoluteOutputMode` | 板子活动区 224×126mm → 显示器 3840×2160 |
| shim 注入的合成 PEN（应用从它读压感/位置） | `inject_pen_tick` | **整块板子 44800×29600 → 整个虚拟桌面** |

两条链路的分母不同，所以越往边缘偏得越多；SAI 又自己按 `lcSysExt` 算一遍光标（shim 里原本就把 `sys_ext` 报成整个桌面），于是屏幕上出现两个位置不一致的光标。这就是"SAI 的光标定位比实际大"。

### 修法：把真实映射发布给 shim，两条链路共用一套

- 共享内存（`Local\OpenTabletDriverWintab`）扩到 version 2，在**数据环之后**追加 32 字节映射块：
  `in_org_x/y`、`in_ext_x/y`（板子活动区，设备单位）、`sys_org_x/y`、`sys_ext_x/y`（显示器矩形，虚拟桌面像素）。
  放在环之后而不是中间，是为了让 System32 里残留的旧 `Wintab32.dll` 仍然只读 offset 32 起的环，不会被写坏。
- 应用侧 `WintabOutputMode.UpdateWintabMapping()`：**实测**而不是反推 —— 把三个板子坐标丢进 `AbsoluteOutputMode.Transform()`（就是移动指针的那段代码），由结果算出斜率、原点、跨度，所以换显示器/锁定比例/左右手旋转都自然跟着走。
- shim 侧：`apply_mapping_a/w()` 用这份映射填 `in_org/in_ext/out_org/out_ext/sys_org/sys_ext`；`inject_pen_tick()` 也用同一份映射算像素，合成笔和鼠标指针落在同一个像素上。

### 实测日志（`data\otd-wintab-22992.log`）

```
TABLET name=Wacom PTH-660 digitizer=44800x29600 penMax=8191 wintabMax=32767
SHM created name=Local\OpenTabletDriverWintab 44800x29600 maxP=32767 size=24640
MAPPING to shim tablet=(0,0)+44800x25200 screen=(0,-0)+3840x2160 slope=0.086,0.086
```

`mapping.json` 里活动区是 224×126mm（锁定比例后板子上下各让出 11mm），换算成设备单位正是 44800×25200；
显示器是 3840×2160@(0,0)。25 200 × 0.086 = 2160，两边完全自洽。

### 已知边界

- 活动区之外（板子上下各 11mm）仍在报包：指针被 `AreaClipping` 卡在屏幕边缘，而应用按外推坐标继续算，那一条窄带里两者会分开。
- 应用若把数位板映射到**整个虚拟桌面**而不是某一块显示器（SAI 的"全屏"就是这种），多显示器时仍会不一致；单显示器时两边重合。
- 必须**同时**更新 exe 和 System32 里的 `Wintab32.dll`：旧 dll 不认识 version 2，会继续按整个桌面映射。新版 exe 一旦以管理员身份启动（dll 版本不对时它会自动提权）就会顺手把 dll 覆盖成新版本，不需要再点一次。
## "无法替换 Wintab32.dll" 的根因（v25）

用户点"安装/覆盖"一直失败。查下来不是一个原因，是三个叠在一起：

1. **安装脚本编码读错（真凶）**。脚本写到 `%TEMP%` 时用的是 UTF-8 **不带 BOM**，
   Windows PowerShell 5.1 按 ANSI 读，把 `C:\Users\画画的\…` 读成 `C:\Users\鐢荤敾鐨刓…`，
   `Copy-Item` 直接 `DirectoryNotFoundException`，日志干脆写不出来 —— 界面上只看到"安装失败"。
   修：脚本改成 UTF-8 **带 BOM** 写盘，并改用 `-File <脚本路径>` 而不是 `-Command "<内联长串>"`。

2. **dll 正被应用加载时任何人都覆盖不了**。`sai2.exe` 把 `C:\Windows\System32\Wintab32.dll`
   映射成映像之后，即使有管理员权限，`Copy-Item` 也是 `ERROR_SHARING_VIOLATION`。
   修：`WintabSetup.Holders()` 枚举"谁正加载着这个 dll"，失败时直接点名（"正被 sai2 占用，关掉它再点一次"）；
   并在提权覆盖仍失败时自动改走**重启时替换**：把新 dll 放到 `System32\Wintab32.dll.new`，
   再用 `MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)` 登记，重启后系统自己换过去，不用关掉正在画画的程序。

3. **装好了也要重启应用才生效**。dll 是进程启动时加载的，SAI/PS 开着的时候换文件，
   它们内存里还是旧的那份。

新增状态 `WintabState.Pending`（"已安排重启后替换 · 重启生效"），
它同时被当成"就绪"来处理 —— 排队等重启期间不会每次开机都弹一次 UAC。

### 验证方式（不弹 UAC 就能跑完整条路径）

```
set OTD_WINTAB_SYSTEM_DIR=<任意普通目录>   # 把"安装目标"指到别处
set OTD_WINTAB_NO_ELEVATE=1                # 不提权，直接在当前权限跑脚本
OpenTabletDriver.Slim.exe --wintabtest     # 结果写到 exe 旁的 wintabtest.txt
```

实测结果：

```
# 目标可写
state before : Outdated  install : ok=True 已经是最新版本?  → 后为 Ours

# 目标被别的进程锁住
state before : Outdated (版本不是本程序的)
install      : ok=False 写入 System32 失败：…being used by another process
脚本日志     : MoveFileEx failed, win32=5     ← 参数/路径都对了，只是这里没管理员权限
目录里       : Wintab32.dll 1024 + Wintab32.dll.new 198144   ← 排队文件写成功了

# 伪造成"已排队"
state before : Pending (已安排重启后替换 · 重启生效)
install      : ok=True 已安排好，重启后生效
```

`--wintabtest` 会一并打印 `holders`，用来确认是谁占着这个 dll。
## "双击没反应 / 没有窗口 / 没有托盘" 的根因（v27）

`Instances.KillOthers()` 结束旧实例的两段式里，第二段写的是：

```
taskkill /F /IM OpenTabletDriver.Slim.exe
```

`/IM` 是**按进程名**杀 —— 把自己也算进去。于是只要有旧实例杀不掉，
本进程就把自己一起杀掉：双击后进程秒退、没有窗口、没有托盘、笔也不动，
连异常日志都留不下（因为在途的 taskkill 先把自己干掉了）。

三个叠加问题：

1. **自杀**：`/IM` 改成按 `/PID` 一个个补刀，且永不把自己算进目标。
2. **无限叠加**：以前"发现已有实例"就提权再起一份，提权起来的那份普通权限杀不掉，
   双击几次就堆一堆后台实例，互相抢同一路输入 —— 表现就是"识别到笔、动一下、然后停止"。
   现在改成**单实例唤醒**：后开的进程往 `Local\OTDSlim.Show` 事件发个信号，
   让已有实例把窗口显示出来，自己立刻退出（`--tray` 自启时不弹窗，静默退出）。
   永不叠加第二个驱动。
3. **静默死亡查不到原因**：新增 `crash-<pid>.log`（AppDomain / Main 兜底）
   与 `app-<pid>.log` 的启动打点（`run start` / `after single-instance` / `avalonia exited`）。

实测（v27，装在 `D:\app\OpenTabletDriver`）：

```
run start elev=False args=
after single-instance others=False
[Info] Detect: Found tablet 'Wacom PTH-660'
[Info] SlimHost: MAPPING display=3840x2160@1920,1080 tablet=224.0x126.0@112.0,74.0 ...
[Info] SlimHost: Wintab 输出已启动（含按键管线）
[Info] 按键: 笔尖 → 左键单击        ← 笔已经真的在出数据

第二次启动（窗口已存在）：
run start elev=False args=
woken existing instance, exiting     ← 退出码 0，窗口被叫到前面，没有叠第二个驱动
```

遗留：机器上还留着 8 个 16:08–16:47 之间提权启动、卡住的旧实例，
即使提权 `taskkill /F /PID` 也杀不掉（`--killothers` 报 killed=0）。
这些只能靠重启清掉；重启后自启只剩一份，不会再出现。
## 托盘图标为什么一个都没有（v28）

Avalonia 自带的 `TrayIcon` 在裁剪后的单文件版本里没真正注册 —— 托盘对象是建出来了，
但进程里根本看不到 NotifyIcon 窗口，`Shell_NotifyIcon` 也没被调用成功。
用户点 X 之后窗口被 `Hide()`，任务栏没了、托盘里也没有，等于窗口凭空消失。

改成自己调 `Shell_NotifyIcon`（`Tray.cs` 重写）：

- 单独线程建隐藏窗口（类名 `OTDSlimTrayWindow`）、跑消息循环，与 Avalonia 的消息循环互不干扰；
- 图标在运行时画成 32×32 的 `tray.ico`（写在 `data\` 下），再用 `LoadImage(..., LR_LOADFROMFILE)` 读回来当 HICON ——
  `CreateDIBSection` + `CreateIconIndirect` 在这台机器上返回 NULL（err=0），这条路走不通；
- 左键单击/双击 → 显示主窗口；右键 → 弹出原生菜单「打开主界面 / 退出」；
- 监听 `TaskbarCreated`，Explorer 重启后自动重新注册图标；
- `Shell_NotifyIcon` 的成败写进 `app-<pid>.log`（`[Tray] Shell_NotifyIcon add ok=True`），以后不用靠猜；
- **兜底**：托盘没注册成功时点 X 不再隐藏窗口，而是最小化成普通任务栏窗口 —— 绝不会再把窗口藏丢。

实测（v28）：

```
[Tray] Shell_NotifyIcon add ok=True hIcon=True
窗口里出现 OTDSlimTrayWindow                  ← 托盘窗口真的建起来了

WM_CLOSE（等于点 X）：
  窗口 visible=False，进程存活=True           ← 收进托盘，驱动继续跑

再双击一次：
  新进程立刻退出，窗口 visible=True           ← 唤回原窗口，不叠第二份驱动
```
## "用笔点托盘图标的一瞬间笔就不动了"（v29）

`MainWindow.Opened` 里原来是"每次窗口显示就检测一遍数位板 + 起输出"。
Avalonia 的 `Opened` 在**隐藏后重新 Show** 时会再触发一次 —— 点 X 收进托盘、
再用笔点托盘图标把窗口叫出来，就正好走到这个分支：

    DetectAsync() 再跑一遍 → 再打开一次 HID → 正在跑的读取线程被顶掉 → 笔的坐标流断了

表现就是"窗口出来的一瞬间笔不能动"。而普通权限下新开的句柄读不出数据，
所以不是"卡一下"，是彻底不动。

修法：`Opened` 里的初始化改成**只跑一次**（`_initialized` 标志），
之后每次重新显示只 `Refresh()`，不碰设备。`--tray` 自启时的隐藏也只做一次，
否则唤醒后窗口会被立刻又藏起来。

实测（v29）：

```
启动后： Detect: Searching for tablets 出现 1 次，托盘 [Tray] Shell_NotifyIcon add ok=True
点 X：   进程存活=True 窗口可见=False
再双击： 新进程退出，窗口可见=True
         检测次数仍然是 1（没有重新打开 HID）
         日志里接着出现 [Info] 按键: 笔尖 → 左键单击   ← 笔还在正常上报
```
## 标题栏改成深灰（v30/v31）

仍然用系统标题栏（圆角、投影、拖动、最小化动画都还是 DWM 自己画的），
只通过 `DwmSetWindowAttribute` 指定颜色（`WindowTheme.cs`）：

- `DWMWA_USE_IMMERSIVE_DARK_MODE(20)` = 1 —— 让右上角三个按钮的图形变白；
- `DWMWA_CAPTION_COLOR(35)` = `0x003A3A3A`（深灰 #3A3A3A，COLORREF 是 0x00BBGGRR）；
- `DWMWA_TEXT_COLOR(36)` = `0x00F0F0F0`（近白标题文字）。

Win11 22H2+ 三个属性都生效，标题栏就是指定的深灰；Win10 只认第 20 号，
于是退化成系统自己的深色标题栏（也是白字），不会变成白底黑字那种突兀的样子。

内容区依旧是浅色 —— 这不是深色模式，只把标题栏压深。

实测（本机 Win11 22631，v31）：

```
[TitleBar] dark=0/0 caption=0 text=0     ← 四个 DwmSetWindowAttribute 全返回 S_OK
```
## 压力页：把「WinTab / Windows Ink」换成「接管 / 恢复 Wacom」（v32）

原来那对胶囊是个死开关。精简版只保留 Wintab32.dll 注入这一条输出链路，
没有 Windows Ink 那条路 —— `FeelConfig.InputMode` 除了打一行调试日志再没人读它，
所以点「Windows Ink」切过去不会发生任何事情。

真正会跟这套驱动抢笔的是 Wacom 自己的服务。现在这个位置换成一对动作
（`WacomControl.cs`）：

- 没装 Wacom 驱动 → 显示「未检测到 Wacom」，置灰不可点；
- 装了且没接管 → 「接管 Wacom 压力」；
- 已接管 → 「恢复 Wacom 控制」。

接管做的事：把 Wacom 的用户态进程（Wacom_Tablet / 桌面中心等）关掉，
`sc stop` + `sc config <服务> start= disabled`；原来每个服务的启动类型先记进
`data\wacom-state.ini`（`服务名=Start数值`，比如 `WTabletServicePro=2`）。
恢复就是按记录的原值 `sc config start= auto|demand` ＋ `sc start`，成功后删掉记录。

只动用户态服务（`Type & 0x30`），不碰内核驱动：这台机器上
`WacomPen` / `WacHidRouterPro` / `wacomrouterfilter` 都是内核驱动，
禁用要重启才生效，硬改反而把 Wacom 装好的驱动弄坏。

脚本用 UTF-8 **带 BOM** 写到 `%TEMP%` 再用 `runas` 跑（和装 Wintab32.dll 同一套写法），
结果读 `%TEMP%\otd-wacom-<HHmmss>.txt` 回填到胶囊下面一行，不弹窗。

成败不看脚本自述、回读注册表确认：非管理员跑的时候 `sc` 会静默失败，
只看日志会误判成"接管成功"——这一条实测过（`OTD_WINTAB_NO_ELEVATE=1` 下第一次
报了 ok=True，加了回读校验之后报 ok=False 并提示没权限/取消 UAC）。

CLI 自测：

```
OpenTabletDriver.Slim.exe --wacomlist          # 只查：列出服务、是否已接管
OpenTabletDriver.Slim.exe --wacomtake          # 真跑接管（会弹 UAC）
OpenTabletDriver.Slim.exe --wacomrestore       # 真跑恢复（会弹 UAC）
```

实测（本机，v32）：

```
--wacomlist → detected : True  taken over : False
              services : WTabletServicePro(Wacom Professional Service) Start=2
界面启动日志 → [Wacom] ui detected=True taken=False
```

退出程序不会自动恢复 Wacom 服务 —— 接管是你点的，恢复也要你点。
## 「压力」页改叫「输入」，底部加触摸控制开关（v33）

导航和页面键名从「压力」改成「输入」（`MainWindow.cs` 里 4 处 + `ShowPage` 默认值）。

压力校准那块画板高度 240 → 192（减 20%），行索引不变。

新增「触摸控制」区块：撤销 / 旋转 / 缩放 三行，右侧各一个开关
（复用侧栏开机自启那只 `ToggleSwitch`，OnContent/OffContent 清空、MinWidth 36），
值写进 `feel.json` 的 `TouchUndo / TouchRotate / TouchZoom`。

**这三个开关目前只存设置，还没有接动作。**（v34 起已接上：环能用，双指手势仍无数据，详见下面「触摸控制：环能用了…」一节） 查了原始 OTD 项目（0.6.x）：

- `Configurations\Wacom\PTH-660.json` 里确实声明了触摸环
  （`Wheels: AbsoluteWheelMax=71, AngleOfZeroReading=90, ButtonCount=1`）
  和触摸面（`Touch: MaxX=8959, MaxY=5919`）；
- 解析器 `Parsers\Wacom\IntuosV2\IntuosV2ReportParser.cs` 认得
  `0x11`（辅助键 + 环绝对位置 + 环按钮）、`0x21 / 0xD2`（最多 5 点触摸坐标）；
- 但也仅此而已 —— OTD 没有手势识别，也没有"撤销/旋转/缩放"这三个动作，
  Windows 端还得自己把环/触摸合成成按键或滚轮。

也就是说这三个开关背后要新写一条触摸管线（打开那路 44 字节的辅助 HID → 解析环位置 /
触摸点 → 合成 Ctrl+Z、缩放、旋转），不是勾一下就有功能。

实测（v33，本机）：窗口起来正常，`[Wacom] ui detected=True taken=False`，
页面上「输入 / 映射 / 按键 / 附加 / 设备 / 关于」、校准框变矮、底部三个开关都在。

## 触摸控制：环能用了，双指手势仍然没有数据（v34–v38 实测）

v33 那三个触摸开关后面确实接上了动作，但结果分裂成两半：

| 输入 | 现象 | 结论 |
| --- | --- | --- |
| 触摸环（环位置、环按钮） | 能用，滚动/音量等动作正常 | `0x11` 报告解析正常 |
| 双指缩放 / 旋转 | 完全没有反应 | 从未收到 `0x21` / `0xD2` 报告 |
| 三指撤销 | 完全没有反应 | 同上 |

日志里只出现 `0x11` / `0x13` 两类报告，`0x21`（触点坐标）和 `0xD2`
一次都没出现。也就是说数位板根本没有把触摸点送上来，或者被 `Wacom_TouchUser`
截住了。接管 Wacom 时这个进程会被杀掉，接管后是否恢复触摸报告 **未实测**。

这条不再是当前阻塞项，先记在这里。


## Wacom 接管的正确顺序（v34 修正）

原来的顺序是"停服务 → 杀进程"，结果经常出现"接管成功但笔没接管到"。
真因：Wacom 的用户态进程（`Wacom_Tablet`、桌面中心等）在退出前会把服务的
启动类型改回 `Automatic`，于是 `sc config start= disabled` 被它覆盖掉，
服务过一会儿又自己起来，和 OTD 抢设备。

改成四步，并且每一步都要复核：

```
1) 先杀进程（TaskKill 全部 Wacom 用户态进程）
2) sc config <服务> start= disabled   +   sc stop <服务>
3) 再补杀三轮进程（每轮之间 sleep 300ms，处理第 1 步之后新起的）
4) 回读注册表 Start 值，确认是 4（disabled）
```

实测（本机）：按这个顺序走，接管后 Wacom 服务不再自启，OTD 独占设备；
恢复按 `data\wacom-state.ini` 里记的原值写回即可。

注意：只动用户态服务（`Type & 0x30`），不碰内核驱动
（`WacomPen` / `WacHidRouterPro` / `wacomrouterfilter` 禁用要重启，硬改会把
Wacom 装好的驱动弄坏）。


## SAI 2（x64）定位/压力问题：一次把假设全部推翻的排查（v39）

### 现象

SAI 2（64 位）里两个独立现象，而且**会随 shim 版本互相翻转**：

1. 笔刷预览光标比系统指针高约 15%（映射区放上方时观感约 7cm，27 寸屏）
2. 某些版本下 SAI 干脆"画不出"，但 PS 侧压力正常

早期（v24）修过"下笔位置和光标不一致"，当时已经定位为"两条坐标链路各用一套映射"，
并把真实映射发布给 shim 修好了。所以这两个现象 **在加入 Wacom 接管之前并不存在**。

### 先确认数据到底有没有送到（关键，省掉大量瞎猜）

给 shim 加 `WTOpenW` 诊断打点（窗口客户区、窗口屏幕矩形、上下文 in/out/sys），
读 SAI 的 `wintab-sai2-<pid>.log`：

```
WTOpenW hwnd=0x240788 client=2831x1816 win_rect=(854,182)-(3695,2064)
        ctx in=(0,0)x(44800,25200) out=(0,0)x(3840,2160) sys=(0,0)x(3840,2160)
PKT serial=4600 mask=0x15E2 len=36 x=16168 y=16939 raw=1547 np=1547 in_range=true
```

包内实际值（hex 解出）：`x=16168, y=8261`（SAI 自己算 `25200-16939=8261`），
`np=1547`。位置、压力、掩码、长度、序列号 **全部正确送达**。

结论：**数据链路没有问题**，"画不出"是 SAI 自己的落笔判定。

### SAI 到底读什么（有日志为证）

```
cat=100 idx=1     → DVC_NAME（看到 "OpenTabletDriver Pen"）
cat=1   idx=1..6  → 接口版本
cat=400..406      → 7 个默认上下文（DEFCONTEXT / DEFSXTCONTEXT / DEFCURSOR…）
DVC idx=15        → **只有 NPRESSURE**，从不读 DVC_X / DVC_Y
```

所以 SAI 的位置**完全**由上下文结构（`lcInOrg/lcInExt/lcOutOrg/lcOutExt/
lcSysOrg/lcSysExt`）决定，改设备轴对它的位置显示毫无作用——这也解释了为什么
"改 DVC_Y 会让它不出笔" 是个副作用，而不是它在用那个值。

### 这一轮试过、并且走不通的方向（别重试）

| 实验 | 结果 |
| --- | --- |
| `out_org/out_ext` 从板子范围改成屏幕范围 | SAI 压力与压力指示点一起错乱 |
| AXIS 结构补 `axName[32] + axType`（84 字节） | 不影响压力，单独保留 |
| `device_extent()` 改报映射区 `in_ext`（`DVC_Y=25200`） | SAI 完全不出笔（复现 2 次） |
| `PK_STATUS` 置 `TPS_PROXIMITY` | 无效，仍无压力 |
| 压力量程改 8191（PTH-660 真机规格） | 无效 |
| 包改"映射区内相对坐标"（X 减区原点、Y 翻转后再减、`in_org` 报 0） | 一次"能画出但仍错位"，同类组合另一次"画不出" |
| exe 侧（v39）：`Publish` 减区原点 + `SetExtent` 改 header 宽高 + `in_org` 报 0 | 仍"画不出" |
| `inject-pen.txt`（合成笔） | 光标来回闪，SAI 仍无压力（文件已删） |
| `force-pressure.txt` | PS 只出最大压力（文件已删） |

### 收敛到的判断

- 值是对的：`in=(0,0)×(44800,25200)`、`out/sys=(0,0)×(3840,2160)` 是标准写法，
  和真驱动一致
- 同一份**正确**数据下，SAI 的落笔判定会随 shim 版本翻转 → 它在按"驱动身份/整板规格"
  做校验（疑似要求 `DVC_Y=29600` 这类整板画像），而不是按数值算位置
- SAI 用的是**窗口客户区**（2831×1816 @ 854,182）再换算到屏幕，
  所以预览光标偏移量是一个"固定高度差"，与压力、与映射比例都不是同一条链路

### 最终状态（2026-11-16 23:20）

- `C:\Windows\System32\Wintab32.dll` = **v32 原版**，`198144` 字节、`19:09:48`、
  hash `5EACB0033C59751F`（来源 `slim\dist-v32\OTD Slim\data\Wintab32.dll`）
- 本轮所有 shim 实验改动已撤出；`wintab-shim\src\lib.rs` 里仍保留诊断打点与
  实验代码（未装），要回退直接 `cargo build --release` 会产出实验版，别再装
- `D:\app\OTD-v39`（exe 侧坐标修正版，`45,272,470` 字节）留着，进程已停
- 临时规避：把映射区拖到屏幕**下方**，预览光标那 7cm 偏移就压到可接受范围

### 如果以后还要彻底解决

不要继续调坐标数值。唯一的合法途径是 **伪造一份更完整的 Wacom 规格画像**，
从这三处入手：

1. `DVC_NAME` / `DVC_PHYSID`（让 SAI 认成 Intuos Pro）
2. 光标能力表（`WTI_CURSORS` 的 `CSR_*` 字段，包括品牌/按钮集合）
3. 整板规格（`DVC_X/DVC_Y` 报真实整板 `44800×29600`，配合映射区 `in_org/in_ext`
   表达有效区，而不是把板子缩小成映射区）

这是另一个量级的工程，要单开一轮做，不要在"调坐标"的循环里继续试。

### 备份链（System32 里可直接回退）

`Wintab32.old-20261116-1909/2155/2200/2207.dll`、
`Wintab32.exp-20261116-2222/2225-outsys/2234/2307/2317.dll`、
`Wintab32.base-20261116-2225.dll`（= v32 原版）、
`Wintab32.diag-20261116-2307.dll`（带窗口诊断日志）、
`Wintab32.prev-20261116-2312.dll`、`Wintab32.rel/relcoord/axischg/park/abc/v39plain/z77/z78/z79-*.dll`

带 `exp-` 前缀的都是失败的实验版，别装；要回到能用状态装 `base-` 或 `dist-v32` 里那份。