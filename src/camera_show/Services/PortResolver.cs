using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace camera_show {
    /// <summary>
    /// Decides which camera index (port) a head uses, ONE job at a time, at the moment that job starts.
    /// Safe to call while other heads are already running:
    ///   * ports owned by running heads are never opened or scanned (so a running camera is never disturbed);
    ///   * probing is serialised with every other camera open (CameraWorker.OpenLock);
    ///   * two heads can never own the same port.
    ///
    ///   Set Address = Port   -> the Port column in the CSV is used as-is: this head always opens that port.
    ///   Set Address = Gamma  -> find the camera whose Gamma equals "Head N Gamma"; the configured port is
    ///                           tried first, then ports 0..15. A new port is written back to the CSV.
    ///                           (Gamma = -1 means "not set" and falls back to Port.)
    /// </summary>
    public static class PortResolver {
        private const int MaxProbe = 16;          // camera indexes 0..15
        private const int ReleaseDelayMs = 300;   // let DirectShow release a camera before reopening

        private static readonly object Sync = new object();
        private static readonly Dictionary<int, string> Detail = new Dictionary<int, string>();

        /// <summary>Extra explanation for the last failed lookup of a head (shown on the tile, not written to the result file).</summary>
        public static string TakeDetail(int head) {
            lock (Sync) { string d; if (!Detail.TryGetValue(head, out d)) return null; Detail.Remove(head); return d; }
        }
        private static readonly Dictionary<int, int> Owner = new Dictionary<int, int>();   // port -> head

        /// <summary>Returns the port to use (already claimed for this head) or -1 and sets <paramref name="error"/>.</summary>
        public static int Acquire(JobSpec job, out string error) {
            error = null;
            int head = job.Head;
            string addr; int cfgPort, gamma;
            StepConfig cfg;
            try {
                cfg = StepConfig.Load(StepConfig.PortStep(job.ModeName));      // port source (if set) decides Port / Address / Gamma
                if (!cfg.TryGet(head, HeadConfigKey.SetAddress, out addr)) addr = CameraAddress.Gamma;
                cfgPort = cfg.GetInt(head, HeadConfigKey.Port);
                gamma   = cfg.GetIntOr(head, HeadConfigKey.Gamma, -1);
            } catch (Exception ex) {
                error = "Fail read config camera: " + ex.Message;
                return -1;
            }

            bool usePort = string.Equals(addr, CameraAddress.Port, StringComparison.OrdinalIgnoreCase) || gamma == -1;
            if (usePort) return AcquireByPort(job, head, cfgPort, out error);

            // Ports that other heads have fixed (Set Address = Port) belong to them: never probe or take them here.
            HashSet<int> reserved = ReservedByPortModeHeads(cfg, head);

            int found = -1;
            var seen = new List<string>();
            CameraWorker.OpenLock.Wait();      // nobody else opens/configures a camera while we probe
            try {
                var order = new List<int> { cfgPort };
                for (int p = 0; p < MaxProbe; p++) if (p != cfgPort) order.Add(p);

                int misses = 0;
                foreach (int p in order) {
                    if (OwnedByOther(p, head) || reserved.Contains(p)) continue;   // running camera / another head's fixed port: do not touch
                    int g = ReadGamma(p);
                    if (g < 0) {
                        if (p != cfgPort && ++misses >= 2) break;   // past the last connected camera
                        continue;
                    }
                    misses = 0;
                    seen.Add("port " + p + " = gamma " + g);
                    if (g == gamma) { found = p; break; }
                }
            } finally {
                CameraWorker.OpenLock.Release();
            }

            if (found < 0) {
                error = "Can not find port (gamma " + gamma + ")";      // short text: this is what the result file gets
                string detail = seen.Count == 0 ? "no free camera answered on ports 0-" + (MaxProbe - 1)
                                                : "cameras seen: " + string.Join(", ", seen);
                lock (Sync) Detail[head] = detail;
                CameraHub.Log("head " + head + ": gamma " + gamma + " not found; " + detail);
                return -1;
            }
            if (!Claim(head, found, out error)) return -1;

            if (found != cfgPort) {
                lock (Sync) {   // several heads may discover a new port at the same moment
                    try { StepConfig.WriteValue(StepConfig.PortStep(job.ModeName), head, HeadConfigKey.Port, found.ToString()); } catch { }
                }
            }
            return found;
        }

        /// <summary>
        /// "Port" addressing. The port number in the CSV is only where the camera was in Windows' list when it was saved.
        /// When the camera of this head was saved (camera_show_ports.txt) its CURRENT port is looked up by its identity, so
        /// a restart that makes Windows list the cameras in another order can no longer swap the heads.
        /// A head that was never saved keeps using the CSV port, and the camera found there is remembered for next time.
        /// </summary>
        private static int AcquireByPort(JobSpec job, int head, int cfgPort, out string error) {
            error = null;
            bool exempt = StepConfig.IsSetPortMode(job.ModeName);      // the *_SetPort mode probes ports on purpose
            if (!exempt) {
                CameraCatalog.Refresh();
                int port; string msg;
                switch (CameraRegistry.Lookup(head, out port, out msg)) {
                    case SavedLookup.Found:
                        if (!Claim(head, port, out error)) return -1;
                        if (port != cfgPort) {
                            CameraHub.Log("head " + head + ": its camera is on port " + port + " now (CSV said " + cfgPort + ") - using the camera, not the number");
                            lock (Sync) {
                                try {
                                    foreach (string mode in StepConfig.PortWriteTargets(job.ModeName))
                                        StepConfig.WriteValue(mode, head, HeadConfigKey.Port, port.ToString());
                                } catch { }
                            }
                        }
                        return port;
                    case SavedLookup.NotConnected:
                        error = msg;
                        lock (Sync) Detail[head] = "connected: " + (CameraCatalog.Count == 0 ? "none" : string.Join(", ", CameraCatalog.Devices.Select(d => d.ToString())));
                        CameraHub.Log("head " + head + ": " + msg);
                        return -1;
                }
            }

            if (!exempt && CameraCatalog.HasStableIds) {
                // Nothing saved for this head, so the number in the CSV is all we have. If the camera on that port is already the
                // camera of ANOTHER head, do not open it (and never take it over): that is how heads swapped cameras at random.
                CameraDevice dev = CameraCatalog.ByIndex(cfgPort);
                int owner = dev != null ? CameraRegistry.OwnerOf(dev.Id, head) : 0;
                if (owner > 0) {
                    error = "Head " + head + " has no camera saved and port " + cfgPort + " (" + dev.Name + ") is the camera of head " + owner +
                            " - give head " + head + " its own camera (live view + Save ports, or Auto assign ports)";
                    lock (Sync) Detail[head] = "connected: " + (CameraCatalog.Count == 0 ? "none" : string.Join(", ", CameraCatalog.Devices.Select(d => d.ToString())));
                    CameraHub.Log("head " + head + ": " + error);
                    return -1;
                }
            }

            if (!Claim(head, cfgPort, out error)) return -1;
            if (!exempt) CameraRegistry.BindPort(head, cfgPort, false);   // first time: remember which camera this head has now (never takes another head's camera)
            return cfgPort;
        }

        /// <summary>Saved ports of the other heads that use "Port" addressing (or have no Gamma): those ports are theirs.</summary>
        private static HashSet<int> ReservedByPortModeHeads(StepConfig cfg, int self) {
            var set = new HashSet<int>();
            for (int h = 1; h <= CameraHub.MaxCameras; h++) {
                if (h == self) continue;
                int port = cfg.GetIntOr(h, HeadConfigKey.Port, -1);
                if (port < 0) continue;
                int g = cfg.GetIntOr(h, HeadConfigKey.Gamma, -1);
                string a;
                bool portMode = cfg.TryGet(h, HeadConfigKey.SetAddress, out a)
                    ? string.Equals(a, CameraAddress.Port, StringComparison.OrdinalIgnoreCase) || g == -1
                    : g == -1;
                if (portMode) set.Add(port);
            }
            return set;
        }

        /// <summary>Call when the head's job is finished (success, failure or cancel).</summary>
        public static void Release(int head) {
            lock (Sync) {
                foreach (int port in Owner.Where(kv => kv.Value == head).Select(kv => kv.Key).ToList())
                    Owner.Remove(port);
            }
        }

        /// <summary>
        /// Set by CameraHub. Stops the live setup preview of the given head (its camera and port are freed).
        /// Returns false when that head is not previewing (e.g. it is running a real job).
        /// </summary>
        public static Func<int, bool> PreemptPreview;

        /// <summary>Claims <paramref name="port"/> for a head. A head that is only previewing gives the port up.</summary>
        public static bool ClaimPort(int head, int port, out string error) => Claim(head, port, out error);

        /// <summary>Head that currently owns the port, or 0.</summary>
        public static int OwnerOf(int port) {
            lock (Sync) { int h; return Owner.TryGetValue(port, out h) ? h : 0; }
        }

        private static bool Claim(int head, int port, out string error) {
            for (int attempt = 0; ; attempt++) {
                int other;
                lock (Sync) {
                    if (!Owner.TryGetValue(port, out other) || other == head) {
                        // one head holds exactly one port at a time
                        foreach (int p in Owner.Where(kv => kv.Value == head && kv.Key != port).Select(kv => kv.Key).ToList())
                            Owner.Remove(p);
                        Owner[port] = head;
                        error = null;
                        return true;
                    }
                }
                // Owned by another head. If that head is only showing a setup preview, it yields (outside the lock).
                Func<int, bool> preempt = PreemptPreview;
                if (attempt == 0 && preempt != null && preempt(other)) continue;
                error = "Port " + port + " is already used by head " + other;
                return false;
            }
        }

        private static bool OwnedByOther(int port, int head) {
            lock (Sync) {
                int other;
                return Owner.TryGetValue(port, out other) && other != head;
            }
        }

        /// <summary>Gamma of the camera at <paramref name="port"/>, or -1 when no camera opens there.</summary>
        private static int ReadGamma(int port) {
            int g = -1;
            VideoCapture cap = null;
            try {
                cap = new VideoCapture(port, CameraWorker.CaptureApi);
                if (cap.Ptr != IntPtr.Zero && cap.Width > 0)
                    g = (int)cap.GetCaptureProperty(CapProp.Gamma);
            } catch { g = -1; }
            finally { try { if (cap != null) cap.Dispose(); } catch { } }
            Thread.Sleep(ReleaseDelayMs);
            return g;
        }
    }
}
