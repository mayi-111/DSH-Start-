// DSH 启动器 / DSH Launcher
// 单文件 WinForms 程序（.NET Framework 4.x，无第三方依赖）
// 功能：
//   1. 启动时自动拉起 DeepSeek Harness（dsh web）
//   2. 右下角悬浮弹窗显示启动状态与进度
//   3. 启动完成后显示 端口 / PID / 运行时间
//   4. 打开页面 / 停止服务 / 关闭弹窗
// 编译：见 build.ps1

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace DshLauncher
{
    internal enum LauncherState
    {
        Launching,
        Running,
        Stopping,
        Idle,
        Failed
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 最早期落盘：即使后面的初始化抛异常，也能看到原因
            try
            {
                string boot = Path.Combine(LauncherForm.DataRoot(), "dsh-launcher-boot.log");
                Directory.CreateDirectory(LauncherForm.DataRoot());
                File.AppendAllText(boot,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " Main entered; baseDir=" + AppDomain.CurrentDomain.BaseDirectory
                    + "; temp=" + Path.GetTempPath()
                    + "; args=" + string.Join(" ", args) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }

            try
            {
                RunLauncher(args);
            }
            catch (Exception ex)
            {
                RecordFatal(ex);
                throw;
            }
        }

        /// <summary>把未处理异常写到临时目录和程序目录，帮用户定位启动失败原因。</summary>
        internal static void RecordFatal(Exception ex)
        {
            string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + " FATAL " + ex + Environment.NewLine;
            string[] targets = new string[]
            {
                Path.Combine(LauncherForm.DataRoot(), "dsh-launcher-fatal.log"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "dsh-launcher-fatal.log"),
                Path.Combine(Path.GetTempPath(), "dsh-launcher-fatal.log")
            };
            foreach (string t in targets)
            {
                try
                {
                    string dir = Path.GetDirectoryName(t);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(t, text, Encoding.UTF8);
                }
                catch { }
            }
        }

        private static void RunLauncher(string[] args)
        {
            bool show = true;
            bool autoOpen = true;
            bool bubble = true;
            int forcedPort = 0;
            int autoCloseSeconds = 0;
            int collapseAfter = 0;
            Point startPos = Point.Empty;       // --pos x,y 指定面板初始位置（空 = 右下角）
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--headless") show = false;
                else if (a == "--no-auto-open") autoOpen = false;
                else if (a == "--no-bubble") bubble = false;
                else if (a == "--pos" && i + 2 < args.Length)
                {
                    // 形式：--pos 900 400（也兼容 --pos 900,400）
                    string sx = args[i + 1];
                    string sy = args[i + 2];
                    int comma = sx.IndexOf(',');
                    if (comma > 0 && sy.Length == 0)
                    {
                        sy = sx.Substring(comma + 1);
                        sx = sx.Substring(0, comma);
                    }
                    int px, py;
                    if (int.TryParse(sx, out px) && int.TryParse(sy, out py))
                    {
                        startPos = new Point(px, py);
                    }
                    i += 2;
                }
                else if (a == "--collapse-after" && i + 1 < args.Length)
                {
                    int secs;
                    if (int.TryParse(args[i + 1], out secs)) collapseAfter = secs;
                    i++;
                }
                else if (a == "--log" && i + 1 < args.Length)
                {
                    LauncherForm.LogPathOverride = args[i + 1];
                    i++;
                }
                else if (a == "--autoclose" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out autoCloseSeconds);
                    i++;
                }
                else if (a == "--port" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out forcedPort);
                    i++;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LauncherForm form = new LauncherForm(forcedPort);
            form.AutoOpenPage = autoOpen;
            form.BubbleEnabled = bubble;
            if (!startPos.IsEmpty) form.StartPositionOverride = startPos;
            if (collapseAfter > 0) form.CollapseAfterSeconds = collapseAfter;
            if (!show) form.Opacity = 0;
            if (autoCloseSeconds > 0) form.AutoCloseAfter = autoCloseSeconds;

            form.Shown += delegate { form.BeginLaunch(); };
            Application.Run(form);
        }
    }

    internal sealed class LauncherForm : Form
    {
        /// <summary>
        /// 为 true 时 Show() 不抢焦点。只在"全屏应用退出后自动恢复圆球"时临时打开，
        /// 避免把你正在打字的窗口焦点抢走；正常启动（用户主动点快捷方式）仍然正常激活。
        /// </summary>
        private bool _suppressActivation;

        protected override bool ShowWithoutActivation
        {
            get { return _suppressActivation; }
        }

        // ── 可调参数 ────────────────────────────────────────────────────────────
        private const int DefaultPort = 3080;
        private const int PanelWidth = 480;
        private const int PanelHeight = 200;
        private const int CornerRadius = 14;
        private const int CountdownTop = 158;        // 底部倒计时行的 y（按钮排在它上面）
        private const int ProgressCap = 92;          // 未就绪前进度上限
        private const int ReadyWaitMs = 240000;      // 服务就绪超时（4 分钟）
        private const string ReadyMarker = "dsh web:";
        private const string LogFileName = "dsh-launcher.log";
        private const int TitleIconSize = 24;        // 单行图标尺寸（备用）
        private const int RowIconWidth = 62;         // 状态行 logo 尺寸
        private const int RowIconHeight = 62;
        private const int RowIconSize = RowIconWidth; // 载入时的预缩放边长
        private const int CollapseDelayMs = 15000;   // 运行多久后自动缩成小圆球（15 秒）
        private const int BubbleSize = 85;           // 缩略小圆球直径
        private const string LogoSideFileName = "logo.png";
        private static readonly Color CountdownColor = Color.FromArgb(110, 117, 132);   // 倒计时：淡灰，不抢眼
        private static readonly Color CountdownHoverColor = Color.FromArgb(196, 205, 222); // 悬停：提亮，提示可点
        // 缩略圆球：窗体背景用"透明色键"实现真透明；只画 logo，不画圆体底色
        // （若以后想加圆盘底色/描边，用 BubbleFillColor / BubbleEdgeColor 即可）
        private static readonly Color KeyColor = Color.FromArgb(1, 2, 3);
        private static readonly Color BubbleFillColor = Color.FromArgb(24, 26, 32);
        private static readonly Color BubbleEdgeColor = Color.FromArgb(70, 76, 92);
        private static string _logoPathOverride;

        // ── 颜色 ───────────────────────────────────────────────────────────────
        private static readonly Color BgColor = Color.FromArgb(24, 26, 32);
        private static readonly Color BorderColor = Color.FromArgb(58, 63, 76);
        private static readonly Color TitleColor = Color.FromArgb(240, 243, 250);
        private static readonly Color StatusColor = Color.FromArgb(150, 192, 255);
        private static readonly Color DetailColor = Color.FromArgb(152, 160, 176);
        private static readonly Color AccentColor = Color.FromArgb(64, 132, 255);
        private static readonly Color ErrorColor = Color.FromArgb(255, 122, 122);
        private static readonly Color OkColor = Color.FromArgb(86, 214, 132);
        private static readonly Color IdleColor = Color.FromArgb(150, 156, 168);
        private static readonly Color TrackColor = Color.FromArgb(44, 48, 58);
        private static readonly Color BtnColor = Color.FromArgb(44, 49, 60);
        private static readonly Color BtnHoverColor = Color.FromArgb(58, 65, 80);
        private static readonly Color BtnDownColor = Color.FromArgb(34, 38, 48);
        private static readonly Color BtnDisabledColor = Color.FromArgb(34, 37, 44);
        private static readonly Color BtnTextColor = Color.FromArgb(228, 233, 242);
        private static readonly Color BtnDisabledTextColor = Color.FromArgb(110, 116, 128);

        // ── 控件 ───────────────────────────────────────────────────────────────
        private RoundedPanel _panel;
        private Label _title;
        private Panel _divider;
        private LogoBox _logo;
        private StatusDot _dot;
        private RoundButton _btnClose;
        private Label _status;
        private ProgressStrip _bar;
        private Label _detail;
        private Label _countdown;
        private RoundButton _btnMain;
        private RoundButton _btnStop;
        private RoundButton _btnExit;
        private ToolTip _tip;

        // ── 状态 ───────────────────────────────────────────────────────────────
        private LauncherState _state = LauncherState.Idle;
        private Process _child;
        private int _port;
        private int _pid;
        private DateTime _startedAt = DateTime.MinValue;
        private int _progress;
        private DateTime _progressStartedAt;
        private int _forcedPort;
        private string _baseUrl;
        private string _openUrl;
        private string _errorText;
        private bool _openPending;
        private bool _autoOpenNext = true;      // 启动完成后是否自动打开 Web 页面
        private bool _closed;
        private volatile string _pendingReadyLine;
        private volatile string _pendingErrorLine;
        private bool _retryWithPrivateHome;     // 下次拉起时使用隔离配置目录
        private string _lastFailLine;           // 上一次失败的错误行（用于判断"同样的错"）
        private int _consecutiveFail;           // 连续同样的失败次数（用于退避等待）
        private bool _usingPrivateHome;
        private int _spawnAttempts;
        private double _lastHeartbeatMs;
        private bool _startedAtKnown;           // false = 接管的外部实例，启动时间是推测值
        private DateTime _lastPidProbeAt = DateTime.MinValue;
        private DateTime _lastRunningLogAt = DateTime.MinValue;
        private DateTime _lastUiDumpAt = DateTime.MinValue;
        private bool _inTick;                   // 是否正在定时器回调内部
        private Image _logoImage;               // 标题行左侧的小图标

        // ── 计时器 ─────────────────────────────────────────────────────────────
        private Timer _uiTimer;
        private Timer _stopWatchdog;
        private Timer _collapseTimer;
        private Timer _animTimer;
        private Timer _countdownTimer;

        // ── 收起 / 展开动画 ────────────────────────────────────────────────────
        private bool _bubbleMode;
        private bool _hiddenByFullscreen;       // 因前台是全屏应用而临时藏起了圆球
        private double _lastFullscreenCheckMs;
        private bool _bubbleEnabled = true;     // 自检用：--no-bubble 可关掉
        private bool _collapseScheduled;        // 收起只安排一次
        private int _collapseDelayMs = CollapseDelayMs;
        private DateTime _collapseDueAt = DateTime.MinValue;   // 预计缩略时刻（用于显示倒计时）
        private DateTime _animStartAt;
        private int _animDurationMs = 260;
        private Rectangle _animFrom;
        private Rectangle _animTo;

        // ── 圆球拖动 ──────────────────────────────────────────────────────────
        private Point _bubbleDragStart;
        private Point _bubbleDragOrigin;
        private bool _bubbleMoved;
        private Point _startPosOverride = Point.Empty;

        public LauncherForm(int forcedPort)
        {
            _forcedPort = forcedPort;
            BuildUi();
            ApplyRoundedCorners();
        }

        /// <summary>把窗体本身裁成圆角，去掉四角的黑色直角。</summary>
        private void ApplyRoundedCorners()
        {
            BackColor = BgColor;
            // 注意：大面板刻意不使用 TransparencyKey。透明色键会把文字抗锯齿边缘的
            // 半透明像素一起穿掉，在花哨壁纸上表现为字周围"重影/透底"。
            // 圆角靠下面的 Region 裁剪实现，四角本来就落在面板圆角之外，看不出差别。
            TransparencyKey = Color.Empty;
            try
            {
                using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius))
                {
                    Region = new Region(path);
                }
            }
            catch (Exception ex)
            {
                Log("ApplyRoundedCorners failed: " + ex.Message);
            }
        }

        /// <summary>进入/退出"缩略圆球"外观：切换背景色 + 透明色键。</summary>
        private void SetBubbleAppearance(bool bubble)
        {
            if (bubble)
            {
                // 窗体背景设成透明色键 → 该颜色区域真透明且点穿；
                // 圆体用抗锯齿画在上面，于是圆边变成半透明（平滑），圆外完全透明。
                BackColor = KeyColor;
                TransparencyKey = KeyColor;
                Region = null;                  // 圆形交给绘制 + 透明色键，不再用 Region 硬裁
            }
            else
            {
                BackColor = BgColor;
                TransparencyKey = Color.Empty;
                ApplyRoundedCorners();
            }
            Invalidate();
        }

        /// <summary>缩略态直接画在窗体上（不经子控件）：先铺透明色键已由 BackColor 完成。</summary>
        private void OnPaintExtra(object sender, PaintEventArgs e)
        {
            if (!_bubbleMode || _logoImage == null) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 方案 B：不画圆体底色、也不画描边，只把 logo 画上去。
            // 于是 logo 的透明区域与窗体背景同为透明色键 → 一起变透明，
            // 圆球看起来就是"一个浮在桌面上的图案"，而不是"深色圆盘 + 图案"。
            Rectangle r = new Rectangle(0, 0, Width, Height);
            using (GraphicsPath clip = new GraphicsPath())
            {
                clip.AddEllipse(0, 0, Width - 1, Height - 1);
                GraphicsState st = e.Graphics.Save();
                e.Graphics.SetClip(clip);
                e.Graphics.DrawImage(_logoImage, r);
                e.Graphics.Restore(st);
            }
        }

        /// <summary>点击点是否落在圆内（透明色键区域会点穿，这里只处理圆内）。</summary>
        private bool InsideBubbleCircle(Point p)
        {
            double cx = Width / 2.0, cy = Height / 2.0;
            double dx = p.X - cx, dy = p.Y - cy;
            double rad = Math.Min(Width, Height) / 2.0;
            return dx * dx + dy * dy <= rad * rad;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;

            // 缩略态不使用 Region：圆体连同抗锯齿边缘由 OnPaintExtra 画在窗体背景上，
            // 再由透明色键把"背景色"变成真透明——这样圆边是平滑的半透明，圆外是真实透明。
            if (_bubbleMode) return;

            try
            {
                using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius))
                {
                    Region = new Region(path);
                }
            }
            catch { }
        }

        /// <summary>启动完成后是否自动用默认浏览器打开 Web 页面。</summary>
        public bool AutoOpenPage
        {
            get { return _autoOpenNext; }
            set { _autoOpenNext = value; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 标题行 logo
        // ═══════════════════════════════════════════════════════════════════════
        /// <summary>自检/自定义用：指定 logo 图片路径。</summary>
        internal static string LogoPathOverride
        {
            get { return _logoPathOverride; }
            set { _logoPathOverride = value; }
        }

        /// <summary>
        /// 载入 logo。整张图按 RowIconSize 预缩放一次，交给 LogoBox 的 OnPaint 做高质量
        /// 缩放；整张图放大绘制不会出现某一次缩放的锯齿。找不到也不影响使用。
        /// </summary>
        private static Image LoadLogo()
        {
            foreach (string path in EnumerateLogoPaths())
            {
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    if (!File.Exists(path)) continue;
                    using (Image raw = Image.FromFile(path))
                    {
                        // 预缩放到状态行 logo 的显示尺寸（宽 × 高），保持比例居中放置
                        Bitmap bmp = new Bitmap(RowIconWidth, RowIconHeight);
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.Clear(Color.Transparent);
                            DrawFitted(g, raw, new Rectangle(0, 0, RowIconWidth, RowIconHeight), false);
                        }
                        Log("logo loaded: " + path + " (source " + raw.Width + "x" + raw.Height + ")");
                        return bmp;
                    }
                }
                catch (Exception ex)
                {
                    Log("logo load failed (" + path + "): " + ex.Message);
                }
            }
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null)
                {
                    Bitmap bmp = ico.ToBitmap();
                    Log("logo: fell back to the executable icon");
                    return bmp;
                }
            }
            catch (Exception ex)
            {
                Log("logo fallback failed: " + ex.Message);
            }
            return null;
        }

        private static string[] EnumerateLogoPaths()
        {
            return new string[]
            {
                _logoPathOverride,
                Path.Combine(DataRoot(), LogoSideFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogoSideFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico")
            };
        }

        /// <summary>按原比例居中缩放绘制到目标矩形内；allowUpscale=false 时不放大超出原始尺寸。</summary>
        internal static void DrawFitted(Graphics g, Image img, Rectangle target, bool allowUpscale)
        {
            if (img == null || target.Width <= 0 || target.Height <= 0) return;
            double scale = Math.Min((double)target.Width / img.Width, (double)target.Height / img.Height);
            if (!allowUpscale && scale > 1.0) scale = 1.0;
            int w = Math.Max(1, (int)Math.Round(img.Width * scale));
            int h = Math.Max(1, (int)Math.Round(img.Height * scale));
            int x = target.X + (target.Width - w) / 2;
            int y = target.Y + (target.Height - h) / 2;
            g.DrawImage(img, new Rectangle(x, y, w, h));
        }

        internal static void DrawFitted(Graphics g, Image img, Rectangle target)
        {
            DrawFitted(g, img, target, true);
        }

        /// <summary>是否允许运行 20 秒后自动收成右下角小圆球。</summary>
        public bool BubbleEnabled
        {
            get { return _bubbleEnabled; }
            set { _bubbleEnabled = value; }
        }

        /// <summary>自检用：覆盖自动收起前等待的秒数。</summary>
        public int CollapseAfterSeconds
        {
            set { if (value > 0) _collapseDelayMs = value * 1000; }
        }

        /// <summary>指定面板初始位置（不指定则用屏幕右下角）。</summary>
        public Point StartPositionOverride
        {
            set
            {
                _startPosOverride = value;
                Rectangle r = DefaultPanelBounds();
                if (!value.IsEmpty) r = new Rectangle(value.X, value.Y, r.Width, r.Height);
                Location = new Point(r.X, r.Y);
                Log("start position override applied -> " + Bounds);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 收起成小圆球 / 展开回面板
        // ═══════════════════════════════════════════════════════════════════════
        private void ScheduleCollapse()
        {
            if (!_bubbleEnabled || _logoImage == null || _bubbleMode) return;
            _collapseScheduled = true;
            _collapseTimer.Stop();
            _collapseTimer.Interval = _collapseDelayMs;
            _collapseTimer.Start();
            _collapseDueAt = DateTime.Now.AddMilliseconds(_collapseDelayMs);
            UpdateCountdown();
            _countdownTimer.Stop();
            _countdownTimer.Start();
            Log("collapse scheduled in " + (_collapseDelayMs / 1000) + "s");
        }

        private void CancelCollapse()
        {
            if (_collapseTimer != null) _collapseTimer.Stop();
            if (_countdownTimer != null) _countdownTimer.Stop();
            if (_countdown != null) _countdown.Text = "";
            _collapseScheduled = false;
        }

        /// <summary>刷新"xx 秒后缩为小圆球"提示；只在运行中且还没缩略时显示。</summary>
        private void UpdateCountdown()
        {
            if (_countdown == null) return;
            if (!_bubbleEnabled || _bubbleMode || _logoImage == null || _closed)
            {
                _countdown.Text = "";
                _countdownTimer.Stop();
                return;
            }
            if (_state != LauncherState.Running || _collapseDueAt == DateTime.MinValue)
            {
                _countdown.Text = "";
                return;
            }
            double left = (_collapseDueAt - DateTime.Now).TotalSeconds;
            int secs = (int)Math.Ceiling(left);
            if (secs > 0) _countdown.Text = secs.ToString(CultureInfo.InvariantCulture) + "s后缩小（点此立即缩小）";
            else _countdown.Text = "即将缩小";
        }

        /// <summary>点击倒计时那一行：不等倒计时，立刻缩成小圆球。</summary>
        private void CollapseNowByUser()
        {
            if (_state != LauncherState.Running || _bubbleMode || _closed) return;
            Log("collapse requested by clicking the countdown line");
            _collapseTimer.Stop();
            _countdownTimer.Stop();
            if (_countdown != null) _countdown.Text = "";
            CollapseToBubble();
        }

        /// <summary>圆球上按下：拖动的同时记住位置；松手时如果没怎么移动就当作"点击展开"。</summary>
        private void BubbleMouseDown(object sender, MouseEventArgs e)
        {
            if (!_bubbleMode || e.Button != MouseButtons.Left) return;
            _bubbleDragStart = Cursor.Position;
            _bubbleDragOrigin = Location;
            _bubbleMoved = false;
        }

        private void BubbleMouseMove(object sender, MouseEventArgs e)
        {
            if (!_bubbleMode || e.Button != MouseButtons.Left) return;
            Point now = Cursor.Position;
            int dx = now.X - _bubbleDragStart.X;
            int dy = now.Y - _bubbleDragStart.Y;
            if (!_bubbleMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // 抖动阈值，避免误判为拖动
            _bubbleMoved = true;
            Location = new Point(_bubbleDragOrigin.X + dx, _bubbleDragOrigin.Y + dy);
        }

        private void BubbleMouseUp(object sender, MouseEventArgs e)
        {
            if (!_bubbleMode) return;
            if (_bubbleMoved)
            {
                // 位置就是当前窗口位置，不做任何"记忆回退"：缩略/展开都在原地
                Log("bubble moved to " + Location);
            }
            else
            {
                ExpandFromBubble();             // 没移动 = 点击，就地展开回面板
            }
        }

        private void CollapseToBubble()
        {
            if (_bubbleMode || _closed) return;
            if (_state != LauncherState.Running) return;   // 出错/停止时保持可见
            Rectangle from = Bounds;                        // 就地从面板当前位置收缩
            _bubbleMode = true;
            _countdownTimer.Stop();
            if (_countdown != null) _countdown.Text = "";
            SetPanelContentVisible(false);
            SetBubbleAppearance(true);
            StartBoundsAnimation(from, BubbleBoundsFrom(from));
            Log("collapsed to bubble; from=" + from);
        }

        private void ExpandFromBubble()
        {
            if (!_bubbleMode || _closed) return;
            Rectangle from = Bounds;                        // 圆球在哪就在哪展开
            _bubbleMode = false;
            SetBubbleAppearance(false);      // 先恢复普通外观，再显示内容
            SetPanelContentVisible(true);
            ApplyContentVisibility();       // 展开后按状态归一化，进度条不会残留
            UpdateDetail();                 // 立刻填上"端口 · PID · 运行时间"，不等下一次 tick
            StartBoundsAnimation(from, ExpandedBoundsFrom(from));
            _collapseScheduled = false;     // 展开后重新计时
            ScheduleCollapse();
            Log("expanded from bubble; from=" + from);
        }

        /// <summary>
        /// 按当前状态归一化"进度条 / 运行信息 / 倒计时"三者的可见性。
        /// 展开面板时必须调用：SetPanelContentVisible(true) 会把所有控件设成可见，
        /// 若不归一化，启动用的进度条会跟着回到运行态面板上。
        /// </summary>
        private void ApplyContentVisibility()
        {
            bool running = _state == LauncherState.Running;
            _bar.Visible = !running && _state != LauncherState.Idle;   // 只有启动中显示进度条
            _detail.Visible = running;                                 // 运行信息常驻
            _countdown.Visible = running;
            if (!_inTick) ApplyState();
        }

        private void SetPanelContentVisible(bool visible)
        {
            foreach (Control c in _panel.Controls)
            {
                c.Visible = visible;
            }
            if (_panel != null)
            {
                // 缩略态必须把面板整个隐藏：面板是 Dock=Fill 的子控件，会铺满窗体并
                // 画出不透明背景，把画在窗体上的透明圆盖成"方块"。
                _panel.Visible = visible;
                _panel.OutlineColor = BorderColor;
                _panel.Radius = visible ? CornerRadius : 0;
                _panel.SolidFill = !visible;
                _panel.Invalidate();
            }
            if (!visible) Invalidate();     // 让窗体重画那个透明圆
        }

        /// <summary>启动时面板的默认位置：屏幕右下角。</summary>
        private static Rectangle DefaultPanelBounds()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle wa = screen.WorkingArea;
            if (wa.Width < 200 || wa.Height < 200) wa = Screen.PrimaryScreen.WorkingArea;
            int w = PanelWidth;
            int h = PanelHeight;
            int margin = 16;
            return new Rectangle(wa.Right - w - margin, wa.Bottom - h - margin, w, h);
        }

        /// <summary>当前屏幕工作区（按给定点所在显示器）。</summary>
        private static Rectangle WorkingAreaAt(int x, int y)
        {
            Screen screen = Screen.FromPoint(new Point(x, y));
            Rectangle wa = screen.WorkingArea;
            if (wa.Width < 200 || wa.Height < 200) wa = Screen.PrimaryScreen.WorkingArea;
            return wa;
        }

        /// <summary>小圆球直径。</summary>
        private static int BubbleDiameter()
        {
            return BubbleSize;
        }

        /// <summary>小圆球里 logo 的边长：与圆球同直径，圆形绘制后与窗口圆形完全重合，不会露出背景形成黑边。</summary>
        private static int BubbleLogoSize()
        {
            return BubbleDiameter();
        }

        /// <summary>从给定面板矩形就地收缩出的小圆球位置（右下角对齐）。</summary>
        private static Rectangle BubbleBoundsFrom(Rectangle panel)
        {
            int d = BubbleDiameter();
            return new Rectangle(panel.Right - d, panel.Bottom - d, d, d);
        }

        /// <summary>从给定圆球矩形就地展开出的面板位置（右下角对齐，并保证不超出屏幕）。</summary>
        private static Rectangle ExpandedBoundsFrom(Rectangle bubble)
        {
            int w = PanelWidth;
            int h = PanelHeight;
            int x = bubble.Right - w;
            int y = bubble.Bottom - h;
            Rectangle wa = WorkingAreaAt(bubble.Left + bubble.Width / 2, bubble.Top + bubble.Height / 2);
            if (x + w > wa.Right) x = wa.Right - w;
            if (y + h > wa.Bottom) y = wa.Bottom - h;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            return new Rectangle(x, y, w, h);
        }

        private void StartBoundsAnimation(Rectangle from, Rectangle to)
        {
            _animFrom = from;
            _animTo = to;
            _animStartAt = DateTime.Now;
            _animTimer.Stop();
            _animTimer.Start();
        }

        private void OnAnimTick()
        {
            double t = (DateTime.Now - _animStartAt).TotalMilliseconds / _animDurationMs;
            if (t >= 1.0)
            {
                _animTimer.Stop();
                ApplyAnimatedBounds(1.0);
                FinishAnimation();
                return;
            }
            ApplyAnimatedBounds(t);
        }

        private void FinishAnimation()
        {
            if (_bubbleMode)
            {
                // 收起完成：窗口本身就是那个透明的圆（OnPaintExtra 直接画在窗体上）
                Invalidate();
                Log("bubble final: bounds=" + Bounds
                    + " physical=" + Native.PhysicalRect(this)
                    + " keyColor=" + TransparencyKey);
            }
            else
            {
                OnResize(EventArgs.Empty);      // 恢复圆角矩形
                ApplyContentVisibility();       // 兜底：保证运行信息常驻、进度条不残留
                _panel.Invalidate();
            }
        }

        private void ApplyAnimatedBounds(double t)
        {
            double k = t * t * (3 - 2 * t);     // smoothstep，起止都平滑
            int x = _animFrom.X + (int)Math.Round((_animTo.X - _animFrom.X) * k);
            int y = _animFrom.Y + (int)Math.Round((_animTo.Y - _animFrom.Y) * k);
            int w = _animFrom.Width + (int)Math.Round((_animTo.Width - _animFrom.Width) * k);
            int h = _animFrom.Height + (int)Math.Round((_animTo.Height - _animFrom.Height) * k);
            if (w < 24) w = 24;
            if (h < 24) h = 24;
            Bounds = new Rectangle(x, y, w, h);
        }

        /// <summary>仅用于自检：N 秒后自动退出。</summary>
        public int AutoCloseAfter        {
            set
            {
                if (value <= 0) return;
                Timer autoClose = new Timer();
                autoClose.Interval = value * 1000;
                autoClose.Tick += delegate
                {
                    autoClose.Stop();
                    Log("autoclose fired");
                    Close();
                };
                autoClose.Start();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 界面
        // ═══════════════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            SuspendLayout();

            Text = "DeepSeek Harness";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            // 不置顶：这一步是"不要浮在其他应用上方"的关键。窗口按普通层级参与 Z 序，
            // 开游戏/全屏程序时不会挡在前面；需要看面板时用桌面快捷方式或 Ctrl+Alt+D。
            TopMost = false;
            MinimizeBox = false;
            MaximizeBox = false;
            KeyPreview = true;
            BackColor = BgColor;
            ClientSize = new Size(PanelWidth, PanelHeight);
            DoubleBuffered = true;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            _panel = new RoundedPanel();
            _panel.Dock = DockStyle.Fill;
            _panel.FillColor = BgColor;
            _panel.OutlineColor = BorderColor;
            _panel.Radius = CornerRadius;
            _panel.MouseDown += DragWindow;
            _panel.MouseMove += delegate (object s, MouseEventArgs e) { BubbleMouseMove(s, e); };
            _panel.MouseUp += delegate (object s, MouseEventArgs e) { BubbleMouseUp(s, e); };

            _logoImage = LoadLogo();

            // 标题行：DeepSeek Harness（与下方内容用一条分隔线区分）
            _title = new Label();
            _title.Text = "DeepSeek Harness";
            _title.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
            _title.ForeColor = TitleColor;
            _title.BackColor = Color.Transparent;
            _title.AutoSize = true;
            _title.Location = new Point(16, 12);

            _divider = new Panel();
            _divider.Location = new Point(14, 40);
            _divider.Size = new Size(PanelWidth - 28, 1);
            _divider.BackColor = BorderColor;

            _btnClose = new RoundButton();
            _btnClose.Text = "\u2715";
            _btnClose.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _btnClose.Size = new Size(28, 28);
            _btnClose.Location = new Point(ClientSize.Width - 40, 10);
            _btnClose.Radius = 8;
            _btnClose.Click += delegate { BeginShutdown(); };

            // 状态行：左边 logo，右边"状态文字 / 运行信息"（启动中那里显示进度条）
            int rowLeft = 16;
            if (_logoImage != null)
            {
                _logo = new LogoBox();
                _logo.Image = _logoImage;
                _logo.Size = new Size(RowIconWidth, RowIconHeight);
                _logo.Location = new Point(rowLeft, 44);
                _logo.BackColor = Color.Transparent;
                _logo.MouseDown += DragWindow;
                rowLeft += RowIconWidth + 10;
            }
            int rowWidth = PanelWidth - 16 - rowLeft;
            int rowTop = 40;

            _dot = new StatusDot();
            _dot.Location = new Point(PanelWidth - 28, rowTop + 8);
            _dot.Size = new Size(10, 10);
            _dot.BackColor = Color.Transparent;
            _dot.Visible = false;       // 状态颜色已经由状态文字表达，这里不再重复显示小圆点

            _status = new Label();
            _status.Text = "DSH 启动器";
            _status.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold, GraphicsUnit.Point);
            _status.ForeColor = StatusColor;
            _status.BackColor = Color.Transparent;
            _status.AutoSize = false;
            _status.AutoEllipsis = false;
            _status.Location = new Point(rowLeft, rowTop);
            _status.Size = new Size(rowWidth, 34);
            _status.TextAlign = ContentAlignment.MiddleLeft;

            // 启动中显示的进度条（就绪后隐藏，让位给"端口 · PID · 运行时间"）
            _bar = new ProgressStrip();
            _bar.Location = new Point(rowLeft, rowTop + 38);
            _bar.Size = new Size(rowWidth, 26);
            _bar.Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);

            // 运行信息行：与状态文字左对齐，和进度条同一位置
            _detail = new Label();
            _detail.Text = "准备就绪";
            _detail.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _detail.ForeColor = DetailColor;
            _detail.BackColor = Color.Transparent;
            _detail.AutoSize = false;
            _detail.Location = new Point(rowLeft, rowTop + 40);
            _detail.Size = new Size(rowWidth, 24);
            _detail.TextAlign = ContentAlignment.MiddleLeft;
            _detail.AutoEllipsis = true;
            _detail.Visible = false;    // 只有运行中才显示

            // 倒计时行：既是提示也是按钮——点一下立刻缩略成小圆球
            _countdown = new Label();
            _countdown.Text = "";
            _countdown.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
            _countdown.ForeColor = CountdownColor;
            _countdown.BackColor = Color.Transparent;
            _countdown.AutoSize = false;
            _countdown.AutoEllipsis = true;
            _countdown.Location = new Point(0, CountdownTop);
            _countdown.Size = new Size(PanelWidth, 18);
            _countdown.TextAlign = ContentAlignment.MiddleCenter;
            _countdown.Cursor = Cursors.Hand;
            _countdown.MouseEnter += delegate { _countdown.ForeColor = CountdownHoverColor; };
            _countdown.MouseLeave += delegate { _countdown.ForeColor = CountdownColor; };
            _countdown.Click += delegate { CollapseNowByUser(); };

            // 按钮：按参考图排在下方居中
            int btnW = 116;
            int btnH = 38;
            int gap = 12;
            int totalW = btnW * 3 + gap * 2;
            int x0 = (PanelWidth - totalW) / 2;
            int y0 = CountdownTop - 12 - btnH;

            _btnMain = MakeButton(x0, y0, btnW, btnH, "打开界面", AccentColor);
            _btnMain.Click += delegate { OnMainButton(); };

            _btnStop = MakeButton(x0 + (btnW + gap), y0, btnW, btnH, "停止服务", BtnColor);
            _btnStop.Click += delegate { OnStopButton(); };

            _btnExit = MakeButton(x0 + (btnW + gap) * 2, y0, btnW, btnH, "关  闭", BtnColor);
            _btnExit.Click += delegate { BeginShutdown(); };

            _panel.Controls.Add(_title);
            _panel.Controls.Add(_divider);
            if (_logo != null) _panel.Controls.Add(_logo);
            _panel.Controls.Add(_dot);
            _panel.Controls.Add(_btnClose);
            _panel.Controls.Add(_status);
            _panel.Controls.Add(_bar);
            _panel.Controls.Add(_detail);
            _panel.Controls.Add(_countdown);
            _panel.Controls.Add(_btnMain);
            _panel.Controls.Add(_btnStop);
            _panel.Controls.Add(_btnExit);
            Controls.Add(_panel);

            _tip = new ToolTip();
            _tip.SetToolTip(_btnMain, "用默认浏览器打开 DSH Web 页面");
            _tip.SetToolTip(_btnStop, "停止服务：结束 dsh web 进程（浏览器里的页面保持不动）");
            _tip.SetToolTip(_btnExit, "关闭本弹窗（同时停止服务）");
            _tip.SetToolTip(_countdown, "点一下立即缩成右下角小圆球（不等倒计时）");

            _uiTimer = new Timer();
            _uiTimer.Interval = 250;
            _uiTimer.Tick += delegate { OnTick(); };

            _stopWatchdog = new Timer();
            _stopWatchdog.Interval = 1000;
            _stopWatchdog.Tick += delegate { OnStopWatchdog(); };

            _collapseTimer = new Timer();
            _collapseTimer.Interval = CollapseDelayMs;
            _collapseTimer.Tick += delegate { _collapseTimer.Stop(); CollapseToBubble(); };

            _countdownTimer = new Timer();
            _countdownTimer.Interval = 1000;
            _countdownTimer.Tick += delegate { UpdateCountdown(); };

            _animTimer = new Timer();
            _animTimer.Interval = 15;
            _animTimer.Tick += delegate { OnAnimTick(); };

            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    if (_bubbleMode) ExpandFromBubble();
                    else BeginShutdown();
                }
            };

            ApplyState();
            PlacePanel();
            ResumeLayout(false);

            // 缩略态的圆体与 logo 直接画在窗体上；拖动/点击由窗体自己接收
            Paint += OnPaintExtra;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            MouseDown += OnBubbleFormMouseDown;
            MouseMove += OnBubbleFormMouseMove;
            MouseUp += OnBubbleFormMouseUp;
        }

        private void OnBubbleFormMouseDown(object sender, MouseEventArgs e)
        {
            if (!_bubbleMode || e.Button != MouseButtons.Left) return;
            if (!InsideBubbleCircle(e.Location)) return;    // 圆外点穿，交给桌面
            BubbleMouseDown(sender, e);
        }

        private void OnBubbleFormMouseMove(object sender, MouseEventArgs e)
        {
            if (!_bubbleMode) return;
            BubbleMouseMove(sender, e);
        }

        private void OnBubbleFormMouseUp(object sender, MouseEventArgs e)
        {
            if (!_bubbleMode) return;
            BubbleMouseUp(sender, e);
        }

        private RoundButton MakeButton(int x, int y, int w, int h, string text, Color fill)
        {            RoundButton b = new RoundButton();
            b.Text = text;
            b.Size = new Size(w, h);
            b.Location = new Point(x, y);
            b.Radius = 9;
            b.FillColor = fill;
            b.HoverColor = BtnHoverColor;
            b.DownColor = BtnDownColor;
            b.DisabledColor = BtnDisabledColor;
            b.TextColor = fill == AccentColor ? Color.White : BtnTextColor;
            b.DisabledTextColor = BtnDisabledTextColor;
            return b;
        }

        private void PlacePanel()
        {
            Rectangle r = DefaultPanelBounds();
            if (!_startPosOverride.IsEmpty) r = new Rectangle(_startPosOverride.X, _startPosOverride.Y, r.Width, r.Height);
            Location = new Point(r.X, r.Y);
            Log("place panel: override=" + _startPosOverride + " default=" + DefaultPanelBounds()
                + " -> " + Bounds + " physical=" + Native.PhysicalRect(this));
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_bubbleMode)      // 缩略态由圆球拖动逻辑接管
            {
                BubbleMouseDown(sender, e);
                return;
            }
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 启动流程
        // ═══════════════════════════════════════════════════════════════════════
        public void BeginLaunch()
        {
            Log("launcher start; forcedPort=" + _forcedPort.ToString(CultureInfo.InvariantCulture));
            EnsureStarted();
        }

        private void EnsureStarted()
        {
            if (_state == LauncherState.Running || _state == LauncherState.Launching) return;

            int port = _forcedPort > 0 ? _forcedPort : DefaultPort;
            if (port > 0)
            {
                bool inUse = IsPortInUse(port);
                bool isDsh = false;
                if (inUse)
                {
                    // 端口已监听：先判断是不是 DSH 在跑，是就直接接管，绝不重复拉起
                    isDsh = ProbeDsh(port);
                    if (!isDsh)
                    {
                        // 可能对方还在启动中，稍等再判一次
                        Application.DoEvents();
                        Thread.Sleep(1000);
                        isDsh = ProbeDsh(port);
                    }
                }
                Log("port " + port + " in use=" + inUse + " isDsh=" + isDsh);
                if (isDsh)
                {
                    Log("port " + port + " already serving DSH; attaching");
                    Adopt(port);
                    _uiTimer.Start();       // 接管路径也要驱动刷新（运行时间/PID）
                    return;
                }

                // 端口被占、却不像 DSH：很可能是上次启动器被强制结束时残留的 dsh 孤儿
                // （它占着端口却不应答，于是"点 DSH 起不来"）。只在此时清掉它并重试一次探测。
                if (inUse && KillLeftoverChild())
                {
                    Application.DoEvents();
                    Thread.Sleep(800);
                    inUse = IsPortInUse(port);
                    isDsh = inUse && ProbeDsh(port);
                    Log("after leftover cleanup: inUse=" + inUse + " isDsh=" + isDsh);
                    if (isDsh)
                    {
                        Adopt(port);
                        _uiTimer.Start();
                        return;
                    }
                }

                if (inUse)
                {
                    Log("port " + port + " occupied by another program; using an OS-assigned port");
                    port = 0;
                }
                else
                {
                    Log("port " + port + " is free; spawning a managed instance");
                }
            }

            StartChild(port);
        }

        /// <summary>接管一个已经在运行的 DSH：不启动新进程，只显示状态。</summary>
        private void Adopt(int port)
        {
            _port = port;
            _pid = 0;
            _startedAt = DateTime.Now;
            _baseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            _openUrl = _baseUrl;
            _progress = 100;
            SetState(LauncherState.Running);
            _detail.Text = "端口 " + port + "  ·  已在运行的实例（非本启动器管理）";
            Log("adopted external dsh web on port " + port);
            if (!_collapseScheduled) ScheduleCollapse();
        }

        /// <summary>记录本启动器拉起的子进程 PID，供下次启动时清理孤儿进程。</summary>
        private static void WriteChildPid(int pid)
        {
            try
            {
                File.WriteAllText(ChildPidPath(), pid.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Log("write child pid failed: " + ex.Message); }
        }

        private static void ClearChildPid()
        {
            try { if (File.Exists(ChildPidPath())) File.Delete(ChildPidPath()); }
            catch { }
        }

        private static string ChildPidPath()
        {
            return Path.Combine(DataRoot(), "dsh-child.pid");
        }

        /// <summary>
        /// 清理上一次运行残留的 dsh 子进程（启动器被强制结束时子进程会变孤儿，
        /// 它会占着端口却不应答，导致下次点 DSH 起不来）。
        /// 只在"端口被占但不像 DSH"时才调用，避免误杀正在正常服务的实例。
        /// </summary>
        /// <returns>是否真的杀掉了一个残留进程。</returns>
        private static bool KillLeftoverChild()
        {
            try
            {
                string path = ChildPidPath();
                if (!File.Exists(path)) return false;
                string text = File.ReadAllText(path).Trim();
                int pid;
                if (!int.TryParse(text, out pid) || pid <= 0) { ClearChildPid(); return false; }

                bool alive = false;
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        alive = !p.HasExited;
                        // 只杀"确实是 node 起的 dsh"，避免 PID 被复用时误杀别的进程
                        string name = p.ProcessName;
                        if (alive && name.IndexOf("node", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            Log("leftover pid " + pid + " is '" + name + "', not node — skipped");
                            ClearChildPid();
                            return false;
                        }
                    }
                }
                catch { alive = false; }

                ClearChildPid();
                if (!alive) return false;

                Log("killing leftover dsh child from a previous run: pid " + pid);
                KillTree(pid);
                Thread.Sleep(800);
                return true;
            }
            catch (Exception ex)
            {
                Log("KillLeftoverChild failed: " + ex.Message);
                return false;
            }
        }

        private void StartChild(int port)
        {
            string node = FindNode();
            string dshBin = FindDshBin();
            if (node == null)
            {
                Fail("未找到 node.exe，请先安装 Node.js");
                return;
            }
            if (dshBin == null || !File.Exists(dshBin))
            {
                Fail("未找到 dsh 程序：" + (dshBin == null ? "(未知路径)" : dshBin));
                return;
            }

            _spawnAttempts++;
            if (_spawnAttempts > 3)
            {
                Fail("DSH 反复启动失败（配置目录写不进去）" + (_pendingErrorLine == null ? "" : "：" + _pendingErrorLine));
                return;
            }

            string args = Quote(dshBin) + " web --no-open";
            if (port > 0) args += " --port " + port.ToString(CultureInfo.InvariantCulture);
            else args += " --port 0";

            string workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir)) workDir = AppDomain.CurrentDomain.BaseDirectory;

            // 启动器日志保留（内含子进程输出），不再清空，便于回溯上一次启动。
            _pendingReadyLine = null;
            _pendingErrorLine = null;
            _errorText = null;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = node;
            psi.Arguments = args;
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;   // 子进程若读 stdin 不会挂住
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            psi.EnvironmentVariables["FORCE_COLOR"] = "0";

            // 配置目录被占用时改用隔离副本，避免与正在运行的实例抢同一个 cordis.yml
            if (_retryWithPrivateHome)
            {
                string privateHome = PreparePrivateHome();
                if (privateHome != null)
                {
                    psi.EnvironmentVariables["DSH_HOME"] = privateHome;
                    _usingPrivateHome = true;
                    Log("using isolated DSH_HOME=" + privateHome);
                }
                else
                {
                    Log("WARNING: could not prepare an isolated DSH_HOME; the retry will use the same config directory");
                }
            }

            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { OnChildLine(e.Data); };
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { OnChildError(e.Data); };
            p.Exited += delegate { /* 由 OnTick 统一处理退出状态 */ };

            try
            {
                if (!p.Start()) throw new InvalidOperationException("进程启动失败");
            }
            catch (Exception ex)
            {
                Fail("无法启动 DSH：" + ex.Message);
                return;
            }

            _child = p;
            _pid = p.Id;
            WriteChildPid(_pid);            // 记下来，下次启动时清理孤儿
            _port = port;
            _startedAt = DateTime.Now;
            _baseUrl = port > 0 ? "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) : null;
            _openUrl = _baseUrl;
            _progress = 3;
            _progressStartedAt = DateTime.Now;
            _lastHeartbeatMs = 0;
            _openPending = false;
            SetState(LauncherState.Launching);
            UpdateDetail();
            Log("spawned pid=" + _pid + " node=" + node + " args=" + args);

            try
            {
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.StandardInput.Close();
            }
            catch (Exception ex)
            {
                Log("BeginOutputReadLine failed: " + ex.Message);
            }

            _uiTimer.Start();
        }

        private void OnChildLine(string line)
        {
            if (line == null) return;
            AppendLog("out", line);
            if (line.IndexOf(ReadyMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _pendingReadyLine = line;
                return;
            }
            string t = line.Trim();
            if (t.Length == 0) return;
            if (IsErrorLike(t)) _pendingErrorLine = t;
        }

        private void OnChildError(string line)
        {
            if (line == null) return;
            AppendLog("err", line);
            string t = line.Trim();
            if (t.Length == 0) return;
            if (t.StartsWith("at ", StringComparison.Ordinal)) return;          // 堆栈帧
            if (t.StartsWith("node:", StringComparison.Ordinal)) return;        // 内部路径
            if (t.StartsWith("Node.js v", StringComparison.Ordinal)) return;    // 版本横幅
            if (t.StartsWith("code:", StringComparison.Ordinal)) return;
            if (t.StartsWith("errno:", StringComparison.Ordinal)) return;
            if (t.StartsWith("syscall:", StringComparison.Ordinal)) return;
            if (t.StartsWith("path:", StringComparison.Ordinal)) return;
            if (t == "^" || t == "}" || t == "{") return;
            if (t.IndexOf("(Use `node", StringComparison.Ordinal) >= 0) return;
            if (t.StartsWith("Error:", StringComparison.Ordinal)
             || t.StartsWith("TypeError:", StringComparison.Ordinal)
             || t.StartsWith("ReferenceError:", StringComparison.Ordinal))
            {
                _pendingErrorLine = t;   // 明确的一行错误，优先保留
                return;
            }
            if (IsErrorLike(t) && _pendingErrorLine == null) _pendingErrorLine = t;
        }

        private static bool IsErrorLike(string text)
        {
            return text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("错误", StringComparison.Ordinal) >= 0
                || text.IndexOf("EADDRINUSE", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("EPERM", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("EACCES", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnTick()
        {
            // 注意：绝不能在 Tick 回调里 Stop() 之后再 Start()——.NET Framework 的
            // WinForms Timer 不会重新生效，界面就会永远停止刷新（曾导致运行时间不更新）。
            _inTick = true;
            try
            {
                TickCore();
            }
            finally
            {
                _inTick = false;
            }
        }

        /// <summary>
        /// 全屏应用（游戏）在前台时把启动器藏起来：既不会压在画面上，也点不到。
        /// 退出全屏后自动恢复。缩略圆球和展开的面板都适用。
        /// </summary>
        private void GuardAgainstFullscreen()
        {
            if (_closed) return;
            if (NowMs() - _lastFullscreenCheckMs < 1500) return;   // 1.5 秒查一次就够
            _lastFullscreenCheckMs = NowMs();

            bool fullscreen = Native.ForegroundIsFullscreen();

            if (fullscreen && Visible && !_hiddenByFullscreen)
            {
                _hiddenByFullscreen = true;
                Hide();
                Log("fullscreen app in front; launcher hidden until it exits");
            }
            else if (!fullscreen && _hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                // 恢复时不抢焦点：你此刻可能正在别的窗口里打字
                _suppressActivation = true;
                try { Show(); }
                finally { _suppressActivation = false; }
                Log("fullscreen app gone; launcher shown again (without stealing focus)");
            }
        }

        private void TickCore()
        {
            GuardAgainstFullscreen();

            string ready = _pendingReadyLine;
            if (ready != null && _state == LauncherState.Launching)
            {
                _pendingReadyLine = null;
                MarkRunning(ready);
            }
            else if (_state == LauncherState.Launching && _pendingErrorLine == null
                  && ElapsedMs() > 4000 && HttpResponds(_port))
            {
                // 没看到 URL 行但端口已经在提供 HTTP 服务（例如日志被别的输出吞掉）
                Log("port " + _port + " answers HTTP before the ready line; treating as ready");
                MarkRunning(null);
            }

            // 运行 20 秒后自动收成右下角小圆球（只安排一次，由 MarkRunning 触发）

            if (_state == LauncherState.Launching)
            {
                bool exit;
                try { exit = _child == null || _child.HasExited; }
                catch { exit = true; }

                if (exit)
                {
                    // 先看是不是"配置目录写不进去"：这类 EPERM/EBUSY 常常是瞬时的
                    // （安全软件扫描、两个 DSH 抢同一个 cordis.yml），隔一会儿重试往往就好了。
                    // 策略：原目录重试 → 隔离 DSH_HOME 重试 → 再等更久重试（共 3 次）
                    string err = _pendingErrorLine;
                    bool sameError = err != null && err == _lastFailLine;
                    _lastFailLine = err;

                    if (NeedsPrivateHomeRetry())
                    {
                        if (sameError) _consecutiveFail++;
                        else _consecutiveFail = 1;

                        if (_spawnAttempts <= 3)
                        {
                            int waitMs = _retryWithPrivateHome ? 1800 : 900;
                            if (sameError) waitMs += _consecutiveFail * 800;
                            Log("child failed (" + (_retryWithPrivateHome ? "isolated home" : "normal home")
                                + "), retry #" + _spawnAttempts + " in " + waitMs + "ms; err=" + Shorten(err));
                            Application.DoEvents();
                            Thread.Sleep(waitMs);

                            if (!_retryWithPrivateHome)
                            {
                                Log("config home is locked; retrying with an isolated DSH_HOME");
                                _retryWithPrivateHome = true;
                            }
                            _pendingErrorLine = null;
                            _errorText = null;
                            _usingPrivateHome = false;
                            StartChild(_port);       // 内部会递增 _spawnAttempts，超过 3 次才彻底失败
                        }
                        else
                        {
                            Log("retries exhausted after " + _spawnAttempts + " attempts");
                            Fail("DSH 启动失败（配置目录写入被拒绝，已重试 3 次）"
                                + (_pendingErrorLine == null ? "" : "：" + Shorten(_pendingErrorLine)));
                        }
                    }
                    else
                    {
                        Fail("DSH 进程已退出" + (_pendingErrorLine == null ? "" : "：" + _pendingErrorLine));
                    }
                }
                else
                {
                    TickProgress();
                    // 心跳诊断：区分“子进程还活着但没就绪”和“启动器自己在空转”
                    if (ElapsedMs() - _lastHeartbeatMs > 5000)
                    {
                        _lastHeartbeatMs = ElapsedMs();
                        Log("waiting: pid=" + _pid + " alive=True port=" + _port
                            + " portAlive=" + PortAlive() + " progress=" + _progress
                            + " privateHome=" + _usingPrivateHome + " waitedMs=" + (long)ElapsedMs());
                    }
                    if (ElapsedMs() > ReadyWaitMs)
                    {
                        Fail("启动超时（" + (ReadyWaitMs / 1000) + " 秒未就绪）");
                    }
                }
            }
            else if (_state == LauncherState.Running)
            {
                bool exit;
                try { exit = _child != null && _child.HasExited; }
                catch { exit = true; }
                if (_child != null && exit && _pid != 0)
                {
                    Log("child exited unexpectedly");
                    SetState(LauncherState.Idle);
                    _pid = 0;
                    _detail.Text = "服务已退出，可点击「启动服务」重新拉起";
                }
                else
                {
                    UpdateDetail();
                    // 每 10 分钟一条心跳 + 每 5 秒记一次界面实际内容（便于事后核对显示）
                    if ((DateTime.Now - _lastRunningLogAt).TotalMinutes >= 10)
                    {
                        _lastRunningLogAt = DateTime.Now;
                        Log("running heartbeat: pid=" + _pid + " port=" + _port
                            + " detail=\"" + _detail.Text + "\"");
                    }
                    if ((DateTime.Now - _lastUiDumpAt).TotalSeconds >= 30)
                    {
                        _lastUiDumpAt = DateTime.Now;
                        Log("ui: bar=" + (_bar.Visible ? "show" : "hide")
                            + " detail=" + (_detail.Visible ? "show" : "hide")
                            + " countdown=" + (_countdown.Visible ? "show" : "hide")
                            + " text=\"" + _detail.Text + "\" cd=\"" + _countdown.Text + "\"");
                    }
                }
            }
            else if (_state == LauncherState.Stopping)
            {
                UpdateDetail();
            }

            // 运行中也要继续跑：那行「端口 · PID · 运行时间」需要每秒刷新
            if (_state != LauncherState.Idle && _state != LauncherState.Failed)
            {
                if (!_uiTimer.Enabled) _uiTimer.Start();
            }
            else if (_uiTimer.Enabled && !_inTick)
            {
                _uiTimer.Stop();
            }
        }

        private void OnStopWatchdog()
        {
            _stopWatchdog.Stop();
            if (_state != LauncherState.Stopping) return;
            int pid = _pid;
            Log("stop watchdog: settling pid " + pid);
            KillTree(pid);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { if (_child != null) _child.WaitForExit(3000); }
                catch { }
                try
                {
                    BeginInvoke((MethodInvoker)delegate { FinalizeStop(); });
                }
                catch { }
            });
        }

        private void MarkRunning(string readyLine)
        {
            _progress = 100;

            string url = ExtractUrl(readyLine);
            int urlPort = PortOf(url);
            if (urlPort > 0) _port = urlPort;
            if (url == null && _port > 0)
                url = "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture);
            _openUrl = url;
            if (_port > 0) _baseUrl = "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture);

            bool shouldOpen = _autoOpenNext || _openPending;
            _autoOpenNext = false;
            _openPending = false;

            SetState(LauncherState.Running);
            UpdateDetail();
            Log("ready: port=" + _port + " url=" + (_openUrl == null ? "(unknown)" : _openUrl));

            if (shouldOpen && !_closed) OpenPage();

            if (!_collapseScheduled) ScheduleCollapse();
        }

        private void TickProgress()
        {
            double t = ElapsedMs();
            int target;
            if (t < 2500) target = 8 + (int)(t / 2500.0 * 32);          // 8  -> 40
            else if (t < 8000) target = 40 + (int)((t - 2500) / 5500.0 * 35); // 40 -> 75
            else if (t < 20000) target = 75 + (int)((t - 8000) / 12000.0 * 14); // 75 -> 89
            else target = 90;
            if (target > ProgressCap) target = ProgressCap;

            if (target > _progress) _progress = Math.Min(target, _progress + 3);
            if (_progress < 2) _progress = 2;

            if (_bar.Value != _progress) _bar.Value = _progress;
            SetStatusText(StatusFor(_progress));
        }

        private static string StatusFor(int progress)
        {
            if (progress < 12) return "正在启动 DeepSeek Harness\u2026";
            if (progress < 40) return "正在加载运行配置\u2026";
            if (progress < 72) return "正在装配插件\u2026";
            if (progress < 88) return "正在绑定本地端口\u2026";
            return "正在等待服务就绪\u2026";
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 按钮行为
        // ═══════════════════════════════════════════════════════════════════════
        private void OnMainButton()
        {
            if (_state == LauncherState.Failed || _state == LauncherState.Idle)
            {
                _autoOpenNext = true;
                _spawnAttempts = 0;         // 手动重试=重置计数，允许再次走完整重试链
                _consecutiveFail = 0;
                _lastFailLine = null;
                EnsureStarted();
                return;
            }
            if (_state == LauncherState.Launching)
            {
                _openPending = true;
                return;
            }
            if (_state == LauncherState.Running)
            {
                OpenPage();
            }
        }

        private void OnStopButton()
        {
            if (_state == LauncherState.Running && _pid == 0)
            {
                // 外部实例：本启动器无法停止，直接释放视图
                Log("stop requested for external instance; detaching only");
                SetState(LauncherState.Idle);
                _detail.Text = "该实例不是由本启动器启动，无法停止";
                return;
            }
            if (_state != LauncherState.Running && _state != LauncherState.Launching) return;

            int pid = _pid;
            Log("stop requested; pid=" + pid);
            SetState(LauncherState.Stopping);

            // 只停服务，不动浏览器：页面留在那儿，下次启动后刷新即可继续用。
            KillTree(pid);
            _stopWatchdog.Start();
        }

        private void FinalizeStop()
        {
            _uiTimer.Stop();
            _stopWatchdog.Stop();
            try
            {
                if (_child != null)
                {
                    _child.CancelOutputRead();
                    _child.CancelErrorRead();
                    _child.Dispose();
                }
            }
            catch { }
            _child = null;
            _pid = 0;
            ClearChildPid();
            _port = _forcedPort > 0 ? _forcedPort : DefaultPort;
            _baseUrl = null;
            _openUrl = null;
            _progress = 0;
            _retryWithPrivateHome = false;
            _usingPrivateHome = false;
            _spawnAttempts = 0;
            _consecutiveFail = 0;
            _lastFailLine = null;
            SetState(LauncherState.Idle);
            _detail.Text = "服务已停止";
            Log("service stopped");
        }

        private void OpenPage()
        {
            string url = _openUrl;
            if (url == null && _port > 0) url = "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture);
            if (url == null)
            {
                Log("open page skipped: no url yet");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log("opened " + url);
            }
            catch (Exception ex)
            {
                Log("open failed: " + ex.Message);
            }
        }

        private void BeginShutdown()
        {
            Log("shutdown requested; state=" + _state);
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closed = true;
            _uiTimer.Stop();
            _stopWatchdog.Stop();
            if (_collapseTimer != null) _collapseTimer.Stop();
            if (_animTimer != null) _animTimer.Stop();
            if (_countdownTimer != null) _countdownTimer.Stop();
            try
            {
                if (_child != null && _pid > 0)
                {
                    bool alive = true;
                    try { alive = !_child.HasExited; }
                    catch { alive = false; }
                    if (alive)
                    {
                        Log("closing: stopping child tree pid " + _pid);
                        KillTree(_pid);
                    }
                    ClearChildPid();        // 已按 pid 清理，PID 记录作废
                }
            }
            catch { }
            Log("launcher exit; state=" + _state + " port=" + _port + " pid=" + _pid
                + " progress=" + _progress + " status=\"" + _status.Text + "\" detail=\"" + _detail.Text + "\"");
            base.OnFormClosing(e);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 状态呈现
        // ═══════════════════════════════════════════════════════════════════════
        private void SetState(LauncherState s)
        {
            _state = s;
            ApplyState();
        }

        private void ApplyState()
        {
            // 状态切换会被 Tick 回调间接触发，此时不能停表（停了 Start() 不会重新生效）
            if (!_inTick && _uiTimer.Enabled) _uiTimer.Stop();
            _bar.Visible = true;
            _detail.Visible = true;
            _countdown.Visible = true;
            _status.ForeColor = StatusColor;
            _dot.Color = IdleColor;
            _btnMain.Enabled = true;
            _btnStop.Enabled = true;

            switch (_state)
            {
                case LauncherState.Launching:
                    _status.Text = "正在启动 DeepSeek Harness";
                    _status.ForeColor = StatusColor;
                    _dot.Color = StatusColor;
                    _bar.Value = _progress;
                    _btnMain.Text = "打开界面";
                    _btnMain.Enabled = false;
                    _btnStop.Text = "停止服务";
                    _btnStop.Enabled = true;
                    _detail.Text = "正在拉起 dsh web 进程\u2026";
                    break;

                case LauncherState.Running:
                    _status.Text = "DSH 正在运行中";
                    _status.ForeColor = OkColor;
                    _dot.Color = OkColor;
                    _bar.Value = 100;
                    _bar.Visible = false;       // 就绪后进度条让位给"端口 · PID · 运行时间"
                    _detail.Visible = true;     // 文本由 UpdateDetail() 填充（含真实 PID 与运行时间）
                    _detail.ForeColor = DetailColor;
                    _btnMain.Text = "打开界面";
                    _btnMain.Enabled = true;
                    _btnStop.Text = "停止服务";
                    _btnStop.Enabled = true;
                    break;

                case LauncherState.Stopping:
                    _status.Text = "正在停止 DSH\u2026";
                    _status.ForeColor = ErrorColor;
                    _dot.Color = ErrorColor;
                    _bar.Value = 0;
                    _btnMain.Text = "打开界面";
                    _btnMain.Enabled = false;
                    _btnStop.Text = "正在停止";
                    _btnStop.Enabled = false;
                    _detail.Text = "正在结束进程 " + _pid.ToString(CultureInfo.InvariantCulture) + " \u2026";
                    break;

                case LauncherState.Failed:
                    _status.Text = "DSH 启动失败";
                    _status.ForeColor = ErrorColor;
                    _dot.Color = ErrorColor;
                    _bar.Value = 100;
                    _btnMain.Text = "重  试";
                    _btnMain.Enabled = true;
                    _btnStop.Text = "停止服务";
                    _btnStop.Enabled = false;
                    _detail.ForeColor = ErrorColor;
                    _detail.Text = _errorText == null ? "未知错误（详见日志）" : _errorText;
                    break;

                default: // Idle
                    _status.Text = "DSH 未运行";
                    _status.ForeColor = IdleColor;
                    _dot.Color = IdleColor;
                    _bar.Value = 0;
                    _bar.Visible = false;       // 没在启动就不显示进度条
                    _btnMain.Text = "启动服务";
                    _btnMain.Enabled = true;
                    _btnStop.Text = "停止服务";
                    _btnStop.Enabled = false;
                    _detail.ForeColor = DetailColor;
                    _detail.Text = "准备就绪";
                    break;
            }

            if (_state != LauncherState.Failed) _detail.ForeColor = DetailColor;
        }

        private void SetStatusText(string text)
        {
            if (_state != LauncherState.Launching) return;
            if (_status.Text != text) _status.Text = text;
        }

        private void UpdateDetail()
        {
            if (_state == LauncherState.Running)
            {
                string port = _port > 0 ? _port.ToString(CultureInfo.InvariantCulture) : "—";
                string pid = _pid > 0 ? _pid.ToString(CultureInfo.InvariantCulture) : "—";
                string up = _pid > 0 ? FormatUptime(DateTime.Now - _startedAt) : "—";

                if (!_startedAtKnown && _port > 0)
                {
                    // 接管的外部实例：向系统要真实启动时间，界面上的运行时间才不会是从接管时刻起算的假值。
                    // 查一次要起 netstat，所以限流到 5 秒一次。
                    if ((DateTime.Now - _lastPidProbeAt).TotalSeconds >= 5)
                    {
                        _lastPidProbeAt = DateTime.Now;
                        if (_pid <= 0)
                        {
                            int discovered = FindListeningPid(_port);
                            if (discovered > 0)
                            {
                                _pid = discovered;
                                pid = discovered.ToString(CultureInfo.InvariantCulture);
                            }
                        }
                        if (_pid > 0)
                        {
                            DateTime real = GetProcessStartTime(_pid);
                            if (real != DateTime.MinValue)
                            {
                                _startedAt = real;
                                _startedAtKnown = true;
                                Log("running: pid=" + _pid + " port=" + _port
                                    + " startedAt=" + real.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    up = _startedAtKnown ? FormatUptime(DateTime.Now - _startedAt) : "—";
                }

                _detail.Text = "端口 " + port + "  ·  PID " + pid + "  ·  运行 " + up;
                _detail.ForeColor = DetailColor;
            }
            else if (_state == LauncherState.Stopping)
            {
                _detail.Text = "正在结束进程 " + _pid.ToString(CultureInfo.InvariantCulture) + " \u2026";
            }
            else if (_state == LauncherState.Launching)
            {
                string port = _port > 0 ? _port.ToString(CultureInfo.InvariantCulture) : "自动分配";
                _detail.Text = "目标端口 " + port + "  ·  已等待 " + FormatUptime(DateTime.Now - _startedAt);
                _detail.ForeColor = DetailColor;
            }
            else if (_state == LauncherState.Failed)
            {
                _detail.ForeColor = ErrorColor;
            }
        }

        /// <summary>查出监听指定端口的进程号（接管外部实例时用来显示真实 PID 和运行时间）。</summary>
        private static int FindListeningPid(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netstat.exe");
                psi.Arguments = "-ano -p tcp";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return 0;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);

                    string needle = ":" + port.ToString(CultureInfo.InvariantCulture);
                    foreach (string raw in output.Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0) continue;
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5) continue;
                        string local = parts[1];
                        if (!local.EndsWith(needle, StringComparison.Ordinal)) continue;
                        int pid;
                        if (int.TryParse(parts[parts.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                        {
                            Log("port " + port + " is owned by pid " + pid);
                            return pid;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("FindListeningPid failed: " + ex.Message);
            }
            return 0;
        }

        private static DateTime GetProcessStartTime(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return p.StartTime;
                }
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string FormatUptime(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            if (ts.TotalHours >= 1)
                return ((int)ts.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":"
                     + ts.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
                     + ts.Seconds.ToString("00", CultureInfo.InvariantCulture);
            return ((int)ts.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":"
                 + ts.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void Fail(string message)
        {
            if (!_inTick && _uiTimer.Enabled) _uiTimer.Stop();
            _stopWatchdog.Stop();
            _errorText = message;
            _progress = 100;
            Log("FAILED: " + message);
            SetState(LauncherState.Failed);
        }

        private double ElapsedMs()
        {
            return (DateTime.Now - _progressStartedAt).TotalMilliseconds;
        }

        /// <summary>单调递增的毫秒数（用于节流，不受系统时间调整影响）。</summary>
        private static double NowMs()
        {
            return Environment.TickCount;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 进程 / 端口工具
        // ═══════════════════════════════════════════════════════════════════════
        private static string FindNode()
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(';'))
                {
                    if (dir.Trim().Length == 0) continue;
                    try
                    {
                        string p = Path.Combine(dir.Trim(), "node.exe");
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            string[] guesses = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node.exe"),
                @"C:\Program Files\nodejs\node.exe"
            };
            foreach (string g in guesses)
            {
                try { if (!string.IsNullOrEmpty(g) && File.Exists(g)) return g; }
                catch { }
            }
            return null;
        }

        private static string FindDshBin()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                string p = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(p)) return p;
            }
            string[] guesses = new string[]
            {
                @"C:\Program Files\nodejs\node_modules\@deepseek-ai\dsh\lib\bin.js",
                @"C:\Program Files (x86)\nodejs\node_modules\@deepseek-ai\dsh\lib\bin.js"
            };
            foreach (string g in guesses)
            {
                try { if (File.Exists(g)) return g; }
                catch { }
            }
            return null;
        }

        private static string Quote(string s)
        {
            return "\"" + s + "\"";
        }

        /// <summary>压缩过长的错误文本，避免日志和失败提示被整段堆栈淹没。</summary>
        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= 220) return s;
            return s.Substring(0, 220) + "…";
        }

        private static bool IsPortInUse(int port)
        {
            TcpListener l = null;
            try
            {
                l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
            catch
            {
                return true;
            }
            finally
            {
                if (l != null)
                {
                    try { l.Stop(); }
                    catch { }
                }
            }
        }

        /// <summary>本进程启动的 dsh 是否已经绑定端口（用于 --port 0 时报就绪）。</summary>
        private bool PortAlive()
        {
            if (_port <= 0) return false;
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, _port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(200)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>端口上是否有真正的 HTTP 服务在应答（比 TCP 连通更可靠）。</summary>
        private static bool HttpResponds(int port)
        {
            if (port <= 0) return false;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                req.Timeout = 1200;
                req.ReadWriteTimeout = 1200;
                req.AllowAutoRedirect = false;
                req.Method = "GET";
                req.Proxy = null;
                req.UserAgent = "DSH-Launcher";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return true;
                }
            }
            catch (WebException we)
            {
                // 收到任何 HTTP 状态码（401/302 等）都说明服务已经在跑
                return we.Response != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断给定端口上跑的是不是 DSH。宽容策略：只要能收到任何 HTTP 响应就认为是
        /// （DSH 的浏览器信任围栏会回 401/302），只有“连得上但完全不像 HTTP 服务”
        /// 才判定为别的程序占用。
        /// </summary>
        private static bool ProbeDsh(int port)
        {
            const int timeoutMs = 3000;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = false;
                req.Method = "GET";
                req.Proxy = null;               // 本机探测绝不能走系统代理
                req.UserAgent = "DSH-Launcher";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    Log("probe " + port + " -> HTTP " + (int)resp.StatusCode + " (treated as DSH)");
                    return true;
                }
            }
            catch (WebException we)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    int code = (int)resp.StatusCode;
                    string server = resp.Headers["Server"];
                    resp.Close();
                    Log("probe " + port + " -> HTTP " + code + " server=" + (server == null ? "(none)" : server) + " (treated as DSH)");
                    return true;
                }
                Log("probe " + port + " -> " + we.Status + " " + we.Message + " (not a DSH listener)");
                return false;
            }
            catch (Exception ex)
            {
                Log("probe " + port + " -> " + ex.GetType().Name + " " + ex.Message + " (not a DSH listener)");
                return false;
            }
        }

        /// <summary>
        /// 子进程是否因为配置目录被占用而退出（典型：已经有一个 DSH 实例在跑，
        /// 两个进程抢同一个 $DSH_HOME/profiles/web/cordis.yml）。
        /// </summary>
        private bool NeedsPrivateHomeRetry()
        {
            if (_usingPrivateHome) return false;      // 已经在隔离目录里了，再隔离没有意义
            string e = _pendingErrorLine;
            if (e == null) return false;
            bool permissionProblem = e.IndexOf("EPERM", StringComparison.OrdinalIgnoreCase) >= 0
                                 || e.IndexOf("EACCES", StringComparison.OrdinalIgnoreCase) >= 0
                                 || e.IndexOf("EBUSY", StringComparison.OrdinalIgnoreCase) >= 0
                                 || e.IndexOf("operation not permitted", StringComparison.OrdinalIgnoreCase) >= 0
                                 || e.IndexOf("resource busy", StringComparison.OrdinalIgnoreCase) >= 0
                                 || e.IndexOf("access is denied", StringComparison.OrdinalIgnoreCase) >= 0
                                 || e.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0
                                 || e.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0
                                 || e.IndexOf("另一个程序正在使用", StringComparison.Ordinal) >= 0;
            if (!permissionProblem) return false;
            return e.IndexOf("cordis.yml", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("profiles", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf(".dsh", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("dsh_home", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 为不能共用 $DSH_HOME 的情况准备一份隔离配置目录：复制用户的 profiles 目录，
        /// 保证插件配置一致；凭据等其它文件不复制，避免把密钥再抄一份。
        /// </summary>
        private static string PreparePrivateHome()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string defaultDsh = Path.Combine(home, ".dsh");
                string target = Path.Combine(DataRoot(), "dsh-home");

                Directory.CreateDirectory(target);
                string srcProfiles = Path.Combine(defaultDsh, "profiles");
                string dstProfiles = Path.Combine(target, "profiles");
                if (Directory.Exists(srcProfiles) && !Directory.Exists(dstProfiles))
                {
                    CopyDirectory(srcProfiles, dstProfiles);
                }
                Log("isolated home prepared at " + target
                    + " (profiles copied=" + Directory.Exists(dstProfiles) + ")");
                return target;
            }
            catch (Exception ex)
            {
                Log("PreparePrivateHome failed: " + ex.Message);
                return null;
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); }
                catch (Exception ex) { Log("copy skipped " + file + ": " + ex.Message); }
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(dir);
                if (name == "node_modules") continue;   // 依赖不复制，交给安装锚点解析
                try { CopyDirectory(dir, Path.Combine(target, name)); }
                catch (Exception ex) { Log("copy dir skipped " + dir + ": " + ex.Message); }
            }
        }

        private static void KillTree(int pid)
        {
            if (pid <= 0) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                psi.Arguments = "/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process tk = Process.Start(psi);
                if (tk != null)
                {
                    tk.StandardOutput.ReadToEnd();
                    tk.StandardError.ReadToEnd();
                    tk.WaitForExit(5000);
                }
            }
            catch { }
        }

        private static string ExtractUrl(string line)
        {
            if (line == null) return null;
            int at = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            int end = at;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ')' && line[end] != '（') end++;
            string url = line.Substring(at, end - at).TrimEnd('.', ',', ';', '"', '\'', '）');
            return url.Length > 10 ? url : null;
        }

        private static int PortOf(string url)
        {
            if (url == null) return 0;
            try
            {
                return new Uri(url).Port;
            }
            catch
            {
                return 0;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 日志（启动器不再占用控制台；日志写在 %TEMP%\dsh-launcher.log）
        // ═══════════════════════════════════════════════════════════════════════
        private static readonly object LogLock = new object();
        private static string _resolvedLogPath;

        internal static void Log(string message)
        {
            AppendLog("app", message);
        }

        /// <summary>自检用：把日志写到指定文件。</summary>
        internal static string LogPathOverride;

        /// <summary>
        /// 日志文件位置：优先 %LOCALAPPDATA%\DSH-Launcher\logs\（纯 ASCII 路径，一定可写），
        /// 其次程序目录，最后才是 %TEMP%。受限/重定向环境里早期路径写不进去，
        /// 那样就完全看不到排查线索。
        /// </summary>
        internal static string LogFilePath()
        {
            if (!string.IsNullOrEmpty(LogPathOverride)) return LogPathOverride;
            if (_resolvedLogPath != null) return _resolvedLogPath;

            foreach (string dir in new string[]
            {
                Path.Combine(DataRoot(), "logs"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")
            })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string candidate = Path.Combine(dir, LogFileName);
                    using (FileStream fs = new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                    _resolvedLogPath = candidate;
                    return _resolvedLogPath;
                }
                catch { }
            }

            _resolvedLogPath = Path.Combine(Path.GetTempPath(), LogFileName);
            return _resolvedLogPath;
        }

        /// <summary>数据根目录：%LOCALAPPDATA%\DSH-Launcher（纯 ASCII，避免中文路径问题）。</summary>
        internal static string DataRoot()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local)) return Path.Combine(local, "DSH-Launcher");
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "DSH-Launcher");
        }

        internal static void AppendLog(string channel, string message)
        {
            try
            {
                lock (LogLock)
                {
                    string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                                + " [" + channel + "] " + message + Environment.NewLine;
                    File.AppendAllText(LogFilePath(), line, Encoding.UTF8);
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 绘制辅助
        // ═══════════════════════════════════════════════════════════════════════
        internal static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 自定义控件
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed class RoundedPanel : Panel
    {
        public Color FillColor = Color.Black;
        public Color OutlineColor = Color.Gray;
        public int Radius = 12;
        /// <summary>整块填满（缩略成圆形时用）：不画描边、不裁圆角。</summary>
        public bool SolidFill;
        /// <summary>不画任何描边（描边被圆形裁切会在圆边留下深色细线，所以默认关闭）。</summary>
        public bool DrawOutline;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            if (SolidFill)
            {
                // 铺满整个控件，连一条缝都不留，避免圆形边缘出现深色描边
                using (SolidBrush b = new SolidBrush(FillColor))
                {
                    e.Graphics.FillRectangle(b, ClientRectangle);
                }
                base.OnPaint(e);
                return;
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = LauncherForm.RoundedPath(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(FillColor)) e.Graphics.FillPath(b, path);
                if (DrawOutline)
                {
                    using (Pen p = new Pen(OutlineColor, 1f)) e.Graphics.DrawPath(p, path);
                }
            }
            base.OnPaint(e);
        }
    }

    internal sealed class LogoBox : Control
    {
        private Image _image;

        /// <summary>true：把图裁成正圆绘制（缩略圆球用，这样圆形与窗口圆形完全重合，不会露出背景形成黑边）。</summary>
        public bool CircleClip;

        public Image Image
        {
            get { return _image; }
            set { _image = value; Invalidate(); }
        }

        public LogoBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (_image == null) return;

            if (CircleClip)
            {
                using (GraphicsPath circle = new GraphicsPath())
                {
                    circle.AddEllipse(0, 0, Width - 1, Height - 1);
                    GraphicsState st = e.Graphics.Save();
                    e.Graphics.SetClip(circle);
                    LauncherForm.DrawFitted(e.Graphics, _image, new Rectangle(0, 0, Width, Height));
                    e.Graphics.Restore(st);
                }
                return;
            }

            LauncherForm.DrawFitted(e.Graphics, _image, new Rectangle(0, 0, Width, Height));
        }
    }

    internal sealed class StatusDot : Control    {
        private Color _color = Color.Gray;
        public Color Color
        {
            get { return _color; }
            set { _color = value; Invalidate(); }
        }

        public StatusDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(_color))
            {
                e.Graphics.FillEllipse(b, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    internal sealed class ProgressStrip : Control
    {
        private int _value;
        private static readonly Color TrackColor = Color.FromArgb(44, 48, 58);
        private static readonly Color AccentColor = Color.FromArgb(64, 132, 255);
        private static readonly Color TextColor = Color.FromArgb(246, 249, 255);

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                if (_value == v) return;
                _value = v;
                Invalidate();
            }
        }

        public ProgressStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = LauncherForm.RoundedPath(r, Height / 2))
            {
                using (SolidBrush b = new SolidBrush(TrackColor)) e.Graphics.FillPath(b, path);
                if (_value > 0)
                {
                    int w = (int)Math.Round((Width - 1) * (_value / 100.0));
                    if (w < Height) w = Math.Min(Height, Width - 1);
                    Rectangle fr = new Rectangle(0, 0, w, Height - 1);
                    GraphicsState st = e.Graphics.Save();
                    using (GraphicsPath clip = LauncherForm.RoundedPath(r, Height / 2))
                    {
                        e.Graphics.SetClip(clip);
                        using (SolidBrush b = new SolidBrush(AccentColor)) e.Graphics.FillRectangle(b, fr);
                    }
                    e.Graphics.Restore(st);
                }
            }
            string text = _value.ToString(CultureInfo.InvariantCulture) + "%";
            TextRenderer.DrawText(e.Graphics, text, Font, r, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class RoundButton : Control
    {
        private bool _hover;
        private bool _down;

        public int Radius = 8;
        public Color FillColor = Color.FromArgb(44, 49, 60);
        public Color HoverColor = Color.FromArgb(58, 65, 80);
        public Color DownColor = Color.FromArgb(34, 38, 48);
        public Color DisabledColor = Color.FromArgb(34, 37, 44);
        public Color TextColor = Color.White;
        public Color DisabledTextColor = Color.FromArgb(110, 116, 128);

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill;
            if (!Enabled) fill = DisabledColor;
            else if (_down) fill = DownColor;
            else if (_hover) fill = HoverColor;
            else fill = FillColor;

            using (GraphicsPath path = LauncherForm.RoundedPath(r, Radius))
            using (SolidBrush b = new SolidBrush(fill))
            {
                e.Graphics.FillPath(b, path);
                if (Enabled && (_hover || _down))
                {
                    using (Pen p = new Pen(Color.FromArgb(90, 140, 230), 1f)) e.Graphics.DrawPath(p, path);
                }
            }

            Color tc = Enabled ? TextColor : DisabledTextColor;
            TextRenderer.DrawText(e.Graphics, Text, Font, r, tc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal static class Native
    {
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        public const int LOGPIXELSY = 90;


        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref Point lpPoint);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>窗口在屏幕上的物理像素矩形（用于自检对照截图坐标）。</summary>
        public static string PhysicalRect(Control c)
        {
            try
            {
                RECT r;
                if (GetWindowRect(c.Handle, out r))
                {
                    return "(" + r.Left + "," + r.Top + ")-(" + r.Right + "," + r.Bottom + ")";
                }
            }
            catch { }
            return "(unknown)";
        }

        /// <summary>
        /// 当前前台窗口是不是"全屏应用"（典型：全屏游戏）。是的话返回 true。
        /// 判定：前台窗口不是本进程、可见、且矩形覆盖了整个显示器（工作区之外，
        /// 连任务栏都盖住），才认为全屏——这样被最大化的普通窗口不会被误判。
        /// </summary>
        public static bool ForegroundIsFullscreen()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                if (!IsWindowVisible(hwnd)) return false;

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)Process.GetCurrentProcess().Id) return false;   // 自己在前台，不算

                RECT r;
                if (!GetWindowRect(hwnd, out r)) return false;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return false;

                Rectangle mon = Screen.FromHandle(hwnd).Bounds;   // 该窗口所在显示器的完整矩形
                // 允许 2% 误差（有些游戏会报 2559x1439 之类）
                return w >= mon.Width * 0.98 && h >= mon.Height * 0.98;
            }
            catch
            {
                return false;
            }
        }
    }
}
