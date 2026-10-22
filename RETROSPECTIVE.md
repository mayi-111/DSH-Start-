# DSH 启动器 · 制作复盘

一份"从零到能用"的完整记录：做过什么、踩过哪些坑、代码怎么实现的、如果手写该怎么下手。

- 成品：[DSH.exe](C:\Users\MSI\AppData\Local\DSH-Launcher\DSH.exe)（桌面快捷方式 `DSH`）
- 源码：[src/DSH.cs](src/DSH.cs) —— 单文件 C# WinForms，约 2600 行，无第三方依赖
- 构建：[构建.bat](构建.bat) / [build.ps1](build.ps1) / [make-icon.ps1](make-icon.ps1)

---

## 一、最终功能清单

| 功能 | 说明 |
| --- | --- |
| 一键启动 | 双击桌面 `DSH` → 自动拉起 `dsh web`，并在右下角弹出状态面板 |
| 启动进度 | 进度条 + 阶段文字（加载配置 / 装配插件 / 绑定端口 / 等待就绪） |
| 运行信息 | 就绪后显示 `端口 · PID · 运行时间`，**每秒刷新**（接管的外部实例显示真实 PID 与真实启动时间） |
| 打开界面 | 用默认浏览器打开 Web GUI（带启动令牌，自动通过浏览器信任校验） |
| 停止服务 | 先**关掉浏览器里那个 DSH 标签页**，再结束 `dsh web` 进程树 |
| 缩略待机 | 运行 15 秒后自动缩成 **84px 正圆**，只显示 logo；点一下原地展开；可随意拖动 |
| 倒计时 | 面板底部一行淡灰小字 `15s后缩小`，**可点击**立即缩略 |
| 图标 | 桌面快捷方式与面板 logo 同一图源（`tp.png`）自动生成多尺寸 ico + 圆形 png |
| 容错 | 端口被占自动接管；配置目录被锁自动用隔离目录重试；失败给出可读原因并可重试 |

---

## 二、制作过程（时间线 + 每一步为什么）

### 阶段 0 · 调研：搞清楚"要启动的是什么"

先读 DSH 自身的启动链路，而不是猜：

- `dsh web` 参数（`--port` / `--host` / `--no-open`）来自 `dsh-web-app/lib/startup.js`
- 默认端口 **3080**，来自 `dsh-web-app/cordis.patch.yml`
- 服务就绪时会打印一行 `dsh web: http://127.0.0.1:<port>/?token=…`
  - 这行是**最好的就绪信号**：既证明起来了，又顺带给出端口和启动令牌
- `.dsh/profiles/web/cordis.yml` 每次启动都会被重写 → 两个实例会互相抢这个文件（后面的大坑）
- 浏览器端有"浏览器信任围栏"：首次必须带 `?token=` 才能通过，之后靠 cookie

**经验**：动手前先把"外部程序的契约"（参数、输出、文件、端口）摸清，后面 90% 的麻烦都来自这些细节。

### 阶段 1 · 技术选型：为什么是 C# WinForms

需求是"右下角悬浮窗 + 圆角 + 自绘进度条 + 系统交互（杀进程、找窗口）"，候选：

| 方案 | 结论 |
| --- | --- |
| PowerShell + WinForms | 能用，但会闪控制台、分发麻烦（要 .ps1 + 执行策略） |
| HTA / VBScript | 能力太弱，做不了圆角和自绘 |
| Electron / Node GUI | 要装一堆依赖，体积大 |
| **C# WinForms + csc.exe** | **Windows 自带编译器，零依赖，产出单个 exe，无控制台** |

关键发现：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 系统自带，因此**不需要 Visual Studio、不需要装任何 SDK** 就能编译。

### 阶段 2 · 搭建"改一次能马上看效果"的构建链

`build.ps1` 做了四件事，每一步都是被坑逼出来的：

1. **把 UTF-8 源码转成 ANSI(CP936) 再编译**
   老 `csc.exe` 默认按系统代码页读源码；源码里的中文字符串（`"正在启动…"`）若是 UTF-8 就会被读成乱码。
   ```powershell
   $text = [System.IO.File]::ReadAllText($src, [Text.Encoding]::UTF8)
   [System.IO.File]::WriteAllText($tmp, $text, [Text.Encoding]::GetEncoding(936))
   ```
