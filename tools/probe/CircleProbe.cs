// Probe step 2: a real transparent round window that shows the launcher's logo
// (TransparencyKey + anti-aliased circle painted on the form background, logo
// drawn straight onto the form), plus click-to-expand and drag.
// Compile: csc /target:winexe /out:CircleProbe.exe CircleProbe.cs
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

internal sealed class CircleProbe : Form
{
    private const int Diameter = 120;
    private static readonly Color KeyColor = Color.FromArgb(1, 2, 3);
    private static readonly Color FillColor = Color.FromArgb(24, 26, 32);
    private static readonly Color EdgeColor = Color.FromArgb(70, 76, 92);

    private static readonly string LogPath = @"C:\Users\MSI\Desktop\DSH启动器\tools\probe\probe.log";
    private static readonly string LogoPath = @"C:\Users\MSI\AppData\Local\DSH-Launcher\logo.png";

    private static void Log(string m)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + m + Environment.NewLine); }
        catch { }
    }

    private Image _logo;
    private bool _dragging;
    private bool _moved;
    private Point _dragStartCursor;
    private Point _dragStartWindow;
    private int _paints;

    [STAThread]
    private static void Main(string[] args)
    {
        try { File.Delete(LogPath); } catch { }
        Log("probe start");
        Application.EnableVisualStyles();
        Application.Run(new CircleProbe());
        Log("probe exit");
    }

    public CircleProbe()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        ClientSize = new Size(Diameter, Diameter);

        try
        {
            if (File.Exists(LogoPath)) _logo = Image.FromFile(LogoPath);
        }
        catch (Exception ex) { Log("logo load failed: " + ex.Message); }

        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Width / 2 - Diameter / 2, wa.Height / 2 - Diameter / 2);

        Paint += OnPaintProbe;
        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;

        Timer auto = new Timer();
        auto.Interval = 25000;
        auto.Tick += delegate { Log("auto close"); auto.Stop(); Close(); };
        auto.Start();

        Shown += delegate
        {
            ClientSize = new Size(Diameter, Diameter);      // 显示后钉死正方形，否则圆会变成椭圆
            Log("shown; client=" + ClientSize + " bounds=" + Bounds);
        };

        Log("built; logo=" + (_logo == null ? "(none)" : _logo.Width + "x" + _logo.Height)
            + " location=" + Location);
    }

    private void OnPaintProbe(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

        // 圆体：不透明填充。边缘的抗锯齿混合像素会被透明色键映射成半透明。
        using (SolidBrush b = new SolidBrush(FillColor)) e.Graphics.FillEllipse(b, r);
        using (Pen p = new Pen(EdgeColor, 1f)) e.Graphics.DrawEllipse(p, r);

        if (_logo != null)
        {
            // logo 直接画到窗体上（不经子控件），这样它的透明像素与圆外同为"背景色"→ 一起透明
            using (GraphicsPath clip = new GraphicsPath())
            {
                clip.AddEllipse(r);
                GraphicsState st = e.Graphics.Save();
                e.Graphics.SetClip(clip);
                e.Graphics.DrawImage(_logo, r);
                e.Graphics.Restore(st);
            }
        }

        _paints++;
        if (_paints <= 3 || _paints % 20 == 0) Log("paint#" + _paints + " size=" + Width + "x" + Height);
    }

    private bool InsideCircle(Point p)
    {
        double cx = Width / 2.0, cy = Height / 2.0;
        double dx = p.X - cx, dy = p.Y - cy;
        double rad = Math.Min(Width, Height) / 2.0;
        return dx * dx + dy * dy <= rad * rad;
    }

    private void OnDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Log("mouse down at " + e.Location + " insideCircle=" + InsideCircle(e.Location) + " size=" + Width + "x" + Height);
        _dragging = true;
        _moved = false;
        _dragStartCursor = Cursor.Position;
        _dragStartWindow = Location;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        int dx = Cursor.Position.X - _dragStartCursor.X;
        int dy = Cursor.Position.Y - _dragStartCursor.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _moved = true;
        Location = new Point(_dragStartWindow.X + dx, _dragStartWindow.Y + dy);
        Log("dragged to " + Location);
    }

    private void OnUp(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_moved) Log("drag end at " + Location);
        else { Log("CLICK inside=" + InsideCircle(e.Location)); Close(); }
    }
}
