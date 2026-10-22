# DSH 启动器 · 代码分析

对 [src/DSH.cs](src/DSH.cs)（单文件，2672 行）的结构与实现逐块说明。
行号对应当前版本，改了代码行号会漂移，看函数名更稳。

---

## 一、文件骨架

```
Program                 (36)  入口：命令行解析、异常兜底、启动窗体
LauncherForm : Form    (160)  主窗体：占全文约 80%
  可调参数 / 颜色       (162-217)  所有尺寸、配色、超时都集中在这里
  状态字段              (218-268)  状态机的数据成员
  ── 圆角与 Region      (279-320)
  ── logo 载入          (351-425)
  ── 缩略/展开/动画      (455-745)
  ── 界面构建 BuildUi   (750-990)
  ── 启动流程           (993-1420)
  ── 停止/关闭/打开页面  (1429-1590)
  ── 状态呈现           (1593-1830)  ApplyState / UpdateDetail
  ── 进程与端口工具      (1834-2135)
  ── 日志               (2138-2203)
自定义控件
  RoundedPanel   (2229)  圆角面板（SolidFill 用于圆形态）
  LogoBox        (2274)  图片控件（CircleClip 按正圆裁切）
  StatusDot      (2317)
  ProgressStrip  (2342)  自绘进度条
  RoundButton    (2397)  三态圆角按钮
Native           (2451)  Win32 P/Invoke
```

**为什么单文件**：这个程序的耦合度很高（窗体既管 UI 又管子进程生命周期），拆成十几个文件反而要来回跳。用 `// ═══` 分区替代多文件。

---

## 二、状态机：整个程序的大脑

### 状态定义（27 行）

```csharp
internal enum LauncherState { Launching, Running, Stopping, Idle, Failed }
```

### 数据流

```
        ┌──────────── EnsureStarted() ─────────────┐
        │                                          │
   端口已被 DSH 占用                          端口空闲
        │                                          │
     Adopt()                                StartChild()
        │                                          │
        └──────────► Running ◄──── MarkRunning() ───┘
                        │
                  OnStopButton()
                        │
                   Stopping ──► FinalizeStop() ──► Idle
                        │
        启动失败/超时 ──► Failed ──(点"重试")──► EnsureStarted()
```

### 谁来改状态

只有两个入口：

- `SetState(s)`（1593）→ 内部调用 `ApplyState()`
- `ApplyState()`（1599）→ **所有控件的外观都在这里统一刷新**

`ApplyState()` 是一个大 `switch`，每种状态决定：

| 元素 | Launching | Running | Stopping | Failed | Idle |
| --- | --- | --- | --- | --- | --- |
| 状态文字 | 正在启动… | DSH 正在运行中 | 正在停止… | DSH 启动失败 | DSH 未运行 |
| 进度条 | **显示** | 隐藏 | 显示(0%) | 隐藏 | 隐藏 |
| 运行信息行 | 隐藏 | **显示** | 显示(结束进程) | 显示(错误) | 隐藏 |
| 主按钮 | 打开界面(灰) | 打开界面 | 打开界面(灰) | 重试 | 启动服务 |
| 停止按钮 | 可用 | 可用 | 正在停止(灰) | 灰 | 灰 |

**这样写的好处**：新增一个状态只需要加一个 `case`，不必到处找"还有哪里要改按钮文字"。
**代价**：`ApplyState()` 会在 Tick 里被间接调用，所以必须处理重入问题（见第五节）。

### 谁驱动状态

```
_uiTimer (250ms)  ──► OnTick() ──► TickCore()
                                    ├─ 有就绪信号？ → MarkRunning()
                                    ├─ Launching？  → TickProgress()/超时/子进程死了
                                    ├─ Running？    → UpdateDetail() 每秒刷运行时间
                                    └─ 按状态重新驱动定时器
_stopWatchdog(1s) ──► OnStopWatchdog() → 强制杀进程树 + 收尾
_collapseTimer(15s)──► CollapseToBubble()
_countdownTimer(1s)──► UpdateCountdown()
_animTimer(15ms)  ──► OnAnimTick() → ApplyAnimatedBounds()
```

---

## 三、启动流程：探测 → 接管或拉起 → 等就绪

### EnsureStarted()（999）