2. **编译为 `winexe`**（无控制台窗口）：`csc /target:winexe /win32icon:DSH.ico`
3. **安装到纯 ASCII 路径**（见坑 1）
4. **重建桌面快捷方式**：`WScript.Shell` COM 建 `.lnk`，设图标和 `Ctrl+Alt+D` 热键

### 阶段 3 · 主体程序：状态机 + 子进程

核心就三件事：

```
探测端口(3080) ──有DSH在跑──> 接管(Adopt)：不重复启动，只显示状态
      │
      └─没有──> 起子进程 node bin.js web --no-open --port 3080
                    │
                    ├─ 读到 "dsh web: http://…" → 就绪 → 显示 端口/PID/运行时间
                    └─ 进程退出/超时 4 分钟 → 失败 + 可重试
```

### 阶段 4 · 交互打磨（用户提一轮、改一轮）

1. 圆角无黑边 → 窗体 `Region` 裁剪
2. 标题改 `DeepSeek Harness` + 分隔线、logo 移到状态行
3. 「停止服务」要关掉浏览器标签页 → 强制激活窗口 + `Ctrl+W`（+ `WM_CLOSE` 兜底）
4. 运行时间不刷新 → **修掉一个真 bug**（见坑 4）
5. 缩略成小圆球 → Region 裁成正圆 + 动画 + 可拖拽 + 原地缩放
6. 倒计时那行改成可点按钮
7. 黑边/重影/字号来回调 → 见坑 5、6

---

## 三、踩过的坑（这部分最值钱）

### 坑 1 · 中文路径下 exe 静默不执行 ⭐️ 最坑

| 位置 | 结果 |
| --- | --- |
| `Desktop\DSH启动器\dist\DSH.exe` | **退出码 0，连 Main 都没进**，不写日志、不弹窗 |
| `Desktop\DSH-ascii-test.exe` | 正常 |
| `%LOCALAPPDATA%\DSH-Launcher\DSH.exe` | 正常 |

同一个 exe，只是路径里有中文就启动失败，还不报错。**对策**：编译产物安装到 `%LOCALAPPDATA%\DSH-Launcher`（纯 ASCII），快捷方式指向那里。

**排查手法**：在 `Main` 第一行写一个"启动落盘"日志（`dsh-launcher-boot.log`），并在 `Main` 外层包 `try/catch` 记录异常——这样"完全没跑"和"跑了但崩了"能立刻区分开。

### 坑 2 · 两个 dsh 实例抢同一个配置文件

`EPERM / EBUSY: operation not permitted, open '…\.dsh\profiles\web\cordis.yml'`

`dsh web` 每次启动重写该文件；已有一个实例占着它就失败。**两层对策**：

1. **先探测再启动**：请求 `http://127.0.0.1:3080/`，只要能收到任意 HTTP 响应就认定是 DSH → **接管**，不重复启动。
2. **自动重试**：万一还是撞上，就复制一份 `profiles` 到隔离目录，用 `DSH_HOME=<隔离目录>` 重跑一次。
   ```csharp
   psi.EnvironmentVariables["DSH_HOME"] = "…\dsh-launcher-home";
   ```
   重试只做一次（`_retryUsed`），失败就直接报错，不无限重启。

### 坑 3 · 抓不到子进程输出 → 就绪信号拿不到

- `RedirectStandardOutput` 配 `OutputDataReceived` 事件（异步），**不要**用同步 `ReadLine`（会和 UI 线程互相卡死）
- 子进程的 `stdin` 要 `Close()`，否则某些工具读 stdin 会挂住
- 没看到就绪行也别放弃：**端口能应答 HTTP 也算就绪**（`HttpResponds`）

### 坑 4 · WinForms Timer：在 Tick 里 Stop 再 Start 不会再生效 ⭐️

现象：面板进入"运行中"后，`端口 · PID · 运行时间` **永远停在那一刻**。

原因有两处叠加：

1. **接管路径根本没启动定时器**（`Adopt()` 提前 return，从没调过 `_uiTimer.Start()`）
2. `MarkRunning()` → `SetState()` → `ApplyState()` 里调了 `_uiTimer.Stop()`，而这条调用链正好在 **Tick 回调内部**；.NET Framework 的 `System.Windows.Forms.Timer` 在 Tick 中 `Stop()` 之后再 `Start()` **不会重新装表**，界面就永久停止刷新

