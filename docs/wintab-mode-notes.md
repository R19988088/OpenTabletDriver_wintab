# OTD Wintab 模式调试记录（2026-11-14）

## 一、已解决：OTD 探测不到板子的两个配置错误

设备：`USB\VID_056A&PID_0357`（Wacom Intuos Pro M / PTH-660）

用 HidSharp（OTD 实际用的库，`hidsharpcore 1.3.0`）枚举到的真实值：

| 集合 | InputReportLength | OutputReportLength | Feature | CanOpen |
|---|---|---|---|---|
| `mi_00&col02`（厂商 0xff00:10，笔数据） | **193** | **161** | 1024 | True |
| `mi_00&col03`（笔，Windows Ink） | 38 | 0 | 2 | **False（被内核独占）** |
| `mi_00&col04`（键盘） | 38 | 0 | 0 | True |
| `mi_02&col02`（辅助键） | 44 | 161 | 1024 | True |

内置配置 `PTH-660.json` 里 digitizer 写的是 `InputReportLength: 192` + `OutputReportLength: 0`，
而 OTD 的匹配逻辑是严格相等（`Driver.cs:180-181`），两项都对不上 → `No tablets were detected.`

**修正后**（外部覆盖文件：`portable-build\userdata\Configurations\Wacom\PTH-660.json`）：

- `InputReportLength: 193`
- 删除 `OutputReportLength: 0`（留空即不参与匹配）
- `FeatureReportLength: 1024` + 把 `FeatureInitReport` 的 `AgI=` 补零到 1024 字节

结果：日志出现 `Found tablet 'Wacom PTH-660'`、`Wacom PTH-660 :: Pressure: Enabled`，
共享缓冲区 `maxP` 由 8191 变为 **32767**（插件按 shim 要求配置成功）。

## 二、未解决：拿到设备但收不到笔数据

日志：`Failed to set device feature: 02-02`（Level 2 警告）。
用 HidSharp 直接对 `col02` 试 SetFeature，各种长度（2 / 161 / 162 / 1024）全部失败；`Write` 全部报“参数错误”。

推断：Wacom 的内核过滤驱动仍在设备栈上——它既独占着笔集合 `col03`（err=32），
也拦掉了厂商集合上的特性/输出报文。设备因此一直停留在 Windows Ink 模式，
不会把笔数据发到 `col02`，OTD 也就发布不出 Wintab 包（`writeIndex` 恒为 0）。

**结论**：这条路线必须先禁用/卸载 Wacom 驱动（设备管理器操作，需要管理员权限），
普通权限下命令行（`sc stop` / `pnputil` / Device Manager）全部 Access denied。

## 三、当前可用状态

- SAI / PS 目录里的 `Wintab32.dll` 已换回官方 Wacom 版（`CA27BE97AD`，2.3MB，Wacom Technology 签名）。
- 关掉 Wacom 用户态进程（`Wacom_TabletUser`）后，笔走 Windows Ink，SAI 自动切到 Ink 通道，压感正常。
- 我们的 shim 作为 `Wintab32.shim-z65.dll` 保留在同目录，插件 `42F4CF3E` 已部署到
  `%LOCALAPPDATA%\OpenTabletDriver\Plugins`。

## 四、拿到管理员权限后的下一步（按顺序）

1. 设备管理器禁用 `Wacom Tablet`（或卸载 Wacom 驱动），让出 `col03` 与 `col02` 报文通道。
2. 重启 `OpenTabletDriver.Daemon.exe`（工作目录 `portable-build`），确认日志有 `Found tablet`。
3. 用笔在板子上划，观察缓冲区是否推进：
   `python otdcheck2.py`（读 `Local\OpenTabletDriverWintab` 头 32 字节的 `writeIndex`）。
4. 把 `Wintab32.shim-z65.dll` 复制回 `Wintab32.dll`（SAI / PS 目录），重启应用验证压感 0–32767 与单击。
5. 若第 3 步仍不动，再看日志里 `Failed to set device feature` 是否消失；
   消失但无包 → 问题在 IntuosV2 解析/发布路径，可直接改
   `src\OpenTabletDriver-0.6.x\OpenTabletDriver.Wintab\WintabPointer.cs`。

## 2026-11-14 晚：压力为什么到不了 SAI / PS

**已证实（数据在案）**
- shim 报文本身正确：SAI 上下文 `pkt_data=0x15E2`、`width=36`、`pressure_at=20`，包里实测 `np=5653`，峰值 `11317`，`misses=0`。与官方 `official-15E2.txt` 的字节布局逐字段一致。
- 强制满压（`force-pressure.txt`）时 SAI 收到 `np=32767` 240 个包，画面仍无任何压力反应 → **SAI 不消费 Wintab 的 pkNormalPressure**。
- 因此 SAI / PS 的压力来自系统指针（笔设备），与 Wintab 无关；Wacom 驱动在位时系统里有真笔，所以它们有压力。ProCraft 是纯 Wintab 消费者，一直正常。

**系统笔（合成笔）路线**
- `CreateSyntheticPointerDevice(PT_PEN)` + `InjectSyntheticPointerInput` 能让 `GetPointerDevices` 从 0 变 1（type=1 Integrated Pen），指针确实进入系统。
- 但实测有严重副作用：光标在“笔位置”和左上角之间高频往返；笔杆键被误触发（`POINTER_PEN_INFO` 少写 `penFlags` 字段导致 `penMask` 被当成 flag，bit0=PEN_FLAG_BARREL→右键）。
- 已修正 `penFlags`、已补 himetric 坐标、已关闭 POINTER_FEEDBACK 动画、已降到 60Hz：**闪烁仍未消除**。
- 结论：合成笔方案在当前实现下不可用，暂时关闭。独立进程 `pen_injector.exe`（`src/bin/pen_injector.rs`）保留备用，由 `inject-pen-exe.txt` 开关控制。