```csharp
int port = _forcedPort > 0 ? _forcedPort : DefaultPort;   // 默认 3080
if (port > 0) {
    bool inUse = IsPortInUse(port);                        // 试绑一下 TCP
    bool isDsh = false;
    if (inUse) {
        isDsh = ProbeDsh(port);                            // HTTP 探测
        if (!isDsh) { Thread.Sleep(1000); isDsh = ProbeDsh(port); }  // 对方可能刚启动
    }
    if (isDsh) { Adopt(port); _uiTimer.Start(); return; }  // 接管，绝不重复启动
    if (inUse) port = 0;                                   // 别的程序占用 → 让系统分配
}
StartChild(port);
```

**关键设计**：先探测再启动。因为 `dsh web` 每次都会重写 `.dsh/profiles/web/cordis.yml`，两个实例会互相锁冲突（`EPERM/EBUSY`）。

### ProbeDsh()（1972）——"宽容探测"

```csharp
req.Proxy = null;              // 本机探测绝不能走系统代理
req.AllowAutoRedirect = false;
try { using (resp = req.GetResponse()) return true; }        // 200/302/… 算活着
catch (WebException we) { return we.Response != null; }      // 401（信任围栏）也算活着
catch { return false; }                                      // 连不上 = 没在跑
```

只要**能收到任何 HTTP 响应**就认定是 DSH：因为它的浏览器信任围栏会回 401，而别的程序占端口时通常连不上或返回完全不同的东西。

### StartChild()（1057）——起子进程

```csharp
psi.FileName  = node.exe;                          // 直接起 node，不经过 dsh.cmd
psi.Arguments = "\"…dsh\\lib\\bin.js\" web --no-open --port 3080";
psi.UseShellExecute = false;                       // 要重定向必须 false
psi.CreateNoWindow  = true;                        // 不弹黑框
psi.RedirectStandardInput/Output/Error = true;     // 三个都接
psi.StandardOutputEncoding = Encoding.UTF8;        // 中文不乱码
psi.EnvironmentVariables["NO_COLOR"] = "1";        // 关彩色转义，便于解析
if (_retryWithPrivateHome) psi.EnvironmentVariables["DSH_HOME"] = 隔离目录;
p.OutputDataReceived += (s,e) => OnChildLine(e.Data);
p.Start();
p.BeginOutputReadLine();   // 必须在 Start 之后
p.BeginErrorReadLine();
p.StandardInput.Close();   // 防止子进程读 stdin 挂住
```

### 就绪判定：三重保险

1. **主路径**：`OnChildLine` 收到含 `dsh web:` 的行 → `MarkRunning(line)`，顺便用 `ExtractUrl()` 取出端口和 token
2. **兜底**：`TickCore` 里超过 4 秒且 `HttpResponds(port)` 为真 → 也认为就绪
3. **失败**：子进程已退出 → `Fail(错误行)`

### 错误行过滤（1180）

Node 报错有十几行堆栈，直接显示没法看：

```csharp
if (t.StartsWith("at ")) return;            // 堆栈帧
if (t.StartsWith("node:")) return;          // 内部路径
if (t.StartsWith("Node.js v")) return;      // 版本横幅
if (t.StartsWith("errno:") || "code:" || "syscall:" || "path:") return;
if (t.StartsWith("Error:")) { _pendingErrorLine = t; return; }  // 关键的一行
```

### 隔离目录重试（2015 / 2041）

```csharp
private bool NeedsPrivateHomeRetry()   // 判断错误是不是"配置目录被占用"
{
    if (_retryUsed || _usingPrivateHome) return false;   // 只重试一次
    bool perm = e.Contains("EPERM") || e.Contains("EBUSY") || e.Contains("EACCES") || …;
    bool path = e.Contains("cordis.yml") || e.Contains("profiles") || e.Contains(".dsh");
    return perm && path;
}
```

命中后：复制 `~/.dsh/profiles` 到 `%LOCALAPPDATA%\DSH-Launcher\dsh-home`，用 `DSH_HOME` 指过去重启一次。

---

## 四、界面与自绘

### BuildUi()（750）——坐标是"手摆"的

面板 480×200，元素位置全部写死：

```
(16,12)  标题 "DeepSeek Harness"          11pt 粗体
(14,40)  分隔线 1px 宽 452
(16,44)  logo 62×62
(88,40)  状态文字 13pt 粗体               宽 368
(88,78)  进度条 / 运行信息行（同一位置二选一）
(0,158)  倒计时行（居中，可点击）
y=108    三个按钮 116×38，水平居中
```

