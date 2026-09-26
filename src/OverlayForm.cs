using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace SystemPulse
{
    internal sealed class OverlayLine
    {
        public string Label;
        public string Value;
        public string Unit;
        public string Secondary;
        public Color ValueColor;
    }

    // Overlay : fenêtre transparente au premier plan, dessinée en pixels avec transparence (aucun navigateur).
    // Verrouillée, elle laisse passer la souris vers le jeu ; en mode « déplacer », on la glisse comme une fenêtre.
    internal sealed class OverlayForm : Form
    {
        public static readonly Color Mint = Color.FromArgb(0x68, 0xE5, 0xBE);
        public static readonly Color Violet = Color.FromArgb(0xAA, 0x8C, 0xFF);
        public static readonly Color Amber = Color.FromArgb(0xF6, 0xC7, 0x6E);
        public static readonly Color Orange = Color.FromArgb(0xFF, 0x9F, 0x73);
        public static readonly Color Blue = Color.FromArgb(0x80, 0xC9, 0xFF);
        public static readonly Color Pink = Color.FromArgb(0xED, 0x91, 0xD0);

        private bool moveMode;
        private Size pixelSize = new Size(241, 200);
        private List<OverlayLine> lastLines = new List<OverlayLine>();
        private string lastTime = "";

        public event EventHandler MoveFinished;

        public bool MoveMode { get { return moveMode; } }
        public Size PixelSize { get { return pixelSize; } }

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "System Pulse Overlay";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void SetMoveMode(bool on)
        {
            moveMode = on;
            if (IsHandleCreated)
            {
                int style = Native.GetExStyle(Handle);
                if (on) style &= ~Native.WS_EX_TRANSPARENT; else style |= Native.WS_EX_TRANSPARENT;
                Native.SetExStyle(Handle, style);
                Render(lastLines, lastTime);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST && moveMode)
            {
                m.Result = (IntPtr)Native.HTCAPTION; // glisser = déplacer la fenêtre
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == Native.WM_EXITSIZEMOVE && MoveFinished != null) MoveFinished(this, EventArgs.Empty);
        }

        private static Font MakeFont(float pixels, float scale)
        {
            try { return new Font("Consolas", pixels * scale, FontStyle.Bold, GraphicsUnit.Pixel); }
            catch (Exception) { return new Font("Courier New", pixels * scale, FontStyle.Bold, GraphicsUnit.Pixel); }
        }

        private static void DrawOutlined(Graphics g, string text, Font font, Color color, float x, float y, float scale)
        {
            using (SolidBrush black = new SolidBrush(Color.FromArgb(255, 0, 0, 0)))
            using (SolidBrush fill = new SolidBrush(color))
            {
                float o = Math.Max(1f, scale);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0) g.DrawString(text, font, black, x + dx * o, y + dy * o, StringFormat.GenericTypographic);
                g.DrawString(text, font, fill, x, y, StringFormat.GenericTypographic);
            }
        }

        private static float TextWidth(Graphics g, string text, Font font)
        {
            return g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        // Dessine les lignes et met à jour la fenêtre (position actuelle conservée).
        public void Render(List<OverlayLine> lines, string timeText)
        {
            lastLines = lines ?? new List<OverlayLine>();
            lastTime = timeText ?? "";
            if (!IsHandleCreated) return;
            float scale = DeviceDpi / 96f;
            float pad = 8 * scale, lineH = 17 * scale, gap = 4 * scale, labelW = 64 * scale, colGap = 7 * scale;

            using (Font labelFont = MakeFont(10, scale))
            using (Font valueFont = MakeFont(14, scale))
            using (Font netFont = MakeFont(12, scale))
            using (Font unitFont = MakeFont(9, scale))
            using (Font timeFont = MakeFont(8, scale))
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float contentW = 225 * scale;
                foreach (OverlayLine l in lastLines)
                {
                    float w = labelW + colGap + TextWidth(pg, l.Value, l.Label == "NET ↓" ? netFont : valueFont);
                    if (!string.IsNullOrEmpty(l.Unit)) w += colGap + TextWidth(pg, l.Unit, unitFont);
                    if (!string.IsNullOrEmpty(l.Secondary)) w += colGap + TextWidth(pg, l.Secondary, unitFont);
                    contentW = Math.Max(contentW, w);
                }
                int extraLines = moveMode ? 1 : 0;
                int n = lastLines.Count + extraLines;
                float contentH = n * lineH + Math.Max(0, n - 1) * gap + gap * 0.5f + timeFont.Height;
                int width = (int)Math.Ceiling(contentW + pad * 2);
                int height = (int)Math.Ceiling(contentH + pad * 2);
                pixelSize = new Size(width, height);

                using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    if (moveMode)
                    {
                        RectangleF box = new RectangleF(pad - 6 * scale, pad - 5 * scale, contentW + 12 * scale, contentH + 10 * scale);
                        using (SolidBrush back = new SolidBrush(Color.FromArgb(150, 8, 13, 21)))
                        using (Pen dash = new Pen(Color.FromArgb(220, 104, 229, 190), Math.Max(1f, scale)))
                        {
                            dash.DashStyle = DashStyle.Dash;
                            g.FillRectangle(back, box.X, box.Y, box.Width, box.Height);
                            g.DrawRectangle(dash, box.X, box.Y, box.Width, box.Height);
                        }
                    }

                    float y = pad;
                    if (moveMode)
                    {
                        DrawOutlined(g, "+ Glissez pour déplacer", unitFont, Mint, pad, y + (lineH - unitFont.Height) / 2f, scale);
                        y += lineH + gap;
                    }
                    foreach (OverlayLine l in lastLines)
                    {
                        Font vf = l.Label == "NET ↓" ? netFont : valueFont;
                        DrawOutlined(g, l.Label, labelFont, Color.FromArgb(245, 255, 255, 255), pad, y + (lineH - labelFont.Height) / 2f, scale);
                        float vx = pad + labelW + colGap;
                        DrawOutlined(g, l.Value, vf, l.ValueColor, vx, y + (lineH - vf.Height) / 2f, scale);
                        float ux = vx + TextWidth(g, l.Value, vf) + colGap;
                        if (!string.IsNullOrEmpty(l.Unit))
                        {
                            DrawOutlined(g, l.Unit, unitFont, Color.FromArgb(230, 255, 255, 255), ux, y + (lineH - unitFont.Height) / 2f, scale);
                            ux += TextWidth(g, l.Unit, unitFont) + colGap;
                        }
                        if (!string.IsNullOrEmpty(l.Secondary))
                            DrawOutlined(g, l.Secondary, unitFont, Color.FromArgb(230, 255, 255, 255), ux, y + (lineH - unitFont.Height) / 2f, scale);
                        y += lineH + gap;
                    }
                    DrawOutlined(g, lastTime, timeFont, Color.FromArgb(210, 255, 255, 255), pad, y - gap * 0.5f, scale);

                    Apply(bmp);
                }
            }
        }

        private void Apply(Bitmap bmp)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBitmap);
                Native.SIZE size = new Native.SIZE(); size.cx = bmp.Width; size.cy = bmp.Height;
                Native.POINT src = new Native.POINT();
                Native.POINT dst = new Native.POINT(); dst.X = Left; dst.Y = Top;
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER; blend.SourceConstantAlpha = 255; blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
                if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // Garde la fenêtre dans la zone utile de l'écran où se trouve son centre.
        public static Point ClampToScreen(int x, int y, Size size)
        {
            Point center = new Point(x + size.Width / 2, y + size.Height / 2);
            Rectangle area = Screen.FromPoint(center).WorkingArea;
            int cx = Math.Min(Math.Max(x, area.Left), Math.Max(area.Left, area.Right - size.Width));
            int cy = Math.Min(Math.Max(y, area.Top), Math.Max(area.Top, area.Bottom - size.Height));
            return new Point(cx, cy);
        }
    }
}