**对策**：
```csharp
private bool _inTick;
private void OnTick() { _inTick = true; try { TickCore(); } finally { _inTick = false; } }
// 所有 Stop() 都加保护：
if (!_inTick && _uiTimer.Enabled) _uiTimer.Stop();
// 在 TickCore 结尾统一按状态重新驱动：
if (_state != Idle && _state != Failed && !_uiTimer.Enabled) _uiTimer.Start();
```

### 坑 5 · 透明色键（TransparencyKey）导致文字"重影" ⭐️

现象（用户描述很准）：**纯色背景正常，花哨壁纸上文字周围有重影/透底**。

原因：`TransparencyKey = 窗体背景色` 会把**所有**等于该颜色的像素变透明。文字抗锯齿会产生一圈**半透明边缘像素**，与背景色混合后落到透明色附近 → 被一起穿掉 → 壁纸透出来，看着像重影。

**对策**：**完全不用 `TransparencyKey`**，圆角改由窗体 `Region` 裁剪实现（四角本来就在面板圆角之外，看不出差别）。

### 坑 6 · 高 DPI（125%）下文字发虚

程序未声明 DPI 感知时，Windows 把整幅画面当位图**拉伸 1.25 倍**，文字就糊。

- 诊断：`GetDeviceCaps(hdc, LOGPIXELSY)` → 120（=125%）
- 试过 `AutoScaleMode.Dpi` + 全量缩放布局，但**坐标常量与缩放后的实际尺寸不一致**，缩略圆球算错位置、被裁掉一块，于是回滚
- 最终接受当前状态（用户选择"撤销回原来的大小"），重影问题由坑 5 的修法解决

**如果以后要做**：正确顺序是"先让所有定位逻辑改用运行时实际尺寸（`Bounds`），再打开 `AutoScaleMode.Dpi`"，而不是一边用常量一边让窗体自动缩放。

### 坑 7 · 后台进程发 `SendInput` 会被系统丢弃

「停止服务」要关掉浏览器标签页，第一版直接 `SetForegroundWindow` + `SendInput(Ctrl+W)`，**日志显示已发送，但 Edge 没反应**。

原因：Windows 的**前台锁定超时**（`SPI_GETFOREGROUNDLOCKTIMEOUT`）会阻止后台进程把窗口提到前台，按键于是落到别的窗口。

**对策**（三步走）：
```csharp
SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE); // 临时解除锁定
AttachThreadInput(ourThread, targetThread, true);                                     // 挂到目标线程
SetForegroundWindow(hwnd); ShowWindow(hwnd, SW_RESTORE);                              // 强行激活
// 然后 SendInput Ctrl+W；600ms 后窗口还在 → 兜底 PostMessage(hwnd, WM_CLOSE)
```

### 坑 8 · 圆球"黑边"其实是尺寸不匹配

缩略圆球（直径 120）里的 logo 只有 84px，中间那圈 18px 露出的是**窗体深色背景**（去掉透明色键后不再透明），看着像黑边。

**对策**：让 logo 与圆球**同直径**，并在控件内部按正圆裁切：
```csharp
public bool CircleClip;                  // LogoBox
circle.AddEllipse(0, 0, Width - 1, Height - 1);
e.Graphics.SetClip(circle);              // 只画圆内，圆边与窗口圆形完全重合
```

### 坑 9 · 隐藏属性的图片会被 `Test-Path` 当成不存在

用户的 `LOGO.png` 带隐藏属性：`Test-Path` 返回 False、`Copy-Item` 报找不到，但文件确实在。
**对策**：脚本里统一改用 `[System.IO.File]::Exists()` / `Get-Item -Force`。

### 坑 10 · 我自己的失误（记录以示警戒）

清理临时文件时把用户的 `LOGO.png` 一起删了，且没有备份/git。**教训**：
- 动手删之前先 `git init` 或复制一份到 `.bak`
- 通配符删除要限定目录和扩展名
- 用户目录里的东西**不是我的临时文件**

---

## 四、代码是怎么实现的

### 4.1 总体结构（单文件、六个部分）

```
Program                入口：解析参数、异常兜底、DPI 无关的启动
LauncherForm           主窗体：UI 构建 / 状态机 / 子进程 / 动画 / 窗口操作
  ├─ 常量与颜色        尺寸、字体、配色都集中在文件开头
  ├─ 启动流程          EnsureStarted → Adopt | StartChild → OnTick → MarkRunning
  ├─ 缩略与动画        CollapseToBubble / ExpandFromBubble / StartBoundsAnimation
  ├─ 窗口与标签页      FindWindowByTitleAndClass / ForceActivate / SendCtrlW
  └─ 日志              AppendLog（写 %LOCALAPPDATA%\DSH-Launcher\logs）
自定义控件
  RoundedPanel         圆角面板（SolidFill 模式用于圆形态）
  LogoBox              图片控件（CircleClip 模式按正圆裁切）
  ProgressStrip        自绘进度条（圆角轨道 + 蓝色填充 + 居中百分比）
  RoundButton          圆角按钮（hover / down / disabled 三态）
  StatusDot            状态小圆点
Native                 Win32 P/Invoke 集合
```

