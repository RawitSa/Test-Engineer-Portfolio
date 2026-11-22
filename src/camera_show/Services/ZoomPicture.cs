using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace camera_show {
    /// <summary>
    /// Picture area of one camera tile. Mouse wheel = zoom at the pointer, drag = move (while zoomed), middle click = 100%.
    /// It only holds the wanted view (<see cref="Desired"/>); the form hands it to <see cref="TileView"/> so the camera thread
    /// sends that part in full detail. Until the sharper frame arrives, the bitmap it already has is cropped and enlarged
    /// locally, so the zoom reacts at once.
    /// </summary>
    public sealed class ZoomPicture : Control {
        public const float MaxZoom = 10f;
        private const float Step = 1.25f;

        private Image _image;
        private RectangleF _desired = TileView.Full;
        private bool _panning;
        private Point _panStart;
        private RectangleF _panStartView;

        /// <summary>Raised when the wanted view changed (wheel / drag / reset).</summary>
        public event Action<ZoomPicture, RectangleF> DesiredChanged;
        /// <summary>Return false to stop dragging from moving the picture (e.g. while the operator draws an ROI).</summary>
        public Func<bool> CanPan;

        public ZoomPicture() {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Black;
            TabStop = false;
        }

        public Image Image { get { return _image; } set { _image = value; Invalidate(); } }

        public RectangleF Desired {
            get { return _desired; }
            set { _desired = Fit(value); Invalidate(); }
        }

        public float Zoom { get { return 1f / Math.Max(0.0001f, _desired.Width); } }

        // ── What is on screen ────────────────────────────────────────────────

        /// <summary>
        /// shown = part of the camera frame on screen (0..1); src = the same part in bitmap pixels; dest = where it is drawn.
        /// </summary>
        public bool GetDisplay(out RectangleF shown, out RectangleF src, out RectangleF dest, out FrameInfo info) {
            shown = src = dest = RectangleF.Empty; info = null;
            Image img = _image;
            if (img == null || ClientSize.Width < 2 || ClientSize.Height < 2) return false;

            info = img.Tag as FrameInfo;
            RectangleF used = info != null ? info.Viewport : TileView.Full;

            // the part of the wanted view that this bitmap really contains
            float x0 = Math.Max(_desired.Left, used.Left),   y0 = Math.Max(_desired.Top, used.Top);
            float x1 = Math.Min(_desired.Right, used.Right), y1 = Math.Min(_desired.Bottom, used.Bottom);
            shown = (x1 - x0 < 1e-4f || y1 - y0 < 1e-4f) ? used : RectangleF.FromLTRB(x0, y0, x1, y1);

            src = new RectangleF((shown.X - used.X) / used.Width * img.Width, (shown.Y - used.Y) / used.Height * img.Height,
                                 shown.Width / used.Width * img.Width,        shown.Height / used.Height * img.Height);
            if (src.Width < 1 || src.Height < 1) return false;

            float scale = Math.Min(ClientSize.Width / src.Width, ClientSize.Height / src.Height);
            float dw = src.Width * scale, dh = src.Height * scale;
            dest = new RectangleF((ClientSize.Width - dw) / 2f, (ClientSize.Height - dh) / 2f, dw, dh);
            return true;
        }

        /// <summary>Control pixel -> pixel of the FULL camera frame (what ROI files use).</summary>
        public bool TryMapToFrame(Point p, out PointF framePx) {
            framePx = PointF.Empty;
            RectangleF shown, src, dest; FrameInfo info;
            if (!GetDisplay(out shown, out src, out dest, out info) || info == null) return false;
            float u = Clamp01((p.X - dest.X) / dest.Width), v = Clamp01((p.Y - dest.Y) / dest.Height);
            framePx = new PointF((shown.X + u * shown.Width) * info.FrameSize.Width,
                                 (shown.Y + v * shown.Height) * info.FrameSize.Height);
            return true;
        }

        // ── Painting ─────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(BackColor);
            RectangleF shown, src, dest; FrameInfo info;
            if (GetDisplay(out shown, out src, out dest, out info)) {
                Graphics g = e.Graphics;
                g.InterpolationMode = InterpolationMode.Bilinear;       // cheap: this is also painted over a remote desktop
                g.PixelOffsetMode = PixelOffsetMode.Half;
                try { g.DrawImage(_image, dest, src, GraphicsUnit.Pixel); } catch { }
            }
            base.OnPaint(e);                                           // lets the form draw the ROI rectangle on top
        }

        /// <summary>Keyboard zoom (+ / -) around the centre of what is on screen.</summary>
        public void ZoomBy(bool zoomIn) {
            RectangleF shown, src, dest; FrameInfo info;
            if (!GetDisplay(out shown, out src, out dest, out info)) return;
            float zoom = Math.Max(1f, Math.Min(MaxZoom, Zoom * (zoomIn ? Step : 1f / Step)));
            float s = 1f / zoom;
            float cx = shown.X + shown.Width / 2f, cy = shown.Y + shown.Height / 2f;
            SetDesired(new RectangleF(cx - s / 2f, cy - s / 2f, s, s));
        }

        public void ResetZoom() { SetDesired(TileView.Full); }

        // ── Mouse: wheel zoom, drag, reset ───────────────────────────────────

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }   // the wheel goes to the focused control

        protected override void OnMouseWheel(MouseEventArgs e) {
            base.OnMouseWheel(e);
            RectangleF shown, src, dest; FrameInfo info;
            if (!GetDisplay(out shown, out src, out dest, out info)) return;

            float u = Clamp01((e.X - dest.X) / dest.Width), v = Clamp01((e.Y - dest.Y) / dest.Height);
            float qx = shown.X + u * shown.Width, qy = shown.Y + v * shown.Height;          // the point under the pointer stays put

            float zoom = Zoom * (e.Delta > 0 ? Step : 1f / Step);
            zoom = Math.Max(1f, Math.Min(MaxZoom, zoom));
            float s = 1f / zoom;
            SetDesired(new RectangleF(qx - u * s, qy - v * s, s, s));
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Middle) { SetDesired(TileView.Full); return; }
            if (e.Button == MouseButtons.Left && _desired.Width < 0.999f && (CanPan == null || CanPan())) {
                _panning = true; _panStart = e.Location; _panStartView = _desired;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if (!_panning) return;
            RectangleF shown, src, dest; FrameInfo info;
            if (!GetDisplay(out shown, out src, out dest, out info)) return;
            float dx = (e.X - _panStart.X) / dest.Width * shown.Width;
            float dy = (e.Y - _panStart.Y) / dest.Height * shown.Height;
            SetDesired(new RectangleF(_panStartView.X - dx, _panStartView.Y - dy, _panStartView.Width, _panStartView.Height));
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _panning = false; }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void SetDesired(RectangleF v) {
            _desired = Fit(v);
            Invalidate();
            Action<ZoomPicture, RectangleF> h = DesiredChanged;
            if (h != null) h(this, _desired);
        }

        /// <summary>Keeps a square-in-frame-units view inside the frame.</summary>
        private static RectangleF Fit(RectangleF v) {
            float s = Math.Max(1f / MaxZoom, Math.Min(1f, v.Width));
            float x = Math.Max(0f, Math.Min(1f - s, v.X));
            float y = Math.Max(0f, Math.Min(1f - s, v.Y));
            return new RectangleF(x, y, s, s);
        }

        private static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }
    }
}