**当前开关状态（全部关闭）**
- `%LOCALAPPDATA%\OTDWintab\` 下 `force-pressure.txt` / `zero-xy.txt` / `pen-only.txt` / `inject-pen.txt` / `inject-pen-exe.txt` 均已删除。
- 桌面指针恢复为 OTD 鼠标注入（`WintabPointer` 的 pen-only 开关为关）。
- shim 已装 z71（`C4D1638E4D`，173568B）到 SAI 与 PS 目录；插件为 `3B07629DE3`（含 pen-only 文件开关）。

**可选下一步**
1. 继续调合成笔（需要解决光标双位置，可能要试 `ptHimetricLocation` 映射与 `POINTER_FLAG_NEW`）。
2. 回滚：`pnputil /add-driver "E:\000 app\OpenTabletDriver\driver-backup\wachidrouter_pro.inf" /install`，恢复 Wacom 驱动 → SAI/PS 立刻恢复压感，但 OTD 无法再读板子。
## 2026-11-14 晚：PS 切窗口回来只能画一个点 —— 根因是队列积压

现象：切到别的窗口再切回 PS，笔能点出一个点，但移动不出线；在手感面板上划几笔后 PS 又自行恢复。

定位（靠 z74 新增的每秒 `gets=` 计数分出方向）：
- 出问题时 PS 仍在高频取包（正常 185 次/秒），说明不是"我们不投递"；
- 同一秒出现过 `samples=349 / msgs=11`：生产了 349 个包只发了 11 条 WT_PACKET 通知——旧通知逻辑"只在队列里还积着包时"才发，而 PS 取包太快、两次通知之间就把队列取空，通知线程于是永远不发；
- 队列上限当时是 `MAX_QUEUE = 256`，200Hz 下等于 1.3 秒旧历史，PS 恢复时把整段旧坐标一次性回放，画面就只剩一个点。

z75：通知改为"只要生产了新包就发 WT_PACKET"，不再依赖队列是否积压。
z77（最终）：不再排队——新样本到达时，若队列里还有**没被取走**的包，直接丢弃替换成最新（`queue.truncate(read)`），队列最多只有一个待取包；`MAX_QUEUE = 16` 仅作已交付包的历史，供 `WTPacket(serial)` 回查。

结果：ProCraft 正常；PS 切窗口回来可连续画线，不再只出一个点。SAI 2 仍无压力输出，暂时搁置。
## 2026-11-14 晚：显示映射"只在上面那块屏"的根因 —— EnableClipping 被改成了 false

- 现象：笔的坐标只在上面那块显示器（\\.\DISPLAY2，Windows y 为负）能控，下面那块（DISPLAY1，主屏 0..3840/0..2160）不动。OTD 界面里显示区域看起来是对的，daemon 日志也自证 Display area: [3840x2160@<1920,1080>]。
- 误判过程：曾据"光标读数 Y 为负"推断 OTD 的显示区 Y 与 Windows 屏幕坐标反向，手写过 3269x2160@(1920,1080) 和 Y=-1080 两种坐标——都被否决，且都不是病因。教训：光标位置不能证明映射方向（笔不在感应区时它停着不动），不要据此反推。
- 真正根因：把 11-13 的旧配置与本轮对拍，两份 settings.json 只有一行不同——
  "EnableClipping": false（本轮被我改坏）vs true（11-13 原始值）；
  其余 Display/Tablet/LockAspectRatio/LockUsableArea* 全部一致。
- 结论：EnableClipping=true 时 OTD 把板子映射覆盖整块显示区（上下两块屏都能命中）；关掉后只剩上面那块响应。改回 true 后恢复正常。
- 恢复方式：直接用 E:\000 app\OpenTabletDriver\userdata\settings.json（11-13 原版，3031B）覆盖 portable-build\userdata\settings.json；被覆盖的那份留在 backup\settings-before-clipping-fix.json。
- 验证口径：看 daemon 最新日志最后几条 "Clipping: Enabled"（注意日志是追加的，要取最后几次而不是第一次出现）。
## 2026-11-14 夜：z78 —— 切走窗口期间不再按 250Hz 空发 WT_PACKET

- 症状：切到别的窗口待一会儿再回 PS，PS 不认笔（或压力只剩一次单击 100%）。
- 现场证据（wintab-Photoshop-23824.log 尾部）：一个 93 秒的空窗里 shim 只产生 4 个样本，PS 却 get 了 18248 次；队列本身健康（q=0/0、misses=0）。所以积压不在 shim 队列里。
- 推断：真正的积压在应用的**消息队列**——我们的通知线程无论应用有没有在取包，都按 4ms tick 发 WT_PACKET。窗口切到后台时它的消息循环被挂住，消息照收不处理，于是堆到几百上千条；等它回来逐条问 WTPacket(serial)，而这边队列早已被 truncate 换成最新包，绝大多数 serial 查不到，PS 拿到的是空/旧状态，表现为"不认笔、只剩一下"。
- z78 改法（消费者感知的通知节流）：
  - Context 新增 last_consume_ms / last_notify_ms / suppressed_posts；
  - WTPacket 与 WTPacketsGet 每次调用刷新 last_consume_ms（这就是"应用真的在取包"的唯一可靠证据）；
  - 通知线程：距上次取包 > CONSUMER_IDLE_MS(400ms) 视为应用不在，此时 WT_PACKET 最多每 CONSUMER_AWAY_INTERVAL_MS(200ms) 发一条（心跳，保证它回来能立刻知道有新数据），不再 250/s；正常在用时仍然按 4ms tick 发。
  - PEAK 行新增 supp= 计数，直接看有多少条被节流掉。
- 部署：h 446B2C1437 / 196608B，三处（SAI2 目录、PS CC 2019 目录、System32）已替换，旧 z77 留在各自目录的 Wintab32.z77-loaded.dll。
- 验证口径：PS 重启后切走 → 回来，看日志 supp= 有非零、q=0/0、misses 基本为 0，笔画可继续。
## 2026-11-14 夜：z79 —— 补上"笔已离开"的 proximity 超时（真正卡"回来不认"的那一环）

- z78 上线后实测：PS 日志 build=20261114-z78，但 supp=0（节流从未触发）。原因：PS 即使切到后台也一直调用 WTPacketsGet（空转时 gets 能到 19052/秒），所以"消费者不在"这个判据对 PS 不成立。z78 的节流保留（对只靠消息的应用有用），但它不是 PS 的病根。
- 真正的病根（z79 修的就是它）：数位板只在状态变化时上报，笔离开板子/静止不动时**根本没有样本进来**，于是 context.pen.in_range 一直停留在最后一次的 true，我们也就永远不会给应用发"笔离开感应区"的报文（TPS_PROXIMITY + WT_PROXIMITY out）。
  - 结果：PS 一直认为笔还在板上、甚至还在按下状态。切到别的窗口再回来，它把接下来的一下当成"同一次交互的继续"，于是不产生新笔画；压力用的是记忆里的那个值，看起来就是"只能点出一个 100% 的点"。
  - 日志佐证：z78 那段里 93231937→93236687 有 ≈4.7 秒只有 1 个样本，其间没有任何 status=TPS_PROXIMITY 的包发出。
- z79 改法：Context 增 last_sample_ms；refresh() 在"本次没有新样本"且距上次样本 > PROXIMITY_TIMEOUT_MS(1000ms) 时，强制 in_range=false、压力清零、按键清零、changed 标记 PK_STATUS|PK_BUTTONS|PK_NORMAL_PRESSURE，随后由已有的离场分支发一个 TPS_PROXIMITY 包，通知线程再补 WT_PROXIMITY(out)。笔再进来时是全新的一轮：WT_PROXIMITY(in) → WT_CSRCHANGE → WT_PACKET，PS 才会重新绑定压力能力。
- 部署：h 4B187FB189 / 197120B，三处已替换（旧 z78 留在各自目录 Wintab32.z78-loaded.dll；z77 的 Wintab32.z77-loaded.dll 仍保留）。
- 验证口径：画→切走 10~30 秒→回来再画；日志里应能看到一次 status=2048(TPS_PROXIMITY) 的包 + 之后重新出现的 WT_PROXIMITY/CSRCHANGE，且笔画能接着画。
## 2026-11-15：z80 —— 去掉通知线程与取包线程的锁打架（"偶尔堵一下"的根因）

- z79 实测结果：焦点丢失（切窗口回来只出一个点）已消失，但出现"偶尔堵住、慢一下"，不频繁。
- 结构上的原因：通知线程每 4ms tick 对每个 context 调一次 refresh()，而 refresh 会 poll_shared_all（读共享环 + 逐个 serialize），**全程持有 registry 全局锁**。PS 绘画时每秒进同一个锁几万次（gets 可到 2 万+/秒），两边在互斥量上轮流等待，于是偶尔出现可见的停顿。
- z80 改法：
  - 通知线程：距该 context 上次取包 < 10ms（说明应用自己正在取包）时**不再调 refresh、不碰共享环**；只有"应用不来取"（纯消息驱动）才由通知线程代劳。
  - CONSUMER_IDLE_MS 400 → 1200（应用短暂忙一下不再被误判为"不在"，避免落到 5Hz 心跳）。
  - proximity 超时 1000 → 1200ms；按下状态单独放宽到 PROXIMITY_TIMEOUT_PRESSED_MS=3000ms——笔按住不动时同样没有样本，若按 1.2s 超时会把正在画的笔画切断。
- 部署：h 5DBCF95BE0 / 197120B，三处已替换；旧 z79 留作 Wintab32.z79-loaded.dll。
- 注意：System32 那份被 procraft（pid 12668）加载着，要重启它才吃到 z80。
## 手感面板已并入 OTD 界面（2026-11-14 夜）

- 源码：`OpenTabletDriver.UX\Controls\FeelGroup.cs`（Eto 版 FeelGroup / DragNumber / CalibrationPad / FeelConfig / TabletShared），`ControlPanel.cs` 把「手感」作为**第一个** TabPage 插入，`OnProfileChanged` 里设置 `feelGroup.TabletName`。
- 数值框：左右拖拽（6px 一步）、点左半边 -1 / 右半边 +1、滚轮 ±1；写回 `%LOCALAPPDATA%\OTDWintab\feel.json`（字段与独立面板完全相同，shim 每秒热读）。
- 校准：CalibrationPad 用 20ms `System.Threading.Timer` 读共享内存 `Local\OpenTabletDriverWintab`，取松手前 100ms 峰值存为 MaxPressure；「保留峰值」显示 `MaxPressure*100%`。
- 编译：`dotnet build OpenTabletDriver.UX.Wpf\OpenTabletDriver.UX.Wpf.csproj -c Release`，0 error。
- **发布（关键）**：portable-build 的 UX 是 `PublishSingleFile` 单文件，散拷 dll 无效，必须
  `dotnet publish OpenTabletDriver.UX.Wpf\OpenTabletDriver.UX.Wpf.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o E:\000 app\OpenTabletDriver\dist-feel`
  产物 158MB `OpenTabletDriver.UX.Wpf.exe` + 5 个散放原生库。
- **坑**：新 exe 配旧 `*_cor3.dll` 会在启动瞬间炸 `System.EntryPointNotFoundException: WpfGfx_SetDisableBoundsCheckProtection`（KERNELBASE 0xe0434352）。发布时必须把 dist-feel 里的 `wpfgfx_cor3.dll` / `PresentationNative_cor3.dll` / `PenImc_cor3.dll` / `D3DCompiler_47_cor3.dll` / `vcruntime140_cor3.dll` 一并覆盖到 portable-build。
- 备份（旧 exe + 旧原生库）：`backup\20261115-ux-before-feel\`。

## daemon 吃满 14.7GB 与日志降噪（2026-11-14 夜）

- 现象：daemon 工作集 14766MB（机器 24GB 只剩 432MB），随后 OTD 界面崩（`c0000005`）、系统 OOM、板子“连不上”。
- 根因链：部署的 `Plugins\OpenTabletDriver.Wintab\OpenTabletDriver.Wintab.dll`（11-14 15:09 那个调试构建）每报告写 `POINTER getter: native=True` / `READ called`，每秒几十到上百行；`LogFile` 用的是 `Channel.CreateUnbounded` + 每条 `FlushAsync`，写入任务追不上就无限堆队列；`Write()` 还是 `while(!TryWrite)` 死循环。
- 修法（两处）：
  - 插件：用当前源码重建（源码里没有这些逐事件日志），已覆盖部署，旧 DLL 备份为 `.bak-20261115-debug`。
  - `OpenTabletDriver.Daemon\LogFile.cs`：队列改 `Channel.CreateBounded(2048, DropOldest)`，`Write()` 只放行 `>= LogLevel.Warning`（Debug/Info 直接丢弃），不再 busy-loop。
- 重发布：`dotnet publish OpenTabletDriver.Daemon\OpenTabletDriver.Daemon.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o E:\000 app\OpenTabletDriver\dist-daemon` → 72MB 单文件，备份在 `backup\20261115-daemon-before-logfilter\`。
- 结果：日志从 82KB/105s 降到 833B 全程；daemon 内存 81MB 稳定不涨。
- 代价：日志不再记录 Info（`Found tablet`、`Display area`、`Clipping` 这些查映射时要用的行也没了）。要临时排查映射，得把 `Write()` 里的 `if (message.Level < LogLevel.Warning) return;` 注释掉重编。
- 旧日志目录 `portable-build\userdata\Logs` 仍有约 1.6GB 历史文件（`MAX_LOG_FILES=20` 只限个数不限大小），未删除。

## 界面收尾：清理、安装按钮、关闭到托盘（2026-11-14 深夜）

- 清理：`portable-build\userdata\Logs` 原有 20 个文件 5673MB，只留最新那个（`13_58_15Z`），现总量 1KB 级。
- 不再给应用单独放 DLL：`D:\app\art  sai 2\Wintab32.dll` 已删（备份 `backup\20261115-app-local-wintab\`）；PS 目录那份因 Photoshop 正在运行被占用未删，等 PS 关闭后再删即可。现在唯一来源是 `C:\Windows\System32\Wintab32.dll`（z80 `5DBCF95BE0`）。
- `portable-build\WintabNative\Wintab32.dll` 之前是旧构建（166400B `CED02D585A`），已换成 z80（197120B `5DBCF95BE0`），旧文件备份为 `.bak-20261115`，安装按钮就从这里取源。
- 新按钮：`MainForm` 里 `installWintabButton`「安装 Wintab 到系统」，放在底部 `CommandsControl` 最左（`Save`/`Apply` 之前）。实现是 `Start-Process cmd.exe /c copy /y` + `Verb="runas"` 提权复制 `WintabNative\Wintab32.dll` 到 `%WINDIR%\System32`，完成后比对文件大小给出「安装完成/失败」提示。
- 关闭行为：`MainForm.OnClosing` 在 `App.EnableTrayIcon && !allowClose` 时取消关闭并 `WindowState = Minimized`（既有处理会把窗口藏起来并从任务栏移除）；真正退出走 `MainForm.Quit()`（设 `allowClose` 后 `Application.Instance.Quit()`）。`TrayIcon` 菜单项由 "Close" 改为 "Quit" 并调用 `window.Quit()`；File 菜单的 Quit 也都改走 `Quit()`。
- 验证：`CloseMainWindow()` 后进程仍存活、`MainWindowHandle=0`（已藏进托盘）。

## 模式下拉取代安装按钮（2026-11-15）

- 底部 `CommandsControl` 现在是 `模式` 标签 + `modeSelector`（Windows Ink / WinTab）+ Save + Apply，安装按钮已删除。
- 选择 WinTab 时先比对 `System32\Wintab32.dll` 与 `WintabNative\Wintab32.dll` 的长度：一致就直接切；缺失或版本不同就弹「要不要装」的确认，确认后用 `cmd /c copy /y` 提权复制，成功后切到 winTab 并提示重启应用，失败则退回选择。
- 选择结果写到 `%LOCALAPPDATA%\OTDWintab\feel.json` 的 `InputMode`（`winInk` / `winTab`），shim 每秒热读；`FeelConfig` 的代码默认值改为 `winInk`（新装默认不需要驱动）。
- `FeelGroup` 里原来的「输入模式」单选框组已删除，模式只有一个来源（底部下拉）。
- 注意：`InputMode` 只决定 shim 是否做死区/弹性/压力放大；OTD 自己的输出模式仍在「输出」页（当前是 `OpenTabletDriver.Wintab.WintabOutputMode`）。选 Ink 时手感处理不生效，压力走原始值。

## 模式成为唯一开关 + Ink 下禁用手感页

- `OutputModeEditor` 底部那个输出模式下拉已移除（"输出"页只剩模式编辑器），窗口底部的 `模式` 是唯一开关。
- `FeelGroup.SetModeEnabled(bool)`：Ink 时把死区/弹性/笔触稳定三个滑块禁用、校准区 `pad.Enabled=false`（同时从 CompositionScheduler 注销，不再轮询），顶部显示提示「Windows Ink 模式没有手感处理…」。
- `MainForm.ApplyInputMode` 写完 `feel.json` 后调 `SyncFeelAvailability`；启动时按已存模式同步一次。
- 事实：这版 OTD 没有 Windows Ink 输出插件（`OutputModeEditor` 白名单里的 "Windows Pen Pointer" 没有对应类型），所以 Ink 不可能脱离 Wintab 链路；Ink 的实际效果是「shim 直通原始压力，不做手感处理」，笔和原始压感仍在。

## SAI 在 WinTab 模式下没压感：根因与修复（2026-11-15）

现象：PS、ProCraft 压感正常，PaintTool SAI Ver.2（`D:\app\art  sai 2\sai2.exe`）能画出线但没有压力；用户否决"改用 Windows Ink/指针模式"，要求三个软件都走 Wintab。

定位手段（都在 `wintab-oracle\`）：

- `penstroke.py 1400 900 2000 900 70 14 ramp cursor mouse`：往共享内存喂 60~70 个假样本（`ramp` 线性升压 / `flat` 恒定满压），可选 `cursor`（同步 `SetCursorPos`）、`mouse`（SendInput 按一下左键）、`prox`（先离位再入位）。这是能不动真笔就复现的关键工具。
- `sai_keys.py`（`new` / `enter` / `keys VK...` / `click x y` / `max`）、`mousedrag.py`（纯鼠标拖一条恒粗线作对照）、`grab.py`（全屏或区域截图）。
- **坐标必须是物理像素**：脚本进程默认 DPI-unaware，`SetCursorPos` 走逻辑坐标（2194×1234，缩放 1.75），而 Wintab 包走物理坐标（3840×2160）。`SetThreadDpiAwarenessContext(-4)` 直接传 int 会失败，**必须传 `ctypes.c_ssize_t(-4)`**，之后 `GetSystemMetrics` 才返回 3840×2160。
- 教训：`sai_keys.py max` 是 `ShowWindow(SW_MAXIMIZE)`，窗口已最大化时再调一次会把窗口还原，之后所有面板/画布坐标全变；每次自动化前先截图确认。"油漆桶"被选中时拖拽只会填色，画不出线——先确认工具是笔。

四个依次定位到的根因（前三个都在 `Lib.rs` 的默认 LOGCONTEXT / 队列行为上）：

1. **默认 `WTI_DEFCONTEXT` 的 `sysExt=(0,0)`**。SAI 用 `WTInfo(cat=400)` 取模板再用 `WTGet` 读映射参数，`sysExt` 为 0 时它的屏→画布映射塌成一条线，表现为"喂了包什么都没有"。改成 `GetSystemMetrics(0/1)` 后 SAI 立刻开始出笔画（此时压力仍是 0，已经能看到极细的淡线 + 笔尖光标圈）。
2. **默认上下文的 `sens=(0,0,0)`、in/outExt 第三分量 `1`、`device=0`** 与官方 `official-x64.txt` 不符（官方 `sens=(65536,65536,65536)`、ext 第三分量 `2047`、`inOrg/outOrg` 第三分量 `-1023`、`device=0xFFFFFFFF`）。按官方逐字段对齐后压力才真正上来。
   - 注意：`official-x64.txt` 里 `inOrg=(0,0,-1023) inExt=(44800,29600,2047) outOrg=(0,0,-1023) outExt=(44800,29600,2047)`，`sysOrg=(0,0) sysExt=(3840,2160) sysSens=(65536,65536)`。
3. **Y 轴语义**。Wintab 的设备坐标原点在左下、Y 朝上，我们一直按屏幕习惯发"朝下"的 Y。SAI 老老实实按 Wintab 语义映射，所以上下镜像；PS/ProCraft 用系统光标定位、只从包里取压力，因此从不暴露这个问题。
   - 结论：**不要在 shim 里按 exe 名做适配**（试过 `host_flips_y()` 按 `sai2.exe` 判断，能用但用户明确否决），而是全局按 Wintab 语义发 Y：`packet_y(y) = device_extent().1 - y`，一处逻辑，所有消费者一致。
4. **队列长度**。`poll_shared_all()` 末尾原本 `queue.truncate(read)`，只保留最新一包；改"保留历史"（`MAX_QUEUE` 上限）后日志里 `misses` 从大量降到 0，但对 SAI 的压感并无决定性作用，反而因为积了 16~22 个包（≈80ms）让笔明显拖尾。**已验证"保留历史"不是必需**，用户也明确要求不留历史，最终回到 `truncate(read)` 的语义。

延迟（本轮追加）：

- `start_notifier()` 里 `std::thread::sleep(4ms)` 实际睡到 **15.6ms**——Windows 默认计时器精度就是这个值，`thread::sleep` 会被向上取整。笔迹拖尾的主因之一。
- 修法：`#[link(name="winmm")] timeBeginPeriod(1)`（进程内一次），睡 `2ms`，实际间隔约 2~3ms。

