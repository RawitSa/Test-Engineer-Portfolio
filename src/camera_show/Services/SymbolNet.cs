using System;
using System.Collections.Generic;
using System.IO;

namespace camera_show {
    /// <summary>
    /// Runs the small CNN trained in Model_SST.ipynb (file symbol_ai.bin) in plain C# - no ONNX, no OpenCV dnn, no extra DLL,
    /// so it does not depend on the Emgu / OpenCV version.
    ///
    /// Layers it understands (exactly what the notebook exports; BatchNorm is already folded into the convolutions):
    ///   1 = Conv 3x3, stride 1, zero padding 'same', + ReLU      2 = MaxPool 2x2      3 = Flatten (H,W,C order)
    ///   4 = Dense + ReLU      5 = Dense (linear)      then Softmax.
    /// Data layout is H x W x C, weights [ky][kx][cin][cout] and Dense [in][out] - the same as Keras.
    /// Forward() keeps no state, so it can be called from several threads at once.
    /// </summary>
    public sealed class SymbolNet {
        private sealed class Layer { public int Type, A, B; public float[] W, Bias; }

        private readonly List<Layer> _layers = new List<Layer>();
        public int Img { get; private set; }
        public int Classes { get; private set; }

        /// <summary>Reads symbol_ai.bin. Returns null (and says why) when the file is missing or not understood.</summary>
        public static SymbolNet Load(string path, out string error) {
            error = null;
            try {
                if (!File.Exists(path)) { error = "file not found: " + path; return null; }
                using (var br = new BinaryReader(File.OpenRead(path))) {
                    var magic = br.ReadBytes(4);
                    if (magic.Length != 4 || magic[0] != 'S' || magic[1] != 'A' || magic[2] != 'I' || magic[3] != '1') { error = "not a symbol_ai.bin file"; return null; }
                    var net = new SymbolNet();
                    net.Img = br.ReadInt32();
                    net.Classes = br.ReadInt32();
                    int n = br.ReadInt32();
                    if (net.Img < 8 || net.Img > 512 || net.Classes < 2 || n < 1 || n > 64) { error = "bad header"; return null; }

                    int h = net.Img, w = net.Img, c = 1, flat = -1;
                    for (int i = 0; i < n; i++) {
                        var L = new Layer { Type = br.ReadInt32() };
                        switch (L.Type) {
                            case 1:
                                L.A = br.ReadInt32(); L.B = br.ReadInt32();
                                if (flat >= 0 || L.A != c || L.B < 1 || L.B > 1024) { error = "layer " + i + ": conv input does not fit"; return null; }
                                L.W = ReadFloats(br, 9 * L.A * L.B); L.Bias = ReadFloats(br, L.B);
                                c = L.B;
                                break;
                            case 2:
                                if (flat >= 0) { error = "layer " + i + ": pool after flatten"; return null; }
                                h /= 2; w /= 2;
                                if (h < 1 || w < 1) { error = "layer " + i + ": picture too small"; return null; }
                                break;
                            case 3:
                                flat = h * w * c;
                                break;
                            case 4:
                            case 5:
                                L.A = br.ReadInt32(); L.B = br.ReadInt32();
                                if (flat < 0) { flat = h * w * c; }
                                if (L.A != flat || L.B < 1 || L.B > 100000) { error = "layer " + i + ": dense input " + L.A + " but previous layer gives " + flat; return null; }
                                L.W = ReadFloats(br, L.A * L.B); L.Bias = ReadFloats(br, L.B);
                                flat = L.B;
                                break;
                            default:
                                error = "layer " + i + ": unknown type " + L.Type; return null;
                        }
                        net._layers.Add(L);
                    }
                    if (flat != net.Classes) { error = "last layer gives " + flat + " values, header says " + net.Classes + " classes"; return null; }
                    return net;
                }
            } catch (Exception ex) {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static float[] ReadFloats(BinaryReader br, int count) {
            var a = new float[count];
            byte[] raw = br.ReadBytes(count * 4);
            if (raw.Length != count * 4) throw new EndOfStreamException("file is cut short");
            Buffer.BlockCopy(raw, 0, a, 0, raw.Length);
            return a;
        }

        /// <summary>input = Img*Img standardised values (H x W, one channel). Returns the softmax of all classes.</summary>
        public float[] Forward(float[] input) {
            int h = Img, w = Img, c = 1;
            float[] x = input;
            foreach (Layer L in _layers) {
                switch (L.Type) {
                    case 1: x = Conv3(x, h, w, L.A, L.B, L.W, L.Bias); c = L.B; break;
                    case 2: x = Pool2(x, h, w, c); h /= 2; w /= 2; break;
                    case 3: break;                                  // already H x W x C in one line
                    case 4: x = Dense(x, L.A, L.B, L.W, L.Bias, true); break;
                    case 5: x = Dense(x, L.A, L.B, L.W, L.Bias, false); break;
                }
            }
            return Softmax(x);
        }

        private static float[] Conv3(float[] x, int h, int w, int cin, int cout, float[] wt, float[] bias) {
            int pw = w + 2;
            var p = new float[(h + 2) * pw * cin];                  // zero border = 'same' padding
            for (int y = 0; y < h; y++) Array.Copy(x, y * w * cin, p, ((y + 1) * pw + 1) * cin, w * cin);

            var o = new float[h * w * cout];
            for (int y = 0; y < h; y++) {
                for (int xx = 0; xx < w; xx++) {
                    int oi = (y * w + xx) * cout;
                    for (int co = 0; co < cout; co++) o[oi + co] = bias[co];
                    for (int ky = 0; ky < 3; ky++) {
                        int rowBase = ((y + ky) * pw + xx) * cin;
                        for (int kx = 0; kx < 3; kx++) {
                            int pi = rowBase + kx * cin;
                            int wi = (ky * 3 + kx) * cin * cout;
                            for (int ci = 0; ci < cin; ci++) {
                                float v = p[pi + ci];
                                if (v == 0f) continue;
                                int wj = wi + ci * cout;
                                for (int co = 0; co < cout; co++) o[oi + co] += v * wt[wj + co];
                            }
                        }
                    }
                    for (int co = 0; co < cout; co++) if (o[oi + co] < 0f) o[oi + co] = 0f;      // ReLU
                }
            }
            return o;
        }

        private static float[] Pool2(float[] x, int h, int w, int c) {
            int oh = h / 2, ow = w / 2;
            var o = new float[oh * ow * c];
            for (int y = 0; y < oh; y++)
                for (int xx = 0; xx < ow; xx++)
                    for (int ch = 0; ch < c; ch++) {
                        float a = x[((2 * y) * w + 2 * xx) * c + ch];
                        float b = x[((2 * y) * w + 2 * xx + 1) * c + ch];
                        float d = x[((2 * y + 1) * w + 2 * xx) * c + ch];
                        float e = x[((2 * y + 1) * w + 2 * xx + 1) * c + ch];
                        float m = a > b ? a : b;
                        float n = d > e ? d : e;
                        o[(y * ow + xx) * c + ch] = m > n ? m : n;
                    }
            return o;
        }

        private static float[] Dense(float[] x, int nin, int nout, float[] wt, float[] bias, bool relu) {
            var o = new float[nout];
            Array.Copy(bias, o, nout);
            for (int i = 0; i < nin; i++) {
                float v = x[i];
                if (v == 0f) continue;
                int wi = i * nout;
                for (int j = 0; j < nout; j++) o[j] += v * wt[wi + j];
            }
            if (relu) for (int j = 0; j < nout; j++) if (o[j] < 0f) o[j] = 0f;
            return o;
        }

        private static float[] Softmax(float[] z) {
            float mx = z[0];
            for (int i = 1; i < z.Length; i++) if (z[i] > mx) mx = z[i];
            double sum = 0;
            var e = new double[z.Length];
            for (int i = 0; i < z.Length; i++) { e[i] = Math.Exp(z[i] - mx); sum += e[i]; }
            var p = new float[z.Length];
            for (int i = 0; i < z.Length; i++) p[i] = (float)(e[i] / sum);
            return p;
        }
    }
}