### 4.2 状态机（程序的大脑）

```csharp
enum LauncherState { Launching, Running, Stopping, Idle, Failed }
```

- **谁改状态**：`SetState()` → `ApplyState()` 统一刷新所有控件（文字、颜色、按钮可用性与文案、进度条/信息行可见性）
- **谁驱动**：`_uiTimer`（250ms）轮询子进程存活、检查就绪信号、刷新运行时间
- **状态决定行为**：主按钮在 `Idle/Failed` 是"启动服务/重试"，`Running` 是"打开界面"，`Launching` 是灰的

**收益**：所有 UI 变化只写一处，避免"某个分支忘了改按钮文字"这类 bug。

### 4.3 启动一个外部程序并跟踪它

```csharp
ProcessStartInfo psi = new ProcessStartInfo();
psi.FileName = nodeExe;                       // 直接起 node.exe，不经过 .cmd shim
psi.Arguments = "\"…\\dsh\\lib\\bin.js\" web --no-open --port 3080";
psi.UseShellExecute = false;                  // 要重定向就必须 false
psi.CreateNoWindow = true;
psi.RedirectStandardInput = true;             // stdin 也接过来，之后立刻 Close()
psi.RedirectStandardOutput = true;
psi.RedirectStandardError = true;
psi.StandardOutputEncoding = Encoding.UTF8;   // 中文输出不乱码
psi.EnvironmentVariables["NO_COLOR"] = "1";   // 关掉彩色转义，方便解析

Process p = new Process { StartInfo = psi, EnableRaisingEvents = true };
p.OutputDataReceived += (s, e) => OnChildLine(e.Data);
p.ErrorDataReceived  += (s, e) => OnChildError(e.Data);
p.Start();
p.BeginOutputReadLine();                      // 必须在 Start 之后
p.BeginErrorReadLine();
p.StandardInput.Close();
```

要点：**直接起 `node.exe` + 脚本路径**，避免经过 `dsh.cmd` 这层批处理（多一层进程、输出会混、杀进程也不干净）。

### 4.4 就绪判定（三重保险）

1. 读到 `dsh web: http://127.0.0.1:PORT/?token=…` → 解析出端口和令牌，直接可用
2. 4 秒后端口能应答 HTTP（`HttpResponds`）→ 也认为就绪
3. 进程退出且有错误输出 → 失败，把**过滤后的**错误行显示在界面上

错误过滤很关键：Node 的堆栈十几行，直接显示没法看，所以只保留 `Error:` 开头或含 `EPERM/EBUSY/EADDRINUSE` 的行。

### 4.5 取消/关闭时的进程树清理

```csharp
taskkill /PID <pid> /T /F      // /T 连子孙进程一起杀
```
配合"看门狗定时器"：点停止后 1 秒强制再杀一次，并等 3 秒确认退出，避免卡在"正在停止"。

### 4.6 窗口操作（Win32 互操作）

| 目的 | API |
| --- | --- |
| 找到浏览器窗口 | `EnumWindows` + `GetWindowText` + `GetClassName` + `IsWindowVisible` |
| 强行提到前台 | `SystemParametersInfo` + `AttachThreadInput` + `SetForegroundWindow` |
| 发 Ctrl+W | `SendInput`（4 个 `INPUT`：Ctrl↓ W↓ W↑ Ctrl↑） |
| 兜底关标签页 | `PostMessage(hwnd, WM_CLOSE)` |
| 启动时定位屏幕右下角 | `Screen.FromPoint(Cursor.Position).WorkingArea` |

### 4.7 自绘控件（圆角与自绘进度条）

WinForms 原生控件很方，所以自己画：

```csharp
// 圆角路径：四段圆弧 + 闭合
path.AddArc(x, y, d, d, 180, 90);   // 左上
path.AddArc(right - d, y, d, d, 270, 90);
path.AddArc(right - d, bottom - d, d, d, 0, 90);
path.AddArc(x, bottom - d, d, d, 90, 90);
path.CloseFigure();

// 用路径既做填充也做裁剪：窗体/面板/圆球都是这样来的
Region = new Region(path);
```