验证证据：SAI 在 WinTab 模式下喂 `ramp` 得到"细→粗"渐变笔画、`flat` 得到等粗粗线；`WTPacket` 日志（`%LOCALAPPDATA%\OTDWintab\wintab-sai2-<pid>.log`）里 `PACK ... np=...` 的 np 数值与喂入值一致。

部署现状（本轮，已统一）：系统里只有一份 shim——`C:\Windows\System32\Wintab32.dll`（x64，SHA256 `7F0621EA…`）和 `C:\Windows\SysWOW64\Wintab32.dll`（i686 同源构建，`D1B9F20F…`）。应用目录里的本地 DLL 全部撤掉：`D:\app\art  sai 2\` 删除，PS 与 ProCraft 目录下的改名为 `Wintab32.dll.bak-doubao-20261115`（进程占用时无法删除，改名即可让它下次启动走系统 DLL）。安装走 `install-wintab.ps1`（提权后把 `portable-build\WintabNative` 里的 x64/x86 分别装进 System32 / SysWOW64，装前自动备份到 `WintabBackup\`）。滚动备份留在 `backups\`：`Wintab32-system32-z80-20261114.dll`、`Wintab32-ps-local-20261114.dll`（同 SHA256 `5DBCF95B…`，即上一版 z80）、`Wintab32-20261115-globalflip.dll`、`Wintab32-20261115-latency1ms.dll`。发布仓库 `repo-publish` 已推送到 GitHub（commit `2b513fd` 起）。

---

## 交付形态：一个 exe 启动（2026-11-15 晚）

用户否决了「源码/脚本 zip」的交付方式（原话「你这个不对 我需要一个exe启动用原来的otd安装」），要的是双击就能用的单个可执行文件。做成 `Wintab 一键启动.exe`（596 KB），放在 OTD 目录里与 `OpenTabletDriver.UX.Wpf.exe` 并列。

**它做什么**

1. 比对自己内嵌的 `Wintab32.dll`（x64 197120B / x86 176640B，`include_bytes!` 编译进 exe）与 `System32\Wintab32.dll`、`SysWOW64\Wintab32.dll` 的字节。相同就一步都不写、不弹 UAC。
2. 不同才请求提权（`ShellExecuteExW` + `SEE_MASK_NOCLOSEPROCESS` + `runas`，子进程带 `--install-shim`）。提权进程里：先把旧文件备份到 `WintabBackup\Wintab32-before-<epoch>-<label>.dll`，再写入。
3. 写入失败（DLL 被运行中的程序映射着）时自动退到「改名让位」：把旧文件改成 `Wintab32.old-<epoch>.dll` 再写新文件，下次启动该程序自然加载新版，旧文件即回滚点。
4. 起 `OpenTabletDriver.UX.Wpf.exe`：先试 exe 所在目录，再试同目录的 `portable-build\`，最后 `%OTD_BASE%`。每个候选等 4 秒，若立刻退出（非零退出码）就换下一个。

**为什么这条回退链是必须的（不是防御性冗余）**

根目录那份 `OpenTabletDriver.UX.Wpf.exe`（165683672B）在本机启动即崩，退出码 `0xC000041D`（`STATUS_FATAL_USER_CALLBACK_EXCEPTION`），应用日志 1026 里是 WPF `HwndSubclass.HookWindowProc → SetWindowLongPtrWndProc` 的 `DllNotFoundException`；同目录 `portable-build\` 那份（165538615B）正常。事件日志显示这个崩溃 11/12、11/13、11/14 都有，属既有问题、与 shim 无关，但正好让「一个 exe 启动」必须能换副本。

**验证过的分支**（用 `--target-dir <目录>` 在临时目录里跑，不碰系统盘）

| 场景 | 结果 |
|---|---|
| 目标不存在 | 写入成功，无备份 |
| 目标存在但内容不对 | 旧文件进 `WintabBackup\`，新文件写入成功 |
| 目标只读/被占用（写入拒绝） | 自动改名 `Wintab32.old-<epoch>.dll`，新文件写入成功 |
| System32 已是最新 | 打印「已是最新，跳过安装」，不做任何写入 |
| 根目录 UX 崩溃 | 打印退出码并自动改启 `portable-build\`，OTD 正常起来 |

**注意**：exe 只负责「装/不装 + 启动」，不会重装 OTD、不下载任何东西、不动软件目录里的本地 DLL。把它放到别的目录会提示找不到 `OpenTabletDriver.UX.Wpf.exe`。

源码在 `wintab-shim\src\bin\launcher.rs`（同 crate 的第一个 bin，`cargo build --release --bin launcher`）；`Cargo.toml` 为此补了 `Win32_UI_Shell` 与 `Win32_System_Registry` 两个 feature（`SHELLEXECUTEINFOW` 在 windows-sys 0.59 里被 `Win32_System_Registry` 门控，缺了会 `unresolved import`）。
---

## 控制面板底部栏：开机启动（2026-11-15 晚）

用户在底部栏截图（设备下拉 / 模式=WinTab / 保存 / 应用）上要求「在下面增加一个开机启动 默认勾选」。改的是 `repo-publish\ux-patch\MainForm.cs`（补丁文件，编译前覆盖到 `src\OpenTabletDriver-0.6.x\OpenTabletDriver.UX\MainForm.cs`）。

**行为**

- 勾选框 `开机启动` 加在底部 `TabletSwitcherPanel.CommandsControl` 的横向 StackLayout 里，排在「应用」右边。
- 首次运行（注册表里没有记录）→ 默认勾上，并且立刻写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `OpenTabletDriver Wintab`，值 = `"<当前 exe 全路径>"`。默认勾选会真正生效，不只是界面上的样子。
- 每次启动若仍是勾选状态，会重写一次该值 → 整个目录搬家/改名后路径自动跟着更新（`Environment.ProcessPath`，单文件发布取到的就是 exe 真路径）。
- 取消勾选 → 删掉 Run 项，并把决定记在 `HKCU\Software\OpenTabletDriver\Wintab\AutoStart`（DWORD 0/1），下次启动不会因为「没有记录」又被默认勾回去。
- 注册表异常全部 try/catch 吞掉：受限环境里勾选框失效也不会把控制面板带崩。

**验证过**

| 项 | 结果 |
|---|---|
| 首次运行（清掉 Run 项与偏好键） | Run 值自动写入，指向当时运行的 exe，偏好键 AutoStart=1 |
| 从另一个目录启动同一份 exe | Run 值自动改成新目录的路径 |
| 界面外观 | 截图确认底部栏右端出现勾选状态的「开机启动」 |
| 启动后平板识别 | 标题 `OpenTabletDriver v0.6.8.1 - Wacom PTH-660` |

未在界面上实际点过一次「取消勾选」（该分支只是 `RegistryKey.DeleteValue`，与写入同一条代码路径的镜像）；要更严的话下次可以拿 UI 自动化点一下。

**编译命令**（用官方 eng 脚本的参数，自包含单文件，产物 ≈165.7 MB，与原来那份同量级）

```powershell
cd "E:\000 app\OpenTabletDriver\src\OpenTabletDriver-0.6.x"
dotnet publish OpenTabletDriver.UX.Wpf -c Release -r win-x64 --self-contained true `
  --output <out> /p:PublishSingleFile=true /p:PublishTrimmed=false `
  /p:DebugType=embedded --framework net8.0-windows