**"同一位置二选一"**是这里最需要注意的设计：进度条和运行信息行占据**同一个矩形**，靠 `Visible` 切换。所以任何"把所有控件设成可见"的操作都会让它们同时出现——这正是之前那个 bug 的根源。

### 自绘控件

**（1）圆角路径**（2206）：所有圆角都靠它

```csharp
path.AddArc(x, y, d, d, 180, 90);                  // 左上
path.AddArc(right - d, y, d, d, 270, 90);          // 右上
path.AddArc(right - d, bottom - d, d, d, 0, 90);   // 右下
path.AddArc(x, bottom - d, d, d, 90, 90);          // 左下
path.CloseFigure();
```

**（2）ProgressStrip**（2342）：轨道 → 裁剪 → 填充 → 居中百分比

```csharp
using (var path = RoundedPath(r, Height / 2)) {
    e.Graphics.FillPath(trackBrush, path);          // 圆角轨道
    var st = e.Graphics.Save();
    e.Graphics.SetClip(path);                       // 裁剪成圆角
    e.Graphics.FillRectangle(accentBrush, fillRect); // 填充进度
    e.Graphics.Restore(st);
}
TextRenderer.DrawText(…, "42%", …, HorizontalCenter | VerticalCenter);
```

**（3）RoundButton**（2397）：三态（hover / down / disabled）靠 `OnMouseEnter/Leave/Down/Up` 改标志位并 `Invalidate()`。

**（4）RoundedPanel**（2229）：两种模式

- 常规：填充圆角矩形 + 可选描边
- `SolidFill = true`（缩略态）：直接铺满矩形，由窗体 `Region` 去裁圆

**（5）LogoBox**（2274）：`CircleClip = true` 时裁成正圆绘制——这是消除圆球"黑边"的关键（图像与圆形窗口完全重合，不露背景）。

### 圆角与圆形窗口（279 / 299）

```csharp
protected override void OnResize(EventArgs e)
{
    if (_bubbleMode) {                         // 缩略态：窗口就是一个正圆
        circle.AddEllipse(0, 0, d, d);
        Region = new Region(circle);
        return;
    }
    Region = new Region(RoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius));
}
```

**`Region` 比"透明色键"更好的原因**：圆/圆角以外**不属于窗口**，既画不出也点不到；而 `TransparencyKey` 会把文字抗锯齿的半透明边缘也穿掉（重影的根源）。

---

## 五、两个定时器相关的坑（代码里都有注释）

### 坑 A：不能在 Tick 里 Stop 再 Start（1214）

```csharp
private void OnTick()
{
    _inTick = true;                 // 进回调先立旗
    try { TickCore(); }
    finally { _inTick = false; }
}
```

所有 `Stop()` 都加保护：

```csharp
if (!_inTick && _uiTimer.Enabled) _uiTimer.Stop();
```

并在 `TickCore` 结尾按状态重新驱动：

```csharp
if (_state != Idle && _state != Failed) { if (!_uiTimer.Enabled) _uiTimer.Start(); }
else if (_uiTimer.Enabled && !_inTick)  { _uiTimer.Stop(); }
```

> .NET Framework 的 `System.Windows.Forms.Timer` 在自己的 Tick 里被 `Stop()` 之后再 `Start()` **不会重新装表**，界面会永久停止刷新。

### 坑 B：可见性必须"归一化"（575）

```csharp
private void ApplyContentVisibility()
{
    bool running = _state == LauncherState.Running;
    _bar.Visible      = !running && _state != LauncherState.Idle;  // 进度条只在启动中
    _detail.Visible   = running;                                   // 运行信息常驻
    _countdown.Visible = running;
    if (!_inTick) ApplyState();                                    // 让按钮/颜色也一致
}
```

`SetPanelContentVisible(true)` 是"全部可见"的粗暴操作，展开面板后必须再调它归一化，否则进度条会顶掉运行信息。

---

## 六、缩略圆形：状态、动画、拖拽

### 收起（543）

```csharp
Rectangle from = Bounds;                       // 就地从当前面板位置收缩
_bubbleMode = true;                            // 先置标志，OnResize 才知道要裁圆
_countdownTimer.Stop();
SetPanelContentVisible(false);                 // 隐藏所有内容，只留圆球 logo
StartBoundsAnimation(from, BubbleBoundsFrom(from));   // 圆球锚在面板右下角
```

### 展开（556）