进度条则是"先画圆角轨道 → 用同一路径 `SetClip` → 填充矩形 → 居中写百分比"。

### 4.8 缩略圆形：Region 是正圆，圆外不可见也不可点

```csharp
// 缩略态
circle.AddEllipse(0, 0, d, d);
Region = new Region(circle);
```
这比"矩形窗口 + 透明色"更彻底：**圆以外的区域不属于窗口**，既画不出东西，也点不到。

### 4.9 缩略动画（平滑缩放 + 不会"跳回去"）

```csharp
// 记录起点和终点，15ms 定时器按 smoothstep 插值
double k = t * t * (3 - 2 * t);          // 缓入缓出
Bounds = Lerp(_animFrom, _animTo, k);
```
位置规则：**就地在原地**——圆球固定在当前面板的右下角，展开也以圆球当前位置为锚点（超出屏幕自动贴边），不做"位置记忆回退"。

### 4.10 倒计时与可点击

- `_collapseTimer`（15s）到点缩略；`_countdownTimer`（1s）刷新那行 `Ns后缩小`
- 那行其实是个 `Label`：`Cursor = Hand`、`MouseEnter/Leave` 改颜色、`Click` 直接调 `CollapseToBubble()`

### 4.11 图标生成（PowerShell + System.Drawing）

`make-icon.ps1` 做两件事：

1. 把源图**居中裁成正方形 → 缩放 → 圆角/圆形裁剪**，输出 6 种尺寸组装成多尺寸 `.ico`（16/32/48/64/128/256）
2. 同时输出一张 256px 的 `logo.png` 给面板用

ICO 文件格式是自己拼的（`ICONDIR` + `ICONDIRENTRY[]` + PNG 数据），因为 .NET 没有现成的多尺寸 ICO 写入 API。

---

## 五、如果想手写，建议这样入手

### 路线（每一步都能独立跑起来，不要一次写完）

**第 0 步 · 先让"编译 → 运行"这条路通**
写一个空的 `winexe`，只弹一个 `MessageBox`，用 `csc` 编译，确认能双击运行、无控制台窗口。
> 这一步能提前暴露"路径/编码/编译器"类问题，比写完 2000 行再踩强得多。

**第 1 步 · 写一个显眼的启动日志**
```csharp
[STAThread] static void Main(string[] a) {
    try { File.AppendAllText(@"C:\temp\boot.log", DateTime.Now + " start\n"); Run(a); }
    catch (Exception ex) { File.AppendAllText(@"C:\temp\boot.log", ex.ToString()); }
}
```
> 窗口程序崩了是"静默"的，日志是唯一可靠的证据。

**第 2 步 · 手工把 `dsh web` 跑通**
在命令行跑 `node …\bin.js web --no-open --port 3080`，观察它打印什么、什么时候可以访问、`.dsh` 目录下什么文件被写。
> 先有"命令行原型"，再包 UI。UI 只是外壳。

**第 3 步 · 最小可用面板**
一个无边框、置顶、定位到右下角的小窗体 + 一个 `Label` 显示"启动中…"。
先不要圆角、不要动画、不要进度条。

**第 4 步 · 接上子进程**
`Process.Start` + `OutputDataReceived`，把子进程输出**原样**追加进日志，直到能在日志里看到就绪行。
> 这一步先只处理"成功路径"。

**第 5 步 · 状态机**
引入 `enum` + `ApplyState()`，把"启动中 / 运行中 / 失败"三种界面收敛到一个方法里。

**第 6 步 · 再补交互**
停止（`taskkill /T /F`）、打开页面（`Process.Start(url)`）、关闭、拖动窗口（`ReleaseCapture` + `WM_NCLBUTTONDOWN`）。

**第 7 步 · 最后做视觉**
圆角（`Region`）、自绘进度条、配色、图标。**视觉放在最后**，因为它最容易反复改。

**第 8 步 · 加分项**
缩略圆形、动画、倒计时、接管已有实例、隔离目录重试。

### 关键代码骨架（可直接抄）

**（1）定位到右下角并置顶**

```csharp
FormBorderStyle = FormBorderStyle.None;   // 无边框
ShowInTaskbar = false;
TopMost = true;
StartPosition = FormStartPosition.Manual;
Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
```

