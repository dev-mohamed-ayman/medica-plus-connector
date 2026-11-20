using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PharmacySync.UI
{
    public static class Theme
    {
        public static readonly Color Primary = Color.FromArgb(13, 118, 110);      // teal 700
        public static readonly Color PrimaryDark = Color.FromArgb(17, 78, 74);
        public static readonly Color PrimaryHover = Color.FromArgb(15, 135, 126);
        public static readonly Color PrimarySoft = Color.FromArgb(222, 244, 241);
        public static readonly Color Background = Color.FromArgb(243, 246, 248);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(222, 227, 232);
        public static readonly Color Text = Color.FromArgb(31, 41, 55);
        public static readonly Color Muted = Color.FromArgb(107, 114, 128);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color SuccessSoft = Color.FromArgb(220, 252, 231);
        public static readonly Color Warning = Color.FromArgb(217, 119, 6);
        public static readonly Color WarningSoft = Color.FromArgb(254, 243, 199);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color DangerSoft = Color.FromArgb(254, 226, 226);
        public static readonly Color Info = Color.FromArgb(37, 99, 235);
        public static readonly Color InfoSoft = Color.FromArgb(219, 234, 254);

        public const string FontName = "Segoe UI";

        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font(FontName, size, style, GraphicsUnit.Point);
        }

        public static readonly Font Body = Font(10f);
        public static readonly Font BodyBold = Font(10f, FontStyle.Bold);
        public static readonly Font Small = Font(9f);
        public static readonly Font Title = Font(15f, FontStyle.Bold);
        public static readonly Font Subtitle = Font(12f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9.5f);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void ApplyForm(Form f)
        {
            f.AutoScaleDimensions = new SizeF(96F, 96F);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.Font = Body;
            f.BackColor = Background;
            f.ForeColor = Text;
            f.RightToLeft = RightToLeft.Yes;
            f.RightToLeftLayout = false;
            try { f.Icon = AppIcon.Get(); } catch { }
        }

        public static Label Label(string text, Font font = null, Color? color = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = font ?? Body,
                ForeColor = color ?? Text,
                RightToLeft = RightToLeft.Yes,
                BackColor = Color.Transparent,
                Margin = new Padding(3, 6, 3, 3)
            };
        }

        public static TextBox TextBox(bool ltr = true)
        {
            return new TextBox
            {
                Font = Body,
                BorderStyle = BorderStyle.FixedSingle,
                RightToLeft = ltr ? RightToLeft.No : RightToLeft.Yes,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 4, 3, 4)
            };
        }
    }

    public enum ButtonKind { Primary, Secondary, Danger, Ghost, Success }

    /// <summary>Flat rounded button.</summary>
    public class FlatButton : Control
    {
        private bool _hover, _down;
        public ButtonKind Kind { get; set; }

        public FlatButton(string text, ButtonKind kind = ButtonKind.Secondary)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            Text = text;
            Kind = kind;
            Font = Theme.BodyBold;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            Height = 36;
            Margin = new Padding(4);
            RightToLeft = RightToLeft.Yes;
            AutoSizeToText();
        }

        public void AutoSizeToText()
        {
            var sz = TextRenderer.MeasureText(Text, Font);
            Width = Math.Max(90, sz.Width + 32);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg, fg, border;
            switch (Kind)
            {
                case ButtonKind.Primary: bg = _hover ? Theme.PrimaryHover : Theme.Primary; fg = Color.White; border = bg; break;
                case ButtonKind.Success: bg = _hover ? Color.FromArgb(21, 128, 61) : Theme.Success; fg = Color.White; border = bg; break;
                case ButtonKind.Danger: bg = _hover ? Theme.DangerSoft : Color.White; fg = Theme.Danger; border = Color.FromArgb(252, 165, 165); break;
                case ButtonKind.Ghost: bg = _hover ? Color.FromArgb(40, 255, 255, 255) : Color.Transparent; fg = Color.White; border = Color.FromArgb(120, 255, 255, 255); break;
                default: bg = _hover ? Color.FromArgb(243, 244, 246) : Color.White; fg = Theme.Text; border = Theme.Border; break;
            }
            if (_down) bg = ControlPaint.Dark(bg, 0.05f);
            if (!Enabled) { bg = Color.FromArgb(229, 231, 235); fg = Color.FromArgb(156, 163, 175); border = bg; }

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 7))
            {
                if (bg.A > 0) using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var p = new Pen(Focused && Enabled ? Theme.Info : border)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.RightToLeft);
        }
    }

    /// <summary>White rounded card surface.</summary>
    public class Card : Panel
    {
        public Color BorderColor = Theme.Border;
        public Color AccentColor = Color.Empty; // stripe on the right edge

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            Padding = new Padding(18, 14, 18, 14);
            RightToLeft = RightToLeft.Yes;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 10))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                if (AccentColor != Color.Empty)
                {
                    var old = g.Clip;
                    g.SetClip(path);
                    using (var b = new SolidBrush(AccentColor)) g.FillRectangle(b, Width - 5, 0, 5, Height);
                    g.Clip = old;
                }
                using (var p = new Pen(BorderColor)) g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Small colored "pill" label.</summary>
    public class Pill : Control
    {
        public Color Fill = Theme.PrimarySoft;

        public Pill(string text, Color fill, Color fore)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.Small;
            Text = text;
            Fill = fill;
            ForeColor = fore;
            Margin = new Padding(4, 6, 4, 2);
            RightToLeft = RightToLeft.Yes;
            Resize2();
        }

        public void Set(string text, Color fill, Color fore)
        {
            Text = text; Fill = fill; ForeColor = fore; Resize2(); Invalidate();
        }

        private void Resize2()
        {
            var sz = TextRenderer.MeasureText(Text, Font);
            Size = new Size(sz.Width + 18, sz.Height + 8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2))
            using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.RightToLeft);
        }
    }

    /// <summary>Selectable option tile (used for program / mode selection).</summary>
    public class OptionTile : Control
    {
        private bool _hover;
        private bool _selected;
        public string Title;
        public string Description;
        public string Badge;        // e.g. "قريباً"
        public bool Available = true;
        public object Value;
        public event EventHandler Selected;

        public OptionTile(string title, string description, object value)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Title = title; Description = description; Value = value;
            BackColor = Theme.Background;
            Size = new Size(210, 100);
            Margin = new Padding(6);
            Cursor = Cursors.Hand;
            RightToLeft = RightToLeft.Yes;
        }

        public bool IsSelected
        {
            get { return _selected; }
            set { _selected = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Available) return;
            Focus();
            var h = Selected; if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color bg = !Available ? Color.FromArgb(249, 250, 251) : _selected ? Theme.PrimarySoft : _hover ? Color.FromArgb(248, 250, 252) : Color.White;
            Color border = !Available ? Theme.Border : _selected ? Theme.Primary : _hover ? Color.FromArgb(160, 200, 196) : Theme.Border;
            using (var path = Theme.Rounded(r, 10))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var p = new Pen(border, _selected ? 2f : 1f)) g.DrawPath(p, path);
            }

            // radio circle (top-right because RTL)
            var cr = new Rectangle(Width - 34, 16, 18, 18);
            using (var p = new Pen(_selected ? Theme.Primary : Color.FromArgb(180, 186, 194), 2f)) g.DrawEllipse(p, cr);
            if (_selected) using (var b = new SolidBrush(Theme.Primary)) g.FillEllipse(b, new Rectangle(cr.X + 4, cr.Y + 4, 10, 10));

            var titleColor = Available ? Theme.Text : Color.FromArgb(156, 163, 175);
            var titleRect = new Rectangle(12, 12, Width - 54, 26);
            TextRenderer.DrawText(g, Title, Theme.Subtitle, titleRect, titleColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrEmpty(Description))
            {
                var dr = new Rectangle(12, 42, Width - 28, Height - 50);
                TextRenderer.DrawText(g, Description, Theme.Small, dr, Available ? Theme.Muted : Color.FromArgb(170, 175, 182),
                    TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.RightToLeft | TextFormatFlags.WordBreak);
            }

            if (!string.IsNullOrEmpty(Badge))
            {
                var sz = TextRenderer.MeasureText(Badge, Theme.Small);
                var br = new Rectangle(12, 14, sz.Width + 14, sz.Height + 4);
                using (var path = Theme.Rounded(br, br.Height / 2))
                using (var b = new SolidBrush(Theme.WarningSoft)) g.FillPath(b, path);
                TextRenderer.DrawText(g, Badge, Theme.Small, br, Theme.Warning, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft);
            }
        }
    }

    /// <summary>Gradient header bar.</summary>
    public class HeaderBar : Panel
    {
        public HeaderBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            RightToLeft = RightToLeft.Yes;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            using (var b = new LinearGradientBrush(ClientRectangle, Theme.PrimaryDark, Theme.Primary, LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }
    }

    public static class Ui
    {
        /// <summary>Embeds left-to-right text (paths, dates, URLs) inside Arabic text so it is not reversed.</summary>
        public static string Ltr(string s)
        {
            return ((char)0x202A).ToString() + (s ?? "") + ((char)0x202C).ToString();
        }

        private const MessageBoxOptions Rtl = MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;

        public static void Info(IWin32Window owner, string text, string title = "Pharmacy Sync")
        {
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl);
        }

        public static void Error(IWin32Window owner, string text, string title = "خطأ")
        {
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Rtl);
        }

        public static void Warn(IWin32Window owner, string text, string title = "تنبيه")
        {
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, Rtl);
        }

        public static bool Ask(IWin32Window owner, string text, string title = "تأكيد", bool warning = false)
        {
            return MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, Rtl) == DialogResult.Yes;
        }
    }

    /// <summary>App icon drawn at runtime (pharmacy cross + sync arrows).</summary>
    public static class AppIcon
    {
        private static Icon _icon;

        public static Icon Get()
        {
            if (_icon != null) return _icon;
            try
            {
                _icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch { }
            return _icon;
        }

        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var r = new Rectangle(0, 0, size - 1, size - 1);
                using (var path = Theme.Rounded(r, Math.Max(2, size / 5)))
                using (var b = new LinearGradientBrush(r, Theme.Primary, Theme.PrimaryDark, LinearGradientMode.ForwardDiagonal))
                    g.FillPath(b, path);
                float u = size / 16f;
                using (var w = new SolidBrush(Color.White))
                {
                    g.FillRectangle(w, 6.5f * u, 3.5f * u, 3f * u, 9f * u);
                    g.FillRectangle(w, 3.5f * u, 6.5f * u, 9f * u, 3f * u);
                }
            }
            return bmp;
        }
    }
}