```csharp
Rectangle from = Bounds;                       // 圆球在哪就在哪展开（不做位置记忆回退）
_bubbleMode = false;
SetPanelContentVisible(true);
ApplyContentVisibility();                      // 关键：归一化可见性
UpdateDetail();                                // 立刻填上运行信息
StartBoundsAnimation(from, ExpandedBoundsFrom(from));
_collapseScheduled = false; ScheduleCollapse();// 重新计时
```

`ExpandedBoundsFrom()` 会把面板**贴边收进屏幕**：

```csharp
if (x + w > wa.Right)  x = wa.Right - w;
if (y + h > wa.Bottom) y = wa.Bottom - h;
if (x < wa.Left) x = wa.Left;
if (y < wa.Top)  y = wa.Top;
```

### 动画（668 / 718）

```csharp
double k = t * t * (3 - 2 * t);   // smoothstep：两端平滑、中间快
Bounds = Lerp(_animFrom, _animTo, k);
```

`t` 由 `_animTimer`（15ms）推进，`_animDurationMs` 默认 260ms；`t >= 1` 时调 `FinishAnimation()` 做收尾（摆正 logo、重设 Region）。

### 拖拽（510）

滚动位置用**屏幕坐标差**而不是控件坐标（避免窗口移动后坐标跳变）：

```csharp
MouseDown: _bubbleDragStart = Cursor.Position; _bubbleDragOrigin = Location; _bubbleMoved = false;
MouseMove: int dx = Cursor.Position.X - _bubbleDragStart.X; …
           if (!_bubbleMoved && |dx|+|dy| < 4) return;   // 抖动阈值
           _bubbleMoved = true; Location = new Point(origin.X + dx, origin.Y + dy);
MouseUp:   if (_bubbleMoved) 记住位置; else ExpandFromBubble();   // 没动 = 点击
```

> 用 `Click` 事件判断"点击展开"会误判拖动，所以改用 `MouseUp` + 位移阈值。

---

## 七、"停止服务"为什么要那么绕

需求：点停止 → **关掉浏览器里那个 DSH 标签页** → 再结束服务。

### 关标签页（1511）

```csharp
IntPtr hwnd = Native.FindWindowByTitleAndClass(
    new[] { "DeepSeek Harness", "DSH" },                       // 标题候选
    new[] { "Chrome_WidgetWin_1", "MozillaWindowClass", … },    // 窗口类候选
    portToken);                                                 // 或标题里含 :3080
if (hwnd == IntPtr.Zero) { 记日志并放弃; return; }

Native.ShowWindow(hwnd, SW_RESTORE);
bool activated = Native.ForceActivate(hwnd);   // 关键：解除前台锁定
Thread.Sleep(450);
Native.SendCtrlW();                            // 正常路径
Thread.Sleep(700);
if (Native.IsWindow(hwnd)) {                   // 兜底
    Native.SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
```

### ForceActivate（2517）——后台进程发按键会被丢弃

```csharp
SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, prev, 0);
SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE); // 临时解禁
if (targetThread != ourThread) attached = AttachThreadInput(ourThread, targetThread, true);
ShowWindow(hwnd, SW_RESTORE);
SetForegroundWindow(hwnd);
// finally: 解除 attach，并把超时恢复成 200000ms
```

**不这么做会怎样**：`SetForegroundWindow` 失败但返回 true，随后 `SendInput` 的按键落到别的窗口——日志显示"已发送"，浏览器毫无反应（实测踩过）。

`SendCtrlW()`（2656）构造 4 个 `INPUT`：Ctrl↓ W↓ W↑ Ctrl↑。

---

## 八、数据来源：PID 与运行时间

### 运行时间（1688 UpdateDetail）

```csharp
if (_state == Running) {
    string port = _port > 0 ? _port.ToString() : "—";
    string pid  = _pid  > 0 ? _pid.ToString()  : "—";
    string up   = _pid  > 0 ? FormatUptime(DateTime.Now - _startedAt) : "—";

    if (!_startedAtKnown && _port > 0) {          // 接管的外部实例
        if ((DateTime.Now - _lastPidProbeAt).TotalSeconds >= 5) {   // 限流：查一次要起 netstat
            _lastPidProbeAt = DateTime.Now;
            if (_pid <= 0) { _pid = FindListeningPid(_port); }      // netstat -ano 反查
            DateTime real = GetProcessStartTime(_pid);              // 进程真实启动时间
            if (real != DateTime.MinValue) { _startedAt = real; _startedAtKnown = true; }
        }
        up = _startedAtKnown ? FormatUptime(DateTime.Now - _startedAt) : "—";
    }
    _detail.Text = "端口 " + port + "  ·  PID " + pid + "  ·  运行 " + up;
}
```