**（2）无边框窗口拖动**

```csharp
[DllImport("user32.dll")] static extern bool ReleaseCapture();
[DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
// MouseDown 里：
ReleaseCapture();
SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)0x2 /*HTCAPTION*/, IntPtr.Zero);
```

**（3）圆角窗口（不含透明色键）**

```csharp
protected override void OnResize(EventArgs e) {
    base.OnResize(e);
    using (var p = RoundedPath(new Rectangle(0, 0, Width, Height), 14))
        Region = new Region(p);
}
```

**（4）异步读取子进程输出**

```csharp
var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
p.OutputDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
p.Start();
p.BeginOutputReadLine();
```

**（5）判断端口上是不是我们要的服务**

```csharp
try {
    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:3080/");
    req.Proxy = null; req.AllowAutoRedirect = false; req.Timeout = 3000;
    using (var resp = req.GetResponse()) return true;           // 200/302/… 都算活着
} catch (WebException we) {
    return we.Response != null;                                 // 401 也算活着
} catch { return false; }                                        // 连不上 = 没在跑
```

**（6）杀掉整棵进程树**

```csharp
Process.Start(new ProcessStartInfo("taskkill",
    "/PID " + pid + " /T /F") { UseShellExecute = false, CreateNoWindow = true });
```

### 如果你用别的语言写，等价物

| 需求 | C# WinForms | PowerShell | Python |
| --- | --- | --- | --- |
| 无边框置顶窗体 | `FormBorderStyle.None; TopMost` | `System.Windows.Forms` 同 API | `tkinter.overrideredirect(True)` |
| 圆角窗口 | `Region` | 同 | 各框架不同，通常要平台 API |
| 子进程 + 输出 | `Process` + 事件 | `Start-Process` / `Diagnostics.Process` | `subprocess.Popen` |
| 找窗口/发按键 | `user32.dll` P/Invoke | 同 | `pywin32` |
| 打包成 exe | `csc` 直接产出 | 需 `ps2exe` 之类 | 需 `pyinstaller` |

> 结论：**Windows 上用 C# + csc 是成本最低的路**——系统自带编译器、单文件产出、Win32 互操作最直接。

---

## 六、经验清单（写给下一次）

1. **先摸清外部程序的契约**（参数、stdout、端口、文件），UI 是最后的事
2. **先写启动日志**，窗口程序崩溃是静默的；"没跑"和"崩了"必须能区分
3. **别用中文路径**放可执行文件（本例中会静默不执行）
4. **别用 `TransparencyKey`** 做圆角/异形窗口，它会毁掉文字的抗锯齿边缘
5. **`System.Windows.Forms.Timer` 不要在 Tick 里 Stop+Start**；用 `_inTick` 保护所有 Stop
6. **后台进程发的按键会被系统丢弃**，要关标签页得先解除前台锁定
7. **视觉元素（圆球/logo）尺寸要一致**，否则会露出一圈背景，看着像"黑边"
8. **所有不确定的地方都记日志**（探测结果、子进程输出、窗口匹配、动画终态）
9. **删文件前先备份**；用户的文件不是临时文件
10. **每改一处就重新编译并跑一遍**，把"改动 → 现象"绑定起来，别攒一堆改动再验证

---

## 七、关键常量与参数速查

```csharp
private const int PanelWidth  = 480;      // 面板宽（逻辑像素）
private const int PanelHeight = 200;      // 面板高
private const int CornerRadius = 14;      // 圆角半径
private const int RowIconWidth = 62;      // 状态行 logo
private const int RowIconHeight = 62;
private const int BubbleSize = 84;        // 缩略圆球直径
private const int CollapseDelayMs = 15000;// 缩略倒计时
private const int ReadyWaitMs = 240000;   // 启动超时（4 分钟）
```

命令行参数：

| 参数 | 作用 |
| --- | --- |
| `--port <n>` | 指定端口（`0` = 系统分配） |
| `--pos <x> <y>` | 指定面板初始位置 |
| `--no-auto-open` | 就绪后不自动打开浏览器 |
| `--no-bubble` | 关闭缩略功能 |
| `--collapse-after <秒>` | 覆盖缩略倒计时（自检用） |
| `--autoclose <秒>` | N 秒后退出（自检用） |
| `--log <路径>` | 指定日志文件（自检用） |

日志位置：`%LOCALAPPDATA%\DSH-Launcher\logs\dsh-launcher.log`
