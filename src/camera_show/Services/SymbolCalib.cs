using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace camera_show {
    /// <summary>
    /// Thresholds learned from real good units, one set PER SYMBOL, instead of one guessed number for all symbols.
    /// Learn:  put  sym_learn=1  in config\camera_show_template.txt, run the job on a GOOD unit (move / turn it a little, light as in production);
    ///         for sym_learn_frames good frames the lowest and the average value of S1 S2 C T G of every symbol are recorded, then
    ///         config\camera_show_calib.txt is written (the job returns "Calibrated"). Set sym_learn=0 again.
    /// Use:    with sym_calib=1 (default) every symbol is checked against ITS OWN limits from that file:
    ///         limit = 0.9 x the lowest value seen on the good unit, kept between a floor and a ceiling.
    /// File format (one line per symbol, values are the LIMITS, -1 = no data, use the default):   S3 0.71 0.55 0.64 0.52 0.60
    /// </summary>
    public sealed class SymbolCalib {
        public static string FilePath { get { return AppPaths.ConfigDir + "camera_show_calib.txt"; } }

        private const int V = 5;                                           // S1 S2 C T G
        private static readonly string[] Names = { "S1", "S2", "C", "T", "G" };
        private readonly double[][] _min, _sum;
        private readonly int[] _cnt;
        public int Frames;

        public SymbolCalib(int symbols) {
            _min = new double[symbols][]; _sum = new double[symbols][]; _cnt = new int[symbols];
            for (int i = 0; i < symbols; i++) {
                _min[i] = new double[V]; _sum[i] = new double[V];
                for (int j = 0; j < V; j++) _min[i][j] = double.MaxValue;
            }
        }

        /// <summary>One more good frame: record the scores of every symbol that was found.</summary>
        public void Add(SymbolResult[] results) {
            Frames++;
            for (int i = 0; i < results.Length && i < _cnt.Length; i++) {
                SymbolResult r = results[i];
                if (r == null || r.S1 < 0) continue;
                double[] v = { r.S1, r.S2, r.C, r.T, r.G };
                for (int j = 0; j < V; j++) { if (v[j] < _min[i][j]) _min[i][j] = v[j]; _sum[i][j] += v[j]; }
                _cnt[i]++;
            }
        }

        public bool Save() {
            try {
                var sb = new StringBuilder();
                sb.AppendLine("# learned from " + Frames + " good frames on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                sb.AppendLine("# limit = 0.9 x lowest value seen. columns: S1 S2 C T G (-1 = no data, default is used)");
                for (int i = 0; i < _cnt.Length; i++) {
                    var line = new StringBuilder("S" + (i + 1));
                    var info = new StringBuilder("#   S" + (i + 1) + " lowest/average:");
                    for (int j = 0; j < V; j++) {
                        if (_cnt[i] == 0) { line.Append(" -1"); continue; }
                        double mn = _min[i][j], avg = _sum[i][j] / _cnt[i];
                        double lim = mn > 0 ? mn * 0.9 : -1;
                        if (lim > 0) lim = Math.Max(j == 0 || j == 1 ? 0.35 : 0.30, Math.Min(0.95, lim));
                        line.Append(' ').Append(lim.ToString("F2", CultureInfo.InvariantCulture));
                        info.Append(' ').Append(Names[j]).Append('=').Append(mn.ToString("F2", CultureInfo.InvariantCulture)).Append('/').Append(avg.ToString("F2", CultureInfo.InvariantCulture));
                    }
                    sb.AppendLine(info.ToString());
                    sb.AppendLine(line.ToString());
                }
                File.WriteAllText(FilePath, sb.ToString());
                return true;
            } catch { return false; }
        }

        /// <summary>One SymbolOptions per symbol: the default ones, with the learned limits put in when camera_show_calib.txt exists.</summary>
        public static SymbolOptions[] Apply(SymbolOptions baseOpt, int symbols) {
            var res = new SymbolOptions[symbols];
            for (int i = 0; i < symbols; i++) res[i] = baseOpt.Clone();
            if (!baseOpt.UseCalib || !File.Exists(FilePath)) return res;
            try {
                foreach (string raw in File.ReadAllLines(FilePath)) {
                    string line = raw.Trim();
                    if (line.Length < 2 || line[0] == '#' || line[0] != 'S') continue;
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int n;
                    if (p.Length < 1 + V || !int.TryParse(p[0].Substring(1), out n) || n < 1 || n > symbols) continue;
                    double[] v = new double[V];
                    for (int j = 0; j < V; j++) v[j] = double.Parse(p[1 + j], CultureInfo.InvariantCulture);
                    SymbolOptions o = res[n - 1];
                    if (v[0] > 0) o.ZnccMin = v[0];
                    if (v[1] > 0) o.EdgeMin = v[1];
                    if (v[2] > 0) o.ContrastMin = v[2];
                    if (v[3] > 0) o.TileMin = v[3];
                    if (v[4] > 0) o.SegMin = v[4];
                }
            } catch { }
            return res;
        }
    }
}