**为什么要反查**：接管已有实例时不知道它的启动时间，直接 `DateTime.Now` 会显示"从接管那一刻起算"的假运行时长。

`FindListeningPid()`（1747）用 `netstat -ano -p tcp` 解析 `LISTENING` 行取最后一列 PID——因为 .NET Framework 没有现成的"端口 → PID"API。

### 心跳与 UI dump

```csharp
if ((DateTime.Now - _lastRunningLogAt).TotalMinutes >= 10) Log("running heartbeat: …");
if ((DateTime.Now - _lastUiDumpAt).TotalSeconds  >= 30)    Log("ui: bar=… detail=… text=\"…\"");
```

`ui:` 那行是为了**事后核对界面实际内容**（"用户看到的不对"时，日志能直接证明是哪个控件、什么文字）。

---

## 九、Win32 互操作清单（2451 Native）

| 成员 | 用途 |
| --- | --- |
| `ReleaseCapture` + `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)` | 无边框窗口拖动 |
| `EnumWindows` + `GetWindowText` + `GetClassName` + `IsWindowVisible` + `GetWindow(GW_OWNER)` | 按标题+窗口类找浏览器窗口 |
| `SystemParametersInfo(SPI_GET/SETFOREGROUNDLOCKTIMEOUT)` | 解除前台锁定 |
| `AttachThreadInput` + `GetWindowThreadProcessId` + `GetCurrentThreadId` | 挂输入线程以便激活 |
| `ShowWindow` / `SetForegroundWindow` / `IsWindow` | 激活与存活判断 |
| `SendInput` + `INPUT`/`KEYBDINPUT` 结构 | 发 Ctrl+W |
| `SendMessage(WM_CLOSE)` | 关标签页兜底 |
| `GetWindowRect`(`RECT`) | 自检：窗口物理像素矩形（对照截图坐标） |

`FindWindowByTitleAndClass()`（2614）的回调里要**同时**判断：可见、无 owner（排除工具窗口/托盘）、类名匹配、标题匹配，命中后 `return false` 停止枚举。

---

## 十、日志与自检设计

```csharp
DataRoot()      → %LOCALAPPDATA%\DSH-Launcher
LogFilePath()   → <DataRoot>\logs\dsh-launcher.log（目录不可写则退回 exe 目录，再退回 %TEMP%）
AppendLog()     → lock + File.AppendAllText(UTF8)，任何异常都吞掉（日志不能影响主流程）
Program.Main    → 第一行就写 dsh-launcher-boot.log（"进 Main 了吗"），外层 try/catch 写 -fatal.log
```

**这套设计的价值**：窗口程序崩溃是**静默**的。有一次 exe 放在中文路径下"退出码 0、连 Main 都没进"，正是 `boot.log` 完全没生成才定位到"进程根本没起来"。

自检用的命令行参数（88-158）：`--port` / `--pos x y` / `--autoclose` / `--collapse-after` / `--log` / `--no-bubble` / `--no-auto-open` / `--headless`。这些参数让我能**程序化地**驱动界面到指定状态并读日志验证，不需要人盯着屏幕。

---

## 十一、如果要改，改哪里

| 想改什么 | 位置 |
| --- | --- |
| 面板尺寸 | `PanelWidth` / `PanelHeight`（164-165） |
| 状态行 logo | `RowIconWidth` / `RowIconHeight`（173-174） |
| 缩略圆球直径 | `BubbleSize`（177）——圆内 logo 自动跟随 |
| 缩略倒计时 | `CollapseDelayMs`（176） |
| 圆角半径 | `CornerRadius`（166） |
| 底部倒计时行位置 | `CountdownTop`（167） |
| 配色 | `BgColor` / `AccentColor` / `OkColor` / `ErrorColor` …（184-192） |
| 字号 | `BuildUi()` 里各处 `new Font(...)` |
| 启动超时 | `ReadyWaitMs`（169） |
| 进度条节奏 | `TickProgress()`（1379）与 `StatusFor()`（1396） |
| 就绪探测策略 | `ProbeDsh()`（1972） |
| 关标签页的匹配规则 | `CloseBrowserTab()`（1511）的标题/类名候选数组 |

改动后：双击 [构建.bat](构建.bat) 重新编译安装；日志看 `%LOCALAPPDATA%\DSH-Launcher\logs\dsh-launcher.log`。