```

踩到两个编译错：`CheckBox` 在 Eto 里没有 `VerticalAlignment`（删掉即可）；`RegistryValueKind.Dword` 必须写成 `DWord`。

旧的前端备份在 `backups\OpenTabletDriver.UX.Wpf-20261115-before-autostart.exe`，回滚直接拷回 `portable-build\`（或绿色版目录）即可。
## 启动检测提速：约 6 秒 → 0.9 秒（2026-11-15）

现象：开机后要等 6~7 秒才检测到数位板；日志第一行在 +5.8s 出现，而 found tablet → aux fail 只有约 200ms，说明慢在检测之前。

方法：`OpenTabletDriver.Plugin/StartupTrace.cs`（环境变量 `OTD_TRACE=<文件>` 开启，`Mark(label)` 追加 `+<ms>ms label`），打点位于 Daemon 的 `Program.cs`、`DriverDaemon.cs`，以及 `Devices/RootHub.cs`、`Devices/WinUSB/WinUSBRootHub.cs`、`Devices/HidSharpBackend/HidSharpDeviceRoot.cs`。

修复前的分段表（一次运行）：
main-enter 28 → build-daemon-start 54 → roothub-ctor-enter 5165 → roothub-enumerated(20) 5166 → driver-resolved 5269 → daemon-ctor 5310 → driverinfo-scan-done 5453 → plugins-loaded 5456 → detect-done 5514。

定位结论：5.07 秒全部在 `WinUSBRootHub` 构造函数里第一个 GUID 的 `EnumerateAllDevicesWithGuid`（标准 WinUSB `{dee824ef-...}`）；第二个 GUID 0ms，HID 枚举只有 17ms。也就是说慢在「枚举到一个 WinUSB 接口后 `new WinUSBInterface(path)` 打开设备读管道」。对照实验（`OTD_DEBUG_SKIP_WINUSB=1` 完全跳过该枚举）把 detect-done 从 5161ms 降到 477ms，证明这段不仅自身慢，还会**拖住 HID 检测**（两者同一时刻结束）。

改动：
1. `WinUSBRootHub` 不再在构造函数里同步枚举：首次枚举放到工作线程，延迟 `InitialEnumerateDelayMs = 10000` 后执行，结果通过 `DevicesChanged` 上报；热插拔通知（`CM_Register_Notification`）照旧立即枚举，所以后插入的 WinUSB 笔仍能立刻识别。
2. `RootHub` 构造函数改为「先订阅各 hub 的 DevicesChanged，再 ForceEnumeration」，避免后台枚举恰好在订阅前完成而丢事件。
3. `DriverDaemon`：`DriverInfo.GetDriverInfos()`（遍历进程与模块）移入 `ReportConflictingDrivers()`，用 `Task.Run` 后台执行，异常吞掉只记日志。
4. `LoadUserSettings()` 顺序改为 DetectTablets → PluginManager.Clean → LoadPlugins → 读 settings。检测只需设备与配置，插件决定的是报文解析与输出模式，不必排在检测前面。
5. 控制面板的开机自启改写 `OpenTabletDriver.Daemon.exe` 路径（`AutoStartExecutable()`：同目录存在 daemon 就用它，否则回退自身），不再开机拉 174MB 的 WPF 面板。

结果（三次）：进程创建 → Main 538~560ms（.NET 单文件装载，非本项目代码可控），Main → detect-done 341~357ms，合计 **879~917ms**。阶段表：roothub-enumerated(19) @122ms、detect-done @351ms、冲突驱动扫描后台完成 @426ms、user-settings-loaded @436ms。修复前为 5.5~5.6 秒。

取舍：WinUSB 设备（Zadig / Huion WinUSB）现在在启动约 10 秒后才进入设备列表（原来 5.6 秒）。要恢复原行为把 `InitialEnumerateDelayMs` 设为 0 即可，代价是重新堵住 HID 检测。

## 控制面板「用管理员身份启动就退出」的根因（0xe0434352）

另一台机器回传的 `userdata/startup-crash.log`：

```
System.ArgumentNullException: Value cannot be null. (Parameter 'path1')
   at System.IO.Path.Combine(String path1, String path2)
   at Eto.Wpf.Forms.EnableThemingInScope.EnsureActivateContextCreated()
   at Eto.Wpf.Forms.EnableThemingInScope..ctor(Boolean enable)
   at Eto.Wpf.Forms.MessageBoxHandler.ShowDialog(Control parent)
   at Eto.Forms.MessageBox.Show(...)
   at OpenTabletDriver.UX.Wpf.Program.Main(String[] args) line 21
