using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace camera_show {
    /// <summary>One saved assignment: this head always uses THIS physical camera.</summary>
    public sealed class SavedCamera {
        public int    Head;
        public int    Port;     // port number when it was saved (only a hint: Windows renumbers ports)
        public string Id;       // CameraDevice.Id - the part that really identifies the camera
        public string Name;
    }

    public enum SavedLookup { NotSaved, Found, NotConnected }

    /// <summary>
    /// Remembers WHICH camera belongs to each head, in config/camera_show_ports.txt  ("head|port|id|name").
    ///
    /// Why: a camera "port" is only its position in Windows' list of cameras. After a restart (or a re-plug) Windows may
    /// list the same cameras in another order, so "head 1 = port 0" suddenly opens another camera and all heads swap.
    /// The registry stores the camera's own identity (USB device path) and looks up its CURRENT port every time a head
    /// opens, so a head always gets the same physical camera. Keep each camera in the same USB socket.
    /// </summary>
    public static class CameraRegistry {
        private static readonly object Sync = new object();
        private static string FilePath { get { return AppPaths.ConfigDir + "camera_show_ports.txt"; } }

        public static Dictionary<int, SavedCamera> Load() {
            var map = new Dictionary<int, SavedCamera>();
            lock (Sync) {
                try {
                    if (!File.Exists(FilePath)) return map;
                    using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs)) {
                        string line;
                        while ((line = sr.ReadLine()) != null) {
                            line = line.Trim();
                            if (line.Length == 0 || line.StartsWith("#")) continue;
                            string[] p = line.Split('|');
                            int head, port;
                            if (p.Length < 3 || !int.TryParse(p[0], out head) || head < 1) continue;
                            int.TryParse(p[1], out port);
                            map[head] = new SavedCamera { Head = head, Port = port, Id = p[2], Name = p.Length > 3 ? p[3] : "" };
                        }
                    }
                } catch { }
            }
            return map;
        }

        public static SavedCamera Get(int head) {
            SavedCamera c; return Load().TryGetValue(head, out c) ? c : null;
        }

        private static void Store(Dictionary<int, SavedCamera> map) {
            var sb = new StringBuilder();
            sb.AppendLine("# camera_show: which physical camera each head uses.  head|port at save time|camera id|name");
            sb.AppendLine("# Delete a line (or the whole file) to forget it; the next open learns the camera again.");
            foreach (SavedCamera c in map.Values.OrderBy(x => x.Head))
                sb.AppendLine(c.Head + "|" + c.Port + "|" + c.Id + "|" + (c.Name ?? "").Replace('|', '/'));
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Copy(tmp, FilePath, true);
            try { File.Delete(tmp); } catch { }
        }

        /// <summary>Which OTHER head has this camera saved (0 = nobody).</summary>
        public static int OwnerOf(string id, int exceptHead) {
            if (string.IsNullOrEmpty(id)) return 0;
            foreach (KeyValuePair<int, SavedCamera> kv in Load())
                if (kv.Key != exceptHead && string.Equals(kv.Value.Id, id, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return 0;
        }

        /// <summary>
        /// Remembers that <paramref name="head"/> uses the camera that is on <paramref name="port"/> right now.
        /// steal = false (automatic learning when a head opens): if that camera is already saved for ANOTHER head nothing is
        /// changed, so a head can never take another head's camera (that was what made the assignment look random).
        /// steal = true (the operator chose it: Save ports / Auto assign ports / picking a port on a tile): the camera moves to
        /// this head and the old head loses its line, which is written to the log.
        /// </summary>
        public static bool BindPort(int head, int port, bool steal) {
            try {
                CameraCatalog.Refresh();
                CameraDevice d = CameraCatalog.ByIndex(port);
                if (d == null || string.IsNullOrEmpty(d.Id) || !CameraCatalog.HasStableIds) return false;
                lock (Sync) {
                    Dictionary<int, SavedCamera> map = Load();
                    // one physical camera can only belong to one head
                    List<int> owners = map.Where(kv => kv.Key != head && string.Equals(kv.Value.Id, d.Id, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
                    if (owners.Count > 0 && !steal) {
                        CameraHub.Log("NOT saved: head " + head + " on port " + port + " (" + d.Name + ") - that camera already belongs to head " + owners[0] +
                                      ". Use Save ports / Auto assign ports to move a camera on purpose.");
                        return false;
                    }
                    foreach (int other in owners) {
                        map.Remove(other);
                        CameraHub.Log("head " + other + " lost its saved camera (" + d.Name + "): it was given to head " + head);
                    }
                    map[head] = new SavedCamera { Head = head, Port = port, Id = d.Id, Name = d.Name };
                    Store(map);
                }
                CameraHub.Log("saved: head " + head + " = port " + port + " (" + d.Name + ")");
                return true;
            } catch (Exception ex) { CameraHub.Log("camera_show_ports.txt not written: " + ex.Message); return false; }
        }

        public static void Forget(int head) {
            try { lock (Sync) { var map = Load(); if (map.Remove(head)) Store(map); } } catch { }
        }

        public static void ForgetAll() {
            try { lock (Sync) { if (File.Exists(FilePath)) File.Delete(FilePath); } } catch { }
        }

        /// <summary>
        /// Current port of the camera saved for this head. NotSaved = nothing saved (or the camera list has no stable ids),
        /// NotConnected = it is saved but that camera is not plugged in now. Call CameraCatalog.Refresh() first.
        /// </summary>
        public static SavedLookup Lookup(int head, out int port, out string message) {
            port = -1; message = null;
            if (!CameraCatalog.HasStableIds) return SavedLookup.NotSaved;
            SavedCamera s = Get(head);
            if (s == null || string.IsNullOrEmpty(s.Id)) return SavedLookup.NotSaved;
            CameraDevice d = CameraCatalog.ById(s.Id);
            if (d == null) {
                message = "Camera of head " + head + " not connected (" + s.Name + ")";
                return SavedLookup.NotConnected;
            }
            port = d.Index;
            return SavedLookup.Found;
        }

        /// <summary>
        /// The operator's choice, saved everywhere the host and the jobs read it: the port (and Set Address = Port) in the
        /// cameraStep CSV of every mode, plus the camera identity in camera_show_ports.txt.
        /// </summary>
        public static void SavePort(int head, int port, string stepName) {
            try {
                IEnumerable<string> targets = stepName != null
                    ? StepConfig.PortWriteTargets(stepName)
                    : StepConfig.ListModes().Where(m => !StepConfig.IsSetPortMode(m)).ToList();
                foreach (string mode in targets) {
                    try {
                        StepConfig.WriteValue(mode, head, HeadConfigKey.Port, port.ToString());
                        StepConfig.WriteValue(mode, head, HeadConfigKey.SetAddress, CameraAddress.Port);
                    } catch { }
                }
            } catch { }
            BindPort(head, port, true);                                  // the operator chose it
        }
    }

    /// <summary>config/camera_show_count.txt: how many camera tiles the window shows. "auto" (or no file) = as many as are connected.</summary>
    public static class CameraCountSetting {
        private static string FilePath { get { return AppPaths.ConfigDir + "camera_show_count.txt"; } }

        /// <summary>0 = auto, otherwise 1..CameraHub.MaxCameras.</summary>
        public static int Read() {
            try {
                string t = File.ReadAllText(FilePath).Trim();
                int n;
                if (int.TryParse(t, out n)) return Math.Max(1, Math.Min(CameraHub.MaxCameras, n));
            } catch { }
            return 0;
        }

        public static void Write(int n) {
            try { File.WriteAllText(FilePath, n <= 0 ? "auto" : Math.Min(CameraHub.MaxCameras, n).ToString()); } catch { }
        }
    }

    /// <summary>
    /// Lowers the capture resolution when many cameras are connected (USB bandwidth) and goes back to the full size when
    /// cameras are missing / unplugged. config/camera_show_resolution.txt (all optional, '#' = comment):
    ///     mode=auto            auto | off
    ///     3=1280x720           from 3 connected cameras on capture at 1280x720
    ///     5=640x480            from 5 connected cameras on capture at 640x480
    ///     1=full               (optional) 1..2 cameras use the size of the CSV (Frame width / height)
    /// The CSV size is the "full" size and never exceeded. Checks still see frames of the CSV size (smaller captures are
    /// scaled back up), so every ROI drawn at the CSV size stays valid.
    /// </summary>
    public static class AutoResolution {
        private static string FilePath { get { return AppPaths.ConfigDir + "camera_show_resolution.txt"; } }
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, Size> Reference = new Dictionary<int, Size>();   // head -> size the checks expect

        private static bool Enabled(out SortedDictionary<int, Size> tiers) {
            tiers = new SortedDictionary<int, Size>();
            bool on = true, any = false;
            try {
                if (File.Exists(FilePath)) {
                    foreach (string raw in File.ReadAllLines(FilePath)) {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq < 1) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant(), val = line.Substring(eq + 1).Trim().ToLowerInvariant();
                        if (key == "mode") { on = val != "off"; continue; }
                        int count; if (!int.TryParse(key, out count) || count < 1) continue;
                        any = true;
                        if (val == "full") { tiers[count] = Size.Empty; continue; }
                        string[] wh = val.Split('x');
                        int w, h;
                        if (wh.Length == 2 && int.TryParse(wh[0], out w) && int.TryParse(wh[1], out h) && w > 0 && h > 0) tiers[count] = new Size(w, h);
                    }
                }
            } catch { }
            if (!any) { tiers[3] = new Size(1280, 720); tiers[5] = new Size(640, 480); }
            return on;
        }

        /// <summary>
        /// Called right after the CSV frame size was applied. Returns the size to capture at, or Size.Empty = leave as is.
        /// <paramref name="full"/> = the size the camera delivers with the CSV values.
        /// </summary>
        public static Size Choose(int head, Size full) {
            lock (Sync) Reference.Remove(head);
            try {
                SortedDictionary<int, Size> tiers;
                if (!Enabled(out tiers) || full.Width <= 0 || full.Height <= 0) return Size.Empty;
                int cameras = CameraCatalog.Count;
                if (cameras <= 0) return Size.Empty;

                Size tier = Size.Empty;
                foreach (var kv in tiers) if (kv.Key <= cameras) tier = kv.Value;       // the highest step that is <= the count
                if (tier.IsEmpty) return Size.Empty;

                if ((long)tier.Width * tier.Height >= (long)full.Width * full.Height) return Size.Empty;      // never above the CSV size
                double aspectFull = (double)full.Width / full.Height, aspectTier = (double)tier.Width / tier.Height;
                if (Math.Abs(aspectTier / aspectFull - 1.0) > 0.2) return Size.Empty;                        // other shape: would distort

                lock (Sync) Reference[head] = full;
                CameraHub.Log("head " + head + ": " + cameras + " camera(s) connected -> capture " + tier.Width + "x" + tier.Height +
                              " (full size " + full.Width + "x" + full.Height + ")");
                return tier;
            } catch { return Size.Empty; }
        }

        /// <summary>Scales a frame back to the size the checks expect. Returns the same Mat when nothing is to be done.</summary>
        public static Mat Normalize(int head, Mat frame) {
            if (frame == null || frame.IsEmpty) return frame;
            Size reference;
            lock (Sync) { if (!Reference.TryGetValue(head, out reference)) return frame; }
            if (frame.Width == reference.Width && frame.Height == reference.Height) return frame;
            try {
                var big = new Mat();
                CvInvoke.Resize(frame, big, reference, 0, 0, Inter.Linear);
                frame.Dispose();
                return big;
            } catch { return frame; }
        }
    }
}
