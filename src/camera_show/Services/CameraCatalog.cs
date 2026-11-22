using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Emgu.CV;

namespace camera_show {
    /// <summary>One camera as Windows lists it. Index = the number OpenCV (DirectShow) uses to open it.</summary>
    public sealed class CameraDevice {
        public int    Index;
        public string Name;
        /// <summary>
        /// Stable identity of the camera: the DirectShow device path (contains the USB vid/pid and the USB port / serial).
        /// It does NOT change when Windows renumbers the cameras after a restart, unlike <see cref="Index"/>.
        /// </summary>
        public string Id;
        public override string ToString() { return "port " + Index + " = " + Name; }
    }

    /// <summary>
    /// Reads the list of connected cameras WITHOUT opening any of them (fast, does not disturb a running camera).
    /// The order is the DirectShow order, which is the same order OpenCV uses for VideoCapture(index, DShow).
    /// If DirectShow can not be enumerated the catalog falls back to probing ports 0..15 (slow, count only).
    /// </summary>
    public static class CameraCatalog {
        private static readonly object Sync = new object();
        private static List<CameraDevice> _devices = new List<CameraDevice>();
        private static bool _byDirectShow;

        /// <summary>True when the list came from DirectShow (so every camera has a stable Id).</summary>
        public static bool HasStableIds { get { lock (Sync) return _byDirectShow; } }

        public static List<CameraDevice> Devices { get { lock (Sync) return new List<CameraDevice>(_devices); } }

        public static int Count { get { lock (Sync) return _devices.Count; } }

        public static CameraDevice ByIndex(int index) {
            lock (Sync) return _devices.FirstOrDefault(d => d.Index == index);
        }

        public static CameraDevice ById(string id) {
            if (string.IsNullOrEmpty(id)) return null;
            lock (Sync) return _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Reads the camera list again. Returns the number of cameras found.</summary>
        public static int Refresh() {
            List<CameraDevice> list = null;
            bool ds = false;
            try { list = EnumerateDirectShow(); ds = list != null; } catch (Exception ex) { CameraHub.Log("camera list (DirectShow) failed: " + ex.Message); }
            if (list == null) list = Probe();

            lock (Sync) {
                string before = string.Join(", ", _devices.Select(d => d.ToString()));
                string after  = string.Join(", ", list.Select(d => d.ToString()));
                _devices = list;
                _byDirectShow = ds;
                if (before != after) CameraHub.Log("cameras connected: " + list.Count + (ds ? "" : " (probed)") + (list.Count > 0 ? "  [" + after + "]" : ""));
            }
            return list.Count;
        }

        // ── DirectShow enumeration (no extra DLL: plain COM interop) ─────────

        private static readonly Guid VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid IidPropertyBag           = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

        [ComImport, Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86")]
        private class SystemDeviceEnum { }

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum {
            [PreserveSig]
            int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker ppEnumMoniker, [In] int dwFlags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag {
            [PreserveSig]
            int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object pVar, IntPtr pErrorLog);
            [PreserveSig]
            int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In, MarshalAs(UnmanagedType.Struct)] ref object pVar);
        }

        private static List<CameraDevice> EnumerateDirectShow() {
            var result = new List<CameraDevice>();
            object comObj = null;
            IEnumMoniker enumMon = null;
            try {
                comObj = new SystemDeviceEnum();
                var devEnum = (ICreateDevEnum)comObj;
                Guid cat = VideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref cat, out enumMon, 0);
                if (hr != 0 || enumMon == null) return result;          // S_FALSE = no camera at all

                var mon = new IMoniker[1];
                var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int index = 0;
                while (enumMon.Next(1, mon, IntPtr.Zero) == 0) {
                    IMoniker m = mon[0];
                    string name = null, path = null;
                    object bagObj = null;
                    try {
                        Guid iid = IidPropertyBag;
                        m.BindToStorage(null, null, ref iid, out bagObj);
                        var bag = (IPropertyBag)bagObj;
                        name = ReadString(bag, "FriendlyName");
                        path = ReadString(bag, "DevicePath");
                    } catch { }
                    finally {
                        if (bagObj != null) { try { Marshal.ReleaseComObject(bagObj); } catch { } }
                        try { Marshal.ReleaseComObject(m); } catch { }
                    }

                    if (string.IsNullOrEmpty(name)) name = "Camera " + index;
                    // Virtual cameras (OBS ...) are not real cameras: not counted, not offered, never used.
                    // They still take a number in Windows' list, so the index keeps counting them.
                    if (IsIgnored(name)) { index++; continue; }
                    int n; nameCount.TryGetValue(name, out n); nameCount[name] = n + 1;
                    // A device without a path (virtual camera): name + "how many of that name came before" is the best identity there is.
                    string id = !string.IsNullOrEmpty(path) ? path : "name:" + name + "#" + n;
                    result.Add(new CameraDevice { Index = index, Name = name, Id = id });
                    index++;
                }
            } finally {
                if (enumMon != null) { try { Marshal.ReleaseComObject(enumMon); } catch { } }
                if (comObj != null)  { try { Marshal.ReleaseComObject(comObj); } catch { } }
            }
            return result;
        }

        /// <summary>
        /// config/camera_show_ignore.txt: one word per line; a camera whose name contains it is ignored (case-insensitive).
        /// No file = "OBS" (OBS Virtual Camera). Put a single line "-" to ignore nothing.
        /// </summary>
        private static bool IsIgnored(string name) {
            var words = new List<string>();
            try {
                string f = AppPaths.ConfigDir + "camera_show_ignore.txt";
                if (System.IO.File.Exists(f))
                    foreach (string raw in System.IO.File.ReadAllLines(f)) {
                        string w = raw.Trim();
                        if (w.Length > 0 && !w.StartsWith("#") && w != "-") words.Add(w);
                    }
                else words.Add("OBS");
            } catch { words.Add("OBS"); }
            foreach (string w in words)
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string ReadString(IPropertyBag bag, string key) {
            object v = null;
            return bag.Read(key, ref v, IntPtr.Zero) == 0 && v != null ? v.ToString() : null;
        }

        // ── Fallback: open ports one by one (only the count is reliable) ─────

        private static List<CameraDevice> Probe() {
            var list = new List<CameraDevice>();
            CameraWorker.OpenLock.Wait();
            try {
                int misses = 0;
                for (int p = 0; p < 16 && misses < 2; p++) {
                    bool ok = false;
                    VideoCapture cap = null;
                    try {
                        cap = new VideoCapture(p, CameraWorker.CaptureApi);
                        ok = cap.Ptr != IntPtr.Zero && cap.Width > 0;
                    } catch { ok = false; }
                    finally { try { if (cap != null) cap.Dispose(); } catch { } }
                    Thread.Sleep(300);
                    if (ok) { misses = 0; list.Add(new CameraDevice { Index = p, Name = "Camera " + p, Id = null }); }
                    else misses++;
                }
            } finally { CameraWorker.OpenLock.Release(); }
            return list;
        }
    }
}