```

结论：不是缺 DLL、也不是单文件发布的问题，而是「**不要用管理员权限运行**」这个提示框自己崩了——Eto 的主题提示框要求 alive 的 Application 上下文，而它被调用时窗口还没建，`Path.Combine` 拿到 null 路径直接抛异常，异常从 `Main` 冒出把进程带走（0xe0434352 = CLR 未处理异常）。之前把同一现象误判成缺 `*_cor3.dll` 的 `0xC000041D`。

修法：`Program.WarnAboutElevation()` 改用 user32 `MessageBoxW`（不依赖 Eto/WPF 上下文），并把整段包在 try/catch 里——提示失败绝不能是启动失败的原因；同时把结果写进 `userdata/startup-notes.log`。实测提权启动：提示框返回 1 后正常出窗口，`startup-crash.log` 不再生成；普通权限启动不受影响。仍然建议双击运行，只有 `install-wintab.bat` 需要提权。
## 开机自启：后台先起 + 托盘静默（2026-11-15 续）

需求：开机要能看到托盘图标，但不要弹出/激活控制面板。

实现（`ux-patch/MainForm.cs` 的 `ApplyAutoStart`）：启用时写两条 Run 值。
- `OpenTabletDriver Wintab` = `"...\OpenTabletDriver.Daemon.exe"`（`AutoStartDaemon()` 找同目录 daemon，找不到则不写这条）
- `OpenTabletDriver Wintab Tray` = `"...\OpenTabletDriver.UX.Wpf.exe" --minimized --skipupdate`

取消勾选两条一起删。之所以两条而不是一条：daemon 只负责笔，实测 1 秒内可画；面板是 174MB 的 WPF app，初始化 2 秒多，不能排在“能不能画”之前。分两条正好让 Windows 并行拉起，先画后托盘。

面板静默：`App.cs` 本来就有 `--minimized`；`MainForm` 构造函数里 `WindowState==Minimized && EnableTrayIcon` 时置 `Visible=false`、`ShowInTaskbar=false`，`TrayIcon` 照常创建（所以托盘在），`DaemonWatchdog` 照常启动（若后台已在跑，`Instance.Exists` 让它跳过，不会重复起）。

唤出窗口的坑（`ux-patch/TrayIcon.cs`、`ux-patch/App.cs`）：最小化启动后窗口 `Visible=false` 且不在任务栏，裸 `Show()` 只会得到一个看不见的窗口。两处统一改为先 `ShowInTaskbar=true`、`WindowState=Normal`，再 `Show()` + `BringToFront()` + `Focus()`：
- 托盘的 `Indicator.Activated` 与右键菜单「Show Window」（新增私有 `TrayIcon.ShowWindow()`）
- 第二个实例启动时通过命名管道让第一实例显示（`App.RunInternal` 的 ipc 分支）

验证（本机按登录顺序模拟：先 daemon，再 `UX --minimized --skipupdate`）：
- daemon 进程创建 → detect-done = 964ms，识别 `Wacom PTH-660`
- daemon 进程数 1（面板看到后台已在跑，没重复拉起）
- 枚举该进程可见窗口 = 0（只剩托盘）
- 通知区登记项存在：`HKCU\Control Panel\NotifyIconSettings` 中 `...\OTD-Wintab-绿色版-20261115\OpenTabletDriver.UX.Wpf.exe`
- 再启动一个实例 → 第一实例窗口变为 VISIBLE（`OpenTabletDriver v0.6.8.1 - Wacom PTH-660`），第二个自行退出

未验证：真的用鼠标点托盘图标。本轮主桌面控制授权超时（`cu.switch_desktop('main')` 返回 user authorization timed out），只能用「第二个实例」这条同义路径验证唤出逻辑。Eto 2.5.6 的 `TrayIndicator` 只暴露 `Activated` 与 `StyleChanged` 两个事件（反射确认），左键是否触发由 WPF 平台实现决定；右键菜单「Show Window」调用的是同一个 `ShowWindow()`，一定有效。若左键点了没反应，就从右键菜单进，或改用定时窗口检查。

另：Windows 11 默认把新图标收进「隐藏的图标」溢出区，使用说明里已提示把它拖到常显位置。

## 控制面板：用过一次之后，双击就直接进托盘

需求原话是「启动的时候判断如果 OpenTabletDriver.UX.Wpf 识别到用户用过就直接最小化」，同时保留开机自启那条「托盘在、面板不弹」。

做法（`ux-patch/App.cs`）：

- `CommandLineOptions` 增加 `ForceShow`，命令行增加 `--show`。
- `RunInternal` 里唯一的一处判断：`startMinimized = options.StartMinimized || (!options.ForceShow && PanelShownBefore())`。非最小化启动时立刻 `RememberPanelShown()`。
- 标记存在注册表 `HKCU\Software\OpenTabletDriver\Wintab` 的 `PanelShown`（DWORD 1）。读不到（首次）或异常，都按「没显示过」处理，即退化成显示窗口。

于是三种启动情形：

| 情形 | 行为 |
| --- | --- |
| 第一次双击（`PanelShown` 不存在） | 出窗口，并写入 `PanelShown=1` |
| 之后双击（无参数） | 直接进托盘，窗口不出现；`--show` 可强制出窗 |
| 已经在运行又双击 | 第二个实例走命名管道，把第一实例窗口唤出后自己退出（`--show` 与无参数都一样） |

开机自启那条 `Run` 值本来就是 `--minimized --skipupdate`，不受影响。

实测（枚举该进程 `IsWindowVisible` 的窗口，只数有标题的）：

- 第 1 次启动：可见窗口 1 个（`OpenTabletDriver v0.6.8.1 - Wacom PTH-660`），daemon 1 个，`PanelShown` 写成 1
- 第 2 次启动（无参数）：可见窗口 0 个，daemon 1 个
- 第 3 次启动带 `--show`：可见窗口 1 个
- 后台状态（无窗口）再双击一次带 `--show`：第一实例窗口变为可见，第二实例退出

## 没装 Wintab32.dll 时启动自动安装

原来的状态是「没有」：只有手工的 `install-wintab.bat`（会 taskkill 三个 OTD 进程并以 `pause` 结尾，不适合被面板静默调用）。新增 `ux-patch/WintabInstall.cs`，在 `Program.Main` 里「提权告警之后、`App.Run` 之前」调用 `WintabInstall.EnsureInstalled()`。

流程：

1. 用 SHA-256 比对包内 `WintabNative\Wintab32.dll` / `Wintab32_x86.dll` 与 `%windir%\System32\Wintab32.dll` / `SysWOW64\Wintab32.dll`。两份都一致就直接返回，不打扰（本机实测：无任何提示、不写日志）。
2. 有差异时，非提权状态弹 user32 `MessageBoxW`（「系统里的 Wintab32.dll 与随包版本不一致 / 未安装，是否现在安装？」）。选「否」写 `WintabInstallDeclined=1`，以后不再问；想再装就删这个值。
3. 选「是」则把复制步骤写成 `%TEMP%\otd-wintab-install-<stamp>.ps1`，非提权用 `ShellExecuteExW`（`runas`、`SW_HIDE`、`SEE_MASK_NOCLOSEPROCESS`）拉起，已提权则直接 `powershell.exe -File`。脚本先备份到包内 `WintabBackup\Wintab32.<tag>-<stamp>.dll`，复制失败（被占用）时把目标改名为 `Wintab32.old-<epoch>.dll` 让位再复制。
4. 脚本只负责「干了什么」，是否真的装对由 C# 复制完再比对一次哈希决定。结果写入 `userdata\startup-notes.log`，成功/失败各弹一个原生提示框。

### 干跑开关与本轮抓出的两个真 bug

`OTD_INSTALL_DRYRUN` 把目标改指 `%TEMP%\otd-install-dryrun\{system32,syswow64}-Wintab32.dll`、跳过询问和弹窗、不走 UAC、保留脚本与日志，用来在本机（系统 DLL 已是最新、不能乱覆盖）验证整条链路。

1. **脚本 UTF-8 无 BOM → PowerShell 5.1 按 ANSI 解码**。脚本里有用户目录路径 `C:\Users\画画的\...`，被读成 `C:\Users\鐢荤敾鐨刓...`，`[IO.File]::WriteAllText` 报 `DirectoryNotFoundException`，日志一行都没留下（于是 C# 侧只看到「installer produced no log」）。改成 `new UTF8Encoding(true)` 带 BOM 写入。
2. **脚本里调 `Get-FileHash` 报「无法识别为 cmdlet」**。该子进程里 `Microsoft.PowerShell.Utility` 没能加载（此时 64 位那份已复制成功、32 位那份还没轮到，现象正是「第一项 OK、第二项 MISSING」）。修法不是去修模块加载，而是把哈希从脚本里删掉：脚本只做备份/复制并记录 OK/FAIL/ERROR，装完由 exe 自己再比对一次系统文件与包内文件，权威且少一个依赖。

干跑通过后的记录（`userdata\startup-notes.log`）：

```
# wintab install: OK   ...\otd-install-dryrun\system32-Wintab32.dll | OK   ...\syswow64-Wintab32.dll | DONE
# wintab install result: both copies match
```

沙箱两份哈希前缀 `7F0621EAE5F46DBB`（197120B）与 `D1B9F20F7BA205A0`（176640B），与系统里现有的一致。

注意：干跑只证明「检测差异 → 生成脚本 → 复制 → 备份 → 复核」这条链路；真正写 System32 需要 UAC 确认，写入逻辑与之前手工验证过的 `replace-wintab-system32.ps1` 相同。另外，本机系统 DLL 已是最新时不会弹任何框，所以「装到真 System32」这次没有在真机上重跑一遍（避免覆盖掉正在用的 shim）。


### 为什么不给整个面板加「以管理员身份运行」

`WintabInstall.cs` 改成：系统里根本没有 Wintab32.dll 时，不再先弹自己的询问框，直接发起 UAC——授权弹窗本身就是同意，点一次「允许」就装完。只有系统里已经存在一个不一样版本的 Wintab32.dll 时才先问一句（那个多半是 Wacom 装的，换掉前留个确认，旧文件照样备份到 WintabBackup）。UAC 被点「否」则记 `WintabInstallDeclined=1` 并提示一次，之后不再打扰（重启程序或删该值可再装）。

反过来给 `OpenTabletDriver.UX.Wpf.exe` 的清单加 `requireAdministrator` 是条坏路：

- 面板本身不需要管理员：插件目录、userdata 全在当前用户下；OTD 自己也明说以管理员运行时 Plugin Manager 与 Tablet Debugger 会不正常（就是那个「should not be run with administrator privileges」框）。
- 提权进程收不到普通进程的拖放（装插件、拖 profile 会失效）。
- 开机自启写在 HKCU 的 Run 下，带管理员清单的 exe 每次登录都会弹一次 UAC，等于把「静默托盘」毁了。
- 真正需要提权的只有「往 System32 写两个 DLL」这一件事，`ShellExecuteExW(runas)` 单独拉起一次临时脚本正好把提权范围圈死在这一点上。

所以：正常双击（不要右键以管理员身份运行）；需要装 DLL 时只会弹一次授权，点「允许」即可。
