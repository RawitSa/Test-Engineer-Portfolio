using System;
using System.Collections.Generic;
using System.Drawing;
using Emgu.CV;
using Emgu.CV.Structure;

namespace camera_show {
    /// <summary>Travels with every preview bitmap (Bitmap.Tag): which part of the camera frame it shows.</summary>
    public sealed class FrameInfo {
        /// <summary>Part of the full camera frame the bitmap shows, in 0..1 units.</summary>
        public RectangleF Viewport;
        /// <summary>Pixel size of the full camera frame. ROI coordinates live in this space.</summary>
        public Size FrameSize;
    }

    /// <summary>
    /// Per head: the part of the picture the operator zoomed into (mouse wheel on the tile).
    /// The camera threads read it and send ONLY that part, cut from the full-resolution frame, so zooming shows real
    /// detail (not an enlarged thumbnail) and sends less data - which also helps over a remote desktop.
    /// </summary>
    public static class TileView {
        public static readonly RectangleF Full = new RectangleF(0, 0, 1, 1);
        private static readonly object L = new object();
        private static readonly Dictionary<int, RectangleF> Map = new Dictionary<int, RectangleF>();

        public static void Set(int head, RectangleF viewport) { lock (L) Map[head] = viewport; }

        public static RectangleF Get(int head) {
            lock (L) { RectangleF v; return Map.TryGetValue(head, out v) ? v : Full; }
        }

        /// <summary>
        /// Independent bitmap (own memory) of the zoomed part of <paramref name="view"/>, at most maxWidth wide,
        /// tagged with a <see cref="FrameInfo"/>. Image.Bitmap is only a view of the image's buffer, so it is always copied.
        /// </summary>
        public static Bitmap Make(int head, Image<Bgr, byte> view, int maxWidth) {
            RectangleF vp = Get(head);
            int W = view.Width, H = view.Height;
            int x = Clamp((int)Math.Round(vp.X * W), 0, W - 1);
            int y = Clamp((int)Math.Round(vp.Y * H), 0, H - 1);
            int w = Clamp((int)Math.Round(vp.Width * W), 1, W - x);
            int h = Clamp((int)Math.Round(vp.Height * H), 1, H - y);
            bool whole = x == 0 && y == 0 && w == W && h == H;

            Image<Bgr, byte> crop = null;
            try {
                Bitmap src;
                if (whole) src = view.Bitmap;
                else { crop = view.Copy(new Rectangle(x, y, w, h)); src = crop.Bitmap; }

                int ow = src.Width, oh = src.Height;
                if (ow > maxWidth) { oh = (int)((long)oh * maxWidth / ow); ow = maxWidth; }
                var copy = new Bitmap(src, ow, Math.Max(1, oh));
                copy.Tag = new FrameInfo {
                    Viewport = new RectangleF((float)x / W, (float)y / H, (float)w / W, (float)h / H),
                    FrameSize = view.Size
                };
                return copy;
            } finally {
                if (crop != null) crop.Dispose();
            }
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
