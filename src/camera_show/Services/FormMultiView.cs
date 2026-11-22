using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace camera_show {
    /// <summary>
    /// Code-only 2x2 view for CameraHub. Tile 1..4 = head 1..4.
    /// Jobs show their live image and PASS/FAIL in the head's tile.
    /// Right-click a tile = the old Form1 setup menu for THAT camera only (set port, frame size,
    /// camera values, address, ...). Everything is read from / saved to the same cameraStep_*.csv the jobs use.
    /// autoCloseWhenIdle = true is used by batch mode: the window closes by itself after the last job.
    /// </summary>
    public sealed class FormMultiView : Form {
        private const int AutoCloseDelayMs = 1500;
        private const int Tiles = CameraHub.MaxCameras;
        private const int PortChoices = 16;                     // camera indexes 0..15

        private readonly CameraHub _hub;
        private readonly bool _autoClose;
        private readonly TableLayoutPanel _grid = new TableLayoutPanel { Dock = DockStyle.Fill };
        private readonly FlowLayoutPanel _toolbar = new FlowLayoutPanel();
        private readonly Label _countLabel = new Label();
        private readonly ComboBox _countBox = new ComboBox();
        private bool _suppressCountEvent;
        private int _visible;                                   // tiles shown (1..Tiles)
        private int _detected;                                  // cameras Windows lists right now
        private const int ToolbarHeight = 36, FooterHeight = 24;
        private readonly ZoomPicture[] _pics   = new ZoomPicture[Tiles];
        private readonly Panel[]      _cells  = new Panel[Tiles];       // the coloured frame around a tile (its padding is the border)
        private readonly Label[]      _labels = new Label[Tiles];
        private readonly string[]     _status = new string[Tiles];       // last status text of each tile
        private int _selectedTile;                                      // the camera "in use": clicked by the operator

        // One colour per head, so a frame is recognised at a glance. Lit when the camera is selected or in use.
        private static readonly Color[] HeadColors = {
            Color.DodgerBlue, Color.Orange, Color.MediumOrchid, Color.Gold,
            Color.LimeGreen, Color.DeepPink, Color.DarkTurquoise, Color.Tomato };
        private static readonly Color IdleBorder = Color.FromArgb(55, 55, 55);
        private const string IdleText = "idle (right-click: setup, double-click: capture 1 frame)";
        private readonly bool[]       _busy   = new bool[Tiles];
        private readonly Dictionary<int, int>     _slotOf    = new Dictionary<int, int>();       // head -> tile
        private readonly Dictionary<int, JobSpec> _jobOf     = new Dictionary<int, JobSpec>();
        private readonly Dictionary<int, string>  _setupStep = new Dictionary<int, string>();    // head -> mode chosen for setup
        private readonly Dictionary<int, int>     _ledStep   = new Dictionary<int, int>();       // head -> LED step being edited (1..100)
        // ROI drawing (per tile)
        private readonly bool[]  _roiDraw  = new bool[Tiles];
        private readonly bool[]  _dragging = new bool[Tiles];
        private readonly Point[] _dragA    = new Point[Tiles];
        private readonly Point[] _dragB    = new Point[Tiles];
        private readonly Label _footer = new Label();
        private System.Windows.Forms.Timer _scan;
        private readonly CheckBox _remote = new CheckBox();
        private bool _anyFail;
        private int  _finished;
        private readonly int _expectedJobs;      // batch mode: number of jobs; hub mode: 0 (never auto-close)

        public FormMultiView(CameraHub hub, bool autoCloseWhenIdle, int expectedJobs) {
            _hub = hub;
            _autoClose = autoCloseWhenIdle;
            _expectedJobs = expectedJobs;

            Text = autoCloseWhenIdle ? "camera_show - parallel check" : "camera_show - waiting for jobs";
            StartPosition = FormStartPosition.CenterScreen;
            _detected = CameraCatalog.Refresh();                 // read the cameras first: the tiles follow this number

            for (int i = 0; i < Tiles; i++) {
                _slotOf[i + 1] = i;                                   // tile i belongs to head i+1

                var cell  = new Panel { Dock = DockStyle.Fill, Margin = new Padding(2), Padding = new Padding(2), BackColor = IdleBorder };
                var label = new Label {
                    Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft,
                    BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.Gray, Text = ""
                };
                var pic = new ZoomPicture { Dock = DockStyle.Fill };

                var menu = new ContextMenuStrip();
                int tile = i;
                menu.Opening += (s, e) => BuildMenu(menu, tile);
                pic.ContextMenuStrip = menu;
                label.ContextMenuStrip = menu;

                // Click a camera = select it (its frame lights up in the head's colour).
                pic.Click   += (s, e) => SelectTile(tile);
                label.Click += (s, e) => SelectTile(tile);
                cell.Click  += (s, e) => SelectTile(tile);
                // Double-click = capture ONE frame (light over a remote desktop).
                pic.DoubleClick += (s, e) => CaptureOne(tile);
                // Mouse wheel = zoom this camera only; drag = move while zoomed; middle click = 100%.
                pic.CanPan = () => !_roiDraw[tile];
                pic.DesiredChanged += (p, vp) => OnViewChanged(tile, vp);

                // Draw ROI: only active after "Draw ROI" was chosen in the menu of this tile
                pic.MouseDown += (s, e) => {
                    if (!_roiDraw[tile] || e.Button != MouseButtons.Left) return;
                    _dragging[tile] = true; _dragA[tile] = _dragB[tile] = e.Location;
                };
                pic.MouseMove += (s, e) => {
                    if (!_dragging[tile]) return;
                    _dragB[tile] = e.Location; pic.Invalidate();
                };
                pic.MouseUp += (s, e) => {
                    if (!_dragging[tile]) return;
                    _dragging[tile] = false; _roiDraw[tile] = false;
                    _dragB[tile] = e.Location; pic.Cursor = Cursors.Default; pic.Invalidate();
                    FinishRoi(tile);
                };
                pic.Paint += (s, e) => {
                    if (_dragging[tile]) e.Graphics.DrawRectangle(Pens.Yellow, DragRect(tile));
                };

                cell.Controls.Add(pic);
                cell.Controls.Add(label);          // added last => docks on top
                _pics[i] = pic; _labels[i] = label; _cells[i] = cell; _status[i] = IdleText;
            }

            var footerPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = Color.FromArgb(25, 25, 25) };
            _footer.Dock = DockStyle.Fill; _footer.TextAlign = ContentAlignment.MiddleLeft;
            _footer.BackColor = footerPanel.BackColor; _footer.ForeColor = Color.Silver;
            _footer.Text = autoCloseWhenIdle ? "starting..." : "watching config - click = select, wheel = zoom, double-click = capture 1 frame, right-click = setup";
            _remote.Text = "Remote (AnyDesk): 1 frame at a time";
            _remote.Dock = DockStyle.Right; _remote.AutoSize = true; _remote.ForeColor = Color.Silver; _remote.BackColor = footerPanel.BackColor;
            _remote.Checked = ReadRemoteSetting();
            _hub.SetRemoteMode(_remote.Checked);
            _remote.CheckedChanged += (s, e) => { _hub.SetRemoteMode(_remote.Checked); SaveRemoteSetting(_remote.Checked); };
            footerPanel.Controls.Add(_footer);
            footerPanel.Controls.Add(_remote);
            Controls.Add(_grid);
            Controls.Add(footerPanel);
            BuildToolbar();
            Controls.Add(_toolbar);                          // added last => docks on top
            ApplyLayout(ComputeCount());

            KeyPreview = true;
            KeyDown += OnKey;

            _hub.JobStarted     += s => OnUi(() => Started(s));
            _hub.JobFinished    += r => OnUi(() => Finished(r));
            _hub.StatusChanged  += (h, t) => OnUi(() => SetStatus(h, t));
            _hub.FrameReady     += (h, b) => { if (!OnUi(() => Frame(h, b))) b.Dispose(); };
            _hub.PreviewStopped += h => OnUi(() => ClearTile(h));

            FormClosing += (s, e) => { _scan.Stop(); _hub.CancelAll(); _hub.StopAllPreviews(); };

            // Cameras plugged in / pulled out while the window is open: the tiles follow (DirectShow list only - never opens a camera).
            _scan = new System.Windows.Forms.Timer { Interval = 4000 };
            _scan.Tick += (s, e) => ScanCameras(false);
            _scan.Start();

            SelectTile(0);                                  // camera 1 is selected at the start
        }

        // ── Number of cameras, toolbar ───────────────────────────────────────

        /// <summary>Tiles to show: the number typed in camera_show_count.txt, else the cameras found. Never hides a tile that is in use.</summary>
        private int ComputeCount() {
            int manual = CameraCountSetting.Read();
            int n = manual > 0 ? manual : _detected;
            for (int i = 0; i < Tiles; i++)
                if (_cells[i] != null && (_busy[i] || _hub.IsPreviewing(HeadOfTile(i)))) n = Math.Max(n, i + 1);
            return Math.Max(1, Math.Min(Tiles, n));
        }

        /// <summary>Shows the first <paramref name="n"/> tiles in a grid that fits: 1 = 1x1, 2 = 2x1, 3-4 = 2x2, 5-6 = 3x2, 7-8 = 4x2.</summary>
        private void ApplyLayout(int n) {
            n = Math.Max(1, Math.Min(Tiles, n));
            bool first = _visible == 0;
            _visible = n;
            int cols = n == 1 ? 1 : n <= 4 ? 2 : n <= 6 ? 3 : 4;
            int rows = (n + cols - 1) / cols;

            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _grid.ColumnStyles.Clear(); _grid.RowStyles.Clear();
            _grid.ColumnCount = cols; _grid.RowCount = rows;
            for (int c = 0; c < cols; c++) _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            for (int r = 0; r < rows; r++) _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            for (int i = 0; i < n; i++) _grid.Controls.Add(_cells[i], i % cols, i / cols);
            _grid.ResumeLayout(true);

            if (WindowState == FormWindowState.Normal) {
                int w = Math.Max(cols * 400, 760);                    // wide enough for the toolbar
                ClientSize = new Size(w, rows * 380 + ToolbarHeight + FooterHeight);
                if (first) CenterToScreen();
            }
            if (_selectedTile >= n) SelectTile(0);
            UpdateCountLabel();
        }

        private void UpdateCountLabel() {
            int manual = CameraCountSetting.Read();
            _countLabel.Text = "Cameras found: " + _detected + "   showing: " + _visible + (manual > 0 ? " (set by config)" : " (auto)");
        }

        private void BuildToolbar() {
            _toolbar.Dock = DockStyle.Top; _toolbar.Height = ToolbarHeight;
            _toolbar.WrapContents = false; _toolbar.AutoScroll = false;
            _toolbar.BackColor = Color.FromArgb(25, 25, 25); _toolbar.Padding = new Padding(4, 3, 4, 3);

            _countLabel.AutoSize = true; _countLabel.ForeColor = Color.Silver; _countLabel.Margin = new Padding(4, 7, 8, 0);
            var showLabel = new Label { Text = "Show:", AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(4, 7, 2, 0) };

            _countBox.DropDownStyle = ComboBoxStyle.DropDownList; _countBox.Width = 60; _countBox.Margin = new Padding(2, 3, 10, 0);
            _countBox.Items.Add("Auto");
            for (int i = 1; i <= Tiles; i++) _countBox.Items.Add(i.ToString());
            _suppressCountEvent = true;
            _countBox.SelectedIndex = CameraCountSetting.Read();     // 0 = Auto, otherwise the number
            _suppressCountEvent = false;
            _countBox.SelectedIndexChanged += (s, e) => {
                if (_suppressCountEvent) return;
                CameraCountSetting.Write(_countBox.SelectedIndex);   // saved: the same count comes back on the next start
                ApplyLayout(ComputeCount());
            };

            _toolbar.Controls.Add(_countLabel);
            _toolbar.Controls.Add(showLabel);
            _toolbar.Controls.Add(_countBox);
            _toolbar.Controls.Add(MakeButton("Default (1 camera)", 130, DefaultOneCamera));
            _toolbar.Controls.Add(MakeButton("Rescan", 64, () => ScanCameras(true)));
            _toolbar.Controls.Add(MakeButton("Auto assign ports", 120, AutoAssignPorts));
            _toolbar.Controls.Add(MakeButton("Save ports", 80, SavePorts));
        }

        private static Button MakeButton(string text, int width, Action click) {
            var b = new Button { Text = text, Width = width, Height = 26, Margin = new Padding(2, 0, 2, 0), FlatStyle = FlatStyle.System };
            b.Click += (s, e) => click();
            return b;
        }

        private void SetCountBox(int index) {
            _suppressCountEvent = true;
            try { _countBox.SelectedIndex = index; } finally { _suppressCountEvent = false; }
        }

        /// <summary>Reads the camera list in the background. A changed number moves the tiles (when the count is on Auto).</summary>
        private void ScanCameras(bool manual) {
            if (!manual && !CameraCatalog.HasStableIds) return;      // the fallback would open every camera - never do that on a timer
            Task.Run(() => {
                int c = CameraCatalog.Refresh();
                OnUi(() => {
                    bool changed = c != _detected;
                    _detected = c;
                    if (changed || manual) { ApplyLayout(ComputeCount()); UpdateFooter(); }
                    if (changed) _footer.Text = "cameras connected: " + c + " (next camera open uses the matching resolution)";
                });
            });
        }

        /// <summary>One camera only: shows 1 tile (saved as the count), stops the other live views and opens camera 1.</summary>
        private void DefaultOneCamera() {
            CameraCountSetting.Write(1);
            SetCountBox(1);
            for (int h = 2; h <= Tiles; h++) {
                int head = h;
                if (_hub.IsPreviewing(head)) Task.Run(() => _hub.StopPreview(head));
            }
            ApplyLayout(ComputeCount());
            SelectTile(0);
            if (SetupStep(1) == null) { string first = StepNames().FirstOrDefault(); if (first != null) _setupStep[1] = first; }
            if (!_hub.IsRunning(1) && !_hub.IsPreviewing(1)) StartLive(1, -1, false, true, true);      // runs the mode, like Start live view
        }

        /// <summary>Head 1 = first camera in Windows' list, head 2 = second ... and saved (port + camera identity).</summary>
        private void AutoAssignPorts() {
            CameraCatalog.Refresh();
            List<CameraDevice> devs = CameraCatalog.Devices;
            if (devs.Count == 0) { MessageBox.Show("No camera found.", "camera_show"); return; }
            int n = Math.Min(devs.Count, Tiles);
            var sb = new StringBuilder();
            for (int h = 1; h <= n; h++) sb.AppendLine("Head " + h + "  =  port " + devs[h - 1].Index + "  (" + devs[h - 1].Name + ")");
            if (MessageBox.Show("Give the cameras to the heads in this order and save it?\n\n" + sb +
                                "\nAfter this, each head keeps ITS camera even when Windows lists the cameras in another order.\n" +
                                "Keep every camera in the same USB socket.", "camera_show", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            for (int h = 1; h <= n; h++) CameraRegistry.SavePort(h, devs[h - 1].Index, SetupStep(h));
            _footer.Text = "saved the ports of " + n + " camera(s). Open the live views again to use them.";
            for (int i = 0; i < Tiles; i++) RenderLabel(i);
        }

        /// <summary>Saves the port every shown head is using now (port + camera identity), for the next start.</summary>
        private void SavePorts() {
            var sb = new StringBuilder();
            int saved = 0;
            for (int h = 1; h <= _visible; h++) {
                int port = _hub.PortOf(h);
                if (port < 0) continue;
                CameraRegistry.SavePort(h, port, SetupStep(h));
                CameraDevice d = CameraCatalog.ByIndex(port);
                sb.AppendLine("Head " + h + "  =  port " + port + (d != null ? "  (" + d.Name + ")" : ""));
                saved++;
            }
            if (saved == 0) { MessageBox.Show("No camera has been opened yet - start a live view first, or use \"Auto assign ports\".", "camera_show"); return; }
            _footer.Text = "saved the ports of " + saved + " camera(s)";
            MessageBox.Show("Saved:\n\n" + sb, "camera_show");
        }

        // ── Tile bookkeeping (UI thread only) ────────────────────────────────

        private void Started(JobSpec job) {
            int slot;
            if (!_slotOf.TryGetValue(job.Head, out slot) || _busy[slot]) {
                slot = Array.FindIndex(_busy, b => !b);
                if (slot < 0) slot = 0;   // cannot happen: hub never runs more than MaxCameras jobs
                foreach (int h in _slotOf.Where(kv => kv.Value == slot).Select(kv => kv.Key).ToList()) _slotOf.Remove(h);
                _slotOf[job.Head] = slot;
            }
            if (slot >= _visible) ApplyLayout(slot + 1);      // a head outside the shown tiles was started: show its tile
            _busy[slot] = true;
            _jobOf[job.Head] = job;
            _labels[slot].ForeColor = Color.White;
            _status[slot] = "starting";
            RenderLabel(slot);
            ApplyStyle(slot);
            UpdateFooter();
        }

        private void SetStatus(int head, string text) {
            int slot;
            if (!_slotOf.TryGetValue(head, out slot)) return;
            _status[slot] = text;
            _labels[slot].ForeColor = text == "PASS" ? Color.LimeGreen
                                    : text.StartsWith("FAIL") || text.StartsWith("Port taken") ? Color.OrangeRed
                                    : text.StartsWith("Live") ? Color.Cyan : Color.White;
            RenderLabel(slot);
            ApplyStyle(slot);
        }

        // ── Colours, selection, titles ───────────────────────────────────────

        private void SelectTile(int tile) {
            if (tile < 0 || tile >= Tiles) return;
            _selectedTile = tile;
            for (int i = 0; i < Tiles; i++) { RenderLabel(i); ApplyStyle(i); }
        }

        /// <summary>
        /// The frame around a tile is its border: selected = thick in the head's colour, in use (job running or setup view
        /// open) = medium in a darker shade of it, otherwise a thin grey line. The title bar carries a tint of the colour.
        /// </summary>
        private void ApplyStyle(int tile) {
            if (tile < 0 || tile >= Tiles || _cells[tile] == null) return;
            int head = HeadOfTile(tile);
            bool inUse = _busy[tile] || _hub.IsPreviewing(head);
            bool selected = tile == _selectedTile;
            Color c = HeadColors[tile % HeadColors.Length];

            Color border; int pad;
            if (selected)    { border = c;            pad = 6; }
            else if (inUse)  { border = Shade(c, 0.65f); pad = 4; }
            else             { border = IdleBorder;   pad = 2; }

            if (_cells[tile].BackColor != border) _cells[tile].BackColor = border;
            if (_cells[tile].Padding.All != pad) _cells[tile].Padding = new Padding(pad);
            _labels[tile].BackColor = (selected || inUse) ? Shade(c, 0.32f) : Color.FromArgb(40, 40, 40);
        }

        private static Color Shade(Color c, float f) {
            return Color.FromArgb((int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        /// <summary>"USING CAMERA | Head 2 | Port 3 | x2.5 | read2d - status" - the port is the one really opened.</summary>
        private void RenderLabel(int tile) {
            int head = HeadOfTile(tile);
            var sb = new StringBuilder();
            if (tile == _selectedTile) sb.Append("USING CAMERA | ");
            sb.Append("Head ").Append(head).Append(" | ").Append(PortText(head, tile));
            float zoom = _pics[tile].Zoom;
            if (zoom > 1.01f) sb.Append(" | x").Append(zoom.ToString("0.0"));
            JobSpec job;
            string name = _jobOf.TryGetValue(head, out job) ? job.StepName : SetupStep(head);
            if (!string.IsNullOrEmpty(name)) sb.Append(" | ").Append(name);
            sb.Append(" - ").Append(_status[tile] ?? IdleText);
            _labels[tile].Text = sb.ToString();
        }

        /// <summary>The port the camera REALLY uses (found by the Port / Gamma rules), not a number assumed from the head.</summary>
        private string PortText(int head, int tile) {
            int p = _hub.PortOf(head);
            bool open = _busy[tile] || _hub.IsPreviewing(head);
            if (p >= 0) return open ? "Port " + p : "last port " + p;
            int saved = CsvInt(StepConfig.PortStep(SetupStep(head)), head, HeadConfigKey.Port);
            return saved >= 0 ? "saved port " + saved : "port ?";
        }

        private void OnViewChanged(int tile, RectangleF viewport) {
            int head = HeadOfTile(tile);
            TileView.Set(head, viewport);                       // the camera thread now cuts this part from the full frame
            LivePreview pv = _hub.GetPreview(head);
            if (pv != null) pv.Rerender();                      // one-frame mode: cut the stored frame again
            RenderLabel(tile);
        }

        // ── One frame at a time (remote desktop) ─────────────────────────────

        private void CaptureOne(int tile) {
            int head = HeadOfTile(tile);
            SelectTile(tile);
            if (_hub.IsRunning(head)) { SetStatus(head, "a job is running on this head - wait for it"); return; }
            LivePreview pv = _hub.GetPreview(head);
            if (pv != null) { SetStatus(head, "Live - capturing 1 frame..."); pv.RequestFrame(); }
            else StartLive(head, -1, false, false);             // opens the camera in one-frame mode; a first picture arrives by itself
        }

        private void OnKey(object sender, KeyEventArgs e) {
            int tile = _selectedTile;
            switch (e.KeyCode) {
                case Keys.Space:                                  CaptureOne(tile); break;
                case Keys.Add: case Keys.Oemplus:                 _pics[tile].ZoomBy(true); break;
                case Keys.Subtract: case Keys.OemMinus:           _pics[tile].ZoomBy(false); break;
                case Keys.D0: case Keys.NumPad0:                  _pics[tile].ResetZoom(); break;
                default:
                    int n = e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9 ? e.KeyCode - Keys.D1
                          : e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad1 : -1;
                    if (n >= 0 && n < Tiles) SelectTile(n);       // keys 1..8 select a camera
                    else return;
                    break;
            }
            e.Handled = true;
        }

        private static string RemoteFile { get { return AppPaths.ConfigDir + "camera_show_remote.txt"; } }
        private static bool ReadRemoteSetting() {
            try { bool v; return bool.TryParse(File.ReadAllText(RemoteFile).Trim(), out v) && v; } catch { return false; }
        }
        private static void SaveRemoteSetting(bool on) { try { File.WriteAllText(RemoteFile, on.ToString()); } catch { } }

        private void Frame(int head, Bitmap bmp) {
            int slot;
            if (!_slotOf.TryGetValue(head, out slot)) { bmp.Dispose(); return; }
            Image old = _pics[slot].Image;
            _pics[slot].Image = bmp;
            if (old != null) old.Dispose();
        }

        /// <summary>Preview ended: blank the picture. The label is only reset when no job owns the tile.</summary>
        private void ClearTile(int head) {
            int slot;
            if (!_slotOf.TryGetValue(head, out slot)) return;
            Image old = _pics[slot].Image;
            _pics[slot].Image = null;
            if (old != null) old.Dispose();
            if (!_busy[slot]) {
                _labels[slot].ForeColor = Color.Gray;
                _status[slot] = IdleText;
                RenderLabel(slot);
            }
            ApplyStyle(slot);
        }

        private void Finished(JobResult r) {
            int slot;
            if (_slotOf.TryGetValue(r.Job.Head, out slot)) { _busy[slot] = false; ApplyStyle(slot); }
            if (!r.Pass) _anyFail = true;
            _finished++;
            UpdateFooter();

            if (_autoClose && _hub.IsIdle && _finished >= _expectedJobs) {
                Environment.ExitCode = _anyFail ? 1 : 0;
                var t = new System.Windows.Forms.Timer { Interval = AutoCloseDelayMs };   // let the operator see the last PASS/FAIL
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); Close(); };
                t.Start();
            }
        }

        private void UpdateFooter() {
            if (_autoClose) _footer.Text = "running " + _hub.RunningCount + " / finished " + _finished + " of " + _expectedJobs;
            else            _footer.Text = "watching config - running " + _hub.RunningCount + ", finished " + _finished + "  (right-click a camera to set it up)";
        }

        // ── Right-click setup menu (the old Form1 menu, one camera at a time) ─

        private int HeadOfTile(int tile) {
            foreach (var kv in _slotOf) if (kv.Value == tile) return kv.Key;
            return tile + 1;
        }

        private void BuildMenu(ContextMenuStrip m, int tile) {
            m.Items.Clear();
            int head = HeadOfTile(tile);
            string step = SetupStep(head);
            bool running = _hub.IsRunning(head);
            bool live = _hub.IsPreviewing(head);
            string pstep = StepConfig.PortStep(step);                  // CSV that decides this head's port / address / gamma
            if (CameraCatalog.HasStableIds) CameraCatalog.Refresh();   // names next to the port numbers

            ToolStripItem title = m.Items.Add("Head " + head + "   mode: " + (step ?? "(choose a mode)"));
            title.Enabled = false;
            if (running) { m.Items.Add("(a job is running on this head)").Enabled = false; return; }
            m.Items.Add(new ToolStripSeparator());

            // Live view. "Start live view" runs this mode's check exactly like a real job (read OCR, decode 2D, LED colours, blink,
            // compare) - but without a time-out and without a result file. "Picture only" is the plain camera picture for setup.
            LivePreview pvLive = _hub.GetPreview(head);
            if (!live) {
                var runItem = new ToolStripMenuItem("Start live view - run the mode" + (step == null ? "" : "  (" + step + ")"));
                runItem.Click += (s, e) => StartLive(head, -1, false, true, true);
                m.Items.Add(runItem);
                var picItem = new ToolStripMenuItem("Start live view - picture only");
                picItem.Click += (s, e) => StartLive(head, -1, false, true, false);
                m.Items.Add(picItem);
            } else {
                var stopItem = new ToolStripMenuItem("Stop live view (release the camera)");
                stopItem.Click += (s, e) => { Task.Run(() => _hub.StopPreview(head)); };
                m.Items.Add(stopItem);
                if (pvLive != null) {
                    var runToggle = new ToolStripMenuItem(pvLive.RunMode ? "Picture only (stop running the mode)" : "Run the mode (like a real job)");
                    runToggle.Click += (s, e) => { LivePreview p = _hub.GetPreview(head); if (p != null) p.SetRunMode(!p.RunMode); };
                    m.Items.Add(runToggle);
                }
            }

            var capItem = new ToolStripMenuItem("Capture 1 frame   (double-click / Space)");
            capItem.Click += (s, e) => CaptureOne(tile);
            m.Items.Add(capItem);
            if (pvLive != null) {
                var mode = new ToolStripMenuItem(pvLive.Streaming ? "Switch to one frame at a time" : "Switch to continuous video");
                mode.Click += (s, e) => { LivePreview p = _hub.GetPreview(head); if (p != null) p.Streaming = !p.Streaming; };
                m.Items.Add(mode);
            }
            var zoomItem = new ToolStripMenuItem("Zoom 100%   (middle click / key 0)") { Enabled = _pics[tile].Zoom > 1.01f };
            zoomItem.Click += (s, e) => _pics[tile].ResetZoom();
            m.Items.Add(zoomItem);

            // Set port: only THIS camera changes port; a head that merely previews that port is reset.
            int curPort = live ? _hub.PreviewPort(head) : CsvInt(pstep, head, HeadConfigKey.Port);
            var portMenu = new ToolStripMenuItem("Set port  (kept: this head always opens it)");
            string portSrc = StepConfig.PortSource(step);
            if (portSrc != null) portMenu.DropDownItems.Add(new ToolStripMenuItem("Ports of all modes come from cameraStep_" + portSrc + ".csv") { Enabled = false });
            string mismatch = StepConfig.PortMismatch(head);
            if (mismatch != null) portMenu.DropDownItems.Add(new ToolStripMenuItem("WARNING - CSVs disagree: " + mismatch) { Enabled = false });
            var savedFor = new Dictionary<int, int>();                  // port -> other head that saved it
            for (int h2 = 1; h2 <= CameraHub.MaxCameras; h2++) {
                if (h2 == head) continue;
                int sp = CsvInt(pstep, h2, HeadConfigKey.Port);
                if (sp >= 0 && !savedFor.ContainsKey(sp)) savedFor[sp] = h2;
            }
            for (int p = 0; p < PortChoices; p++) {
                int owner = PortResolver.OwnerOf(p);
                int other;
                string text = "Port " + p
                    + (owner != 0 && owner != head ? "      (head " + owner + " is on it)"
                     : savedFor.TryGetValue(p, out other) ? "      (saved for head " + other + ")" : "");
                CameraDevice dev = CameraCatalog.ByIndex(p);
                if (dev != null) text += "      [" + dev.Name + "]";
                var item = new ToolStripMenuItem(text) { Checked = p == curPort };
                if (dev == null && CameraCatalog.HasStableIds) item.Enabled = false;     // Windows lists no camera there
                int port = p;
                item.Click += (s, e) => {
                    int holder;
                    if (savedFor.TryGetValue(port, out holder) &&
                        MessageBox.Show("Port " + port + " is saved for head " + holder + ".\n\nUse it for head " + head +
                                        " too? Head " + holder + " will fail until its own port is changed.",
                                        "camera_show", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    StartLive(head, port, true, true, KeepRunMode(head));
                };
                portMenu.DropDownItems.Add(item);
            }
            m.Items.Add(portMenu);

            // Frame
            var frame = new ToolStripMenuItem("Frame");
            AddValue(frame, head, step, "Frame width",  HeadConfigKey.FrameWidth,  "Default = 600",  null, true);
            AddValue(frame, head, step, "Frame height", HeadConfigKey.FrameHeight, "Default = 800",  null, true);
            m.Items.Add(frame);

            // Camera values (applied to the live camera at once and saved to the CSV)
            var cam = new ToolStripMenuItem("Camera values");
            AddValue(cam, head, step, "Zoom",       HeadConfigKey.Zoom,       "-1 = leave unchanged", CapProp.Zoom,       false);
            AddValue(cam, head, step, "Pan",        HeadConfigKey.Pan,        "-1 = leave unchanged", CapProp.Pan,        false);
            AddValue(cam, head, step, "Tilt",       HeadConfigKey.Tilt,       "-1 = leave unchanged", CapProp.Tilt,       false);
            AddValue(cam, head, step, "Contrast",   HeadConfigKey.Contrast,   "-1 = leave unchanged", CapProp.Contrast,   false);
            AddValue(cam, head, step, "Brightness", HeadConfigKey.Brightness, "-1 = leave unchanged", CapProp.Brightness, false);
            AddValue(cam, head, step, "Saturation", HeadConfigKey.Saturation, "-1 = leave unchanged", CapProp.Saturation, false);
            AddValue(cam, head, step, "Sharpness",  HeadConfigKey.Sharpness,  "-1 = leave unchanged", CapProp.Sharpness,  false);
            AddValue(cam, head, step, "Gain",       HeadConfigKey.Gain,       "-1 = leave unchanged", CapProp.Gain,       false);
            cam.DropDownItems.Add(new ToolStripSeparator());
            var driver = new ToolStripMenuItem("Property setting (camera driver window)");
            driver.Click += (s, e) => {
                LivePreview pv = RequireLive(head); if (pv == null || !Password()) return;
                pv.Post(cap => cap.SetCaptureProperty(CapProp.Settings, 0));
            };
            cam.DropDownItems.Add(driver);
            var save = new ToolStripMenuItem("Save config (live camera values -> CSV)");
            save.Click += (s, e) => SaveLiveValues(head, step);
            cam.DropDownItems.Add(save);
            m.Items.Add(cam);

            // ROI: drag a rectangle on the live picture (saved the same way as the old program)
            JobKind? kind = KindOf(head, step);
            var roiMenu = new ToolStripMenuItem("ROI");
            var draw = new ToolStripMenuItem(_roiDraw[tile] ? "Cancel draw ROI" : "Draw ROI (then drag on the picture)") { Enabled = live };
            draw.Click += (s, e) => ToggleDraw(tile, head);
            roiMenu.DropDownItems.Add(draw);
            roiMenu.DropDownItems.Add(new ToolStripMenuItem("ROI in CSV: " + CsvRect(step, head)) { Enabled = false });
            AddCropItem(roiMenu);
            if (kind == JobKind.CheckLed || kind == JobKind.Compare || kind == JobKind.MultiLed) {
                roiMenu.DropDownItems.Add(new ToolStripSeparator());
                int n = LedStep(head);
                var stepNo = new ToolStripMenuItem((kind == JobKind.Compare ? "Compare image" : kind == JobKind.MultiLed ? "LED number" : "LED step") + " being edited: " + n + "  (change...)");
                stepNo.Click += (s, e) => {
                    string input = Microsoft.VisualBasic.Interaction.InputBox(
                        "Keys Number Step in Check Led\r\nInteger = 1 - 100\r\nStep 1 = ROI in the CSV, step 2+ = files in config\\" + step,
                        "Number Check Led", n.ToString(), 500, 300);
                    int v;
                    if (string.IsNullOrEmpty(input)) return;
                    if (!int.TryParse(input.Trim(), out v) || v < 1 || v > 100) { MessageBox.Show("not format"); return; }
                    _ledStep[head] = v;
                };
                var next = new ToolStripMenuItem("Next LED step");
                next.Click += (s, e) => { if (LedStep(head) < 100) _ledStep[head] = LedStep(head) + 1; SetStatus(head, "LED step " + LedStep(head)); };
                roiMenu.DropDownItems.Add(stepNo);
                roiMenu.DropDownItems.Add(next);
                if (kind == JobKind.MultiLed) AddLedSelectItems(roiMenu, head, step);
            }
            m.Items.Add(roiMenu);

            // Colour (LED / Blink LED): range, mask, hold time, and a live pixel count to tune them
            if (kind == JobKind.CheckLed || kind == JobKind.BlinkLed || kind == JobKind.MultiLed) {
                var hsv = new ToolStripMenuItem("Color (HSV)");
                LivePreview pvNow = _hub.GetPreview(head);
                var test = new ToolStripMenuItem("Live test (green = matching pixels, count in ROI)") {
                    Enabled = live, Checked = pvNow != null && pvNow.HsvTest };
                test.Click += (s, e) => { LivePreview p = _hub.GetPreview(head); if (p != null) { if (p.RunMode) p.SetRunMode(false); p.ReloadVision(); p.SetHsvTest(!p.HsvTest); } };
                hsv.DropDownItems.Add(test);

                bool isHsv; bool.TryParse(Csv(step, head, HeadConfigKey.HsvFlag), out isHsv);
                var space = new ToolStripMenuItem("Color space: " + (isHsv ? "HSV" : "BGR") + "   (click to switch)");
                space.Click += (s, e) => { if (WriteCsv(step, head, HeadConfigKey.HsvFlag, (!isHsv).ToString())) ReloadVision(head); };
                hsv.DropDownItems.Add(space);

                var range = new ToolStripMenuItem("Range  [" + Csv(step, head, HeadConfigKey.HsvFormat) + "]");
                range.Click += (s, e) => {
                    string input = Microsoft.VisualBasic.Interaction.InputBox(
                        "6 integers separated by spaces:\r\nmin1 max1 min2 max2 min3 max3\r\n" +
                        "HSV: H S V     BGR: B G R     (e.g. 35 85 80 255 80 255)",
                        "Color range", Csv(step, head, HeadConfigKey.HsvFormat), 500, 300);
                    if (string.IsNullOrWhiteSpace(input)) return;
                    string[] parts = input.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int tmp;
                    if (parts.Length != 6 || parts.Any(x => !int.TryParse(x, out tmp))) { MessageBox.Show("not format (need 6 integers)"); return; }
                    if (WriteCsv(step, head, HeadConfigKey.HsvFormat, string.Join(" ", parts))) ReloadVision(head);
                };
                hsv.DropDownItems.Add(range);
                AddValue(hsv, head, step, "Mask (min pixels)",  HeadConfigKey.HsvMask,    "Pixels inside the ROI that must match", null, false);
                AddValue(hsv, head, step, "Hold time (ms)",     HeadConfigKey.HsvTimeOut, "How long the colour must stay present",  null, false);
                m.Items.Add(hsv);
            }

            // Video format: MJPG sends compressed video, which is what lets several cameras share a USB controller / hub
            var fmt = new ToolStripMenuItem("Video format (USB bandwidth)");
            string fcPath = AppFilePath.Folder + head + "_fourcc.txt";
            bool isMjpg = File.Exists(fcPath);
            var mj = new ToolStripMenuItem("MJPG (compressed, less USB bandwidth)") { Checked = isMjpg };
            mj.Click += (s, e) => SetFourcc(head, fcPath, "MJPG");
            var def = new ToolStripMenuItem("Driver default (uncompressed)") { Checked = !isMjpg };
            def.Click += (s, e) => SetFourcc(head, fcPath, null);
            fmt.DropDownItems.Add(mj); fmt.DropDownItems.Add(def);
            m.Items.Add(fmt);

            // Process parameters (Read2D / OCR)
            var proc = new ToolStripMenuItem("Process");
            AddValue(proc, head, step, "Process timeout",    HeadConfigKey.StartTimeOut,   "Default = 500", null, false);
            AddValue(proc, head, step, "Process roi (move)", HeadConfigKey.RoiMove,        "Default = 10",  null, false);
            var roiSet = new ToolStripMenuItem("Process roi set (move on/off)  [" + Csv(step, head, HeadConfigKey.RoiMoveSet) + "]");
            roiSet.Click += (s, e) => {
                bool now; bool.TryParse(Csv(step, head, HeadConfigKey.RoiMoveSet), out now);
                WriteCsv(step, head, HeadConfigKey.RoiMoveSet, (!now).ToString());
            };
            proc.DropDownItems.Add(roiSet);
            AddValue(proc, head, step, "Process scale limit", HeadConfigKey.AutoScaleLimit, "Default = 40", null, false);
            AddValue(proc, head, step, "Process scale next",  HeadConfigKey.AutoScaleNext,  "Default = 2",  null, false);
            AddValue(proc, head, step, "Digit SN",            HeadConfigKey.DigitSN,        "Default = 13", null, false);
            AddValue(proc, head, step, "Adjust degree",       HeadConfigKey.AdjustDegree,   "Default = 0",  null, false);
            m.Items.Add(proc);

            // Address: how this head finds its camera
            var addr = new ToolStripMenuItem("Address");
            string curAddr = Csv(pstep, head, HeadConfigKey.SetAddress);
            var usePort = new ToolStripMenuItem("Use Port") { Checked = string.Equals(curAddr, CameraAddress.Port, StringComparison.OrdinalIgnoreCase) };
            usePort.Click += (s, e) => { if (Password()) WriteCsv(pstep, head, HeadConfigKey.SetAddress, CameraAddress.Port); };
            var useGamma = new ToolStripMenuItem("Use Gamma") { Checked = string.Equals(curAddr, CameraAddress.Gamma, StringComparison.OrdinalIgnoreCase) };
            useGamma.Click += (s, e) => { if (Password()) WriteCsv(pstep, head, HeadConfigKey.SetAddress, CameraAddress.Gamma); };
            var setGamma = new ToolStripMenuItem("Set Gamma (address)  [" + Csv(pstep, head, HeadConfigKey.Gamma) + "]");
            setGamma.Click += (s, e) => SetGamma(head, step);
            // Does the camera keep its gamma after unplugging? Read it back from the live camera to find out.
            var readGamma = new ToolStripMenuItem("Read gamma from the live camera") { Enabled = live };
            readGamma.Click += (s, e) => {
                LivePreview pv = RequireLive(head); if (pv == null) return;
                pv.Post(cap => {
                    double g = cap.GetCaptureProperty(CapProp.Gamma);
                    OnUi(() => MessageBox.Show("Head " + head + " (port " + pv.Port + "): camera gamma = " + g, "camera_show"));
                });
            };
            addr.DropDownItems.Add(usePort); addr.DropDownItems.Add(useGamma); addr.DropDownItems.Add(setGamma);
            addr.DropDownItems.Add(readGamma);
            m.Items.Add(addr);

            // Which cameraStep_*.csv this tile edits (default: the mode written in test_head_N_steptest.txt)
            var modeMenu = new ToolStripMenuItem("Mode (which CSV to edit)");
            foreach (string name in StepNames()) {
                var it = new ToolStripMenuItem(name) { Checked = string.Equals(name, step, StringComparison.OrdinalIgnoreCase) };
                string chosen = name;
                it.Click += (s, e) => {
                    _setupStep[head] = chosen;
                    if (_hub.IsPreviewing(head)) StartLive(head, _hub.PreviewPort(head), false, true, KeepRunMode(head));
                    else SetStatus(head, "mode " + chosen);
                };
                modeMenu.DropDownItems.Add(it);
            }
            modeMenu.DropDownItems.Add(new ToolStripSeparator());
            var newCompare = new ToolStripMenuItem("+ New Compare mode (template check)...");
            newCompare.Click += (s, e) => CreateCompareMode(head, step);
            modeMenu.DropDownItems.Add(newCompare);
            var newMulti = new ToolStripMenuItem("+ New Multi-LED mode (several LEDs at once)...");
            newMulti.Click += (s, e) => CreateMultiLedMode(head, step);
            modeMenu.DropDownItems.Add(newMulti);
            m.Items.Add(modeMenu);
        }

        /// <summary>
        /// A mode is a cameraStep_{name}.csv. A Compare mode is any mode whose name contains "compar": this makes one by copying
        /// the CSV of the mode being edited (same camera port, frame size, timeout), then selects it for this head.
        /// </summary>
        private void CreateCompareMode(int head, string currentStep) {
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Name of the new Compare mode.\r\nIt must contain \"compar\" (e.g. compar_image).\r\n" +
                "Its settings start as a copy of: " + (currentStep ?? "(none)"), "New Compare mode", "compar_image", 500, 300);
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.IndexOf("compar", StringComparison.OrdinalIgnoreCase) < 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
                MessageBox.Show("The name must contain \"compar\" and no special characters.", "camera_show");
                return;
            }

            string target = StepConfig.ConfigDir + "cameraStep_" + name + ".csv";
            if (!File.Exists(target)) {
                string source = currentStep != null && !StepConfig.IsSetPortMode(currentStep) ? currentStep
                              : StepNames().FirstOrDefault(n => !StepConfig.IsSetPortMode(n));
                string srcFile = source == null ? null : StepConfig.ConfigDir + "cameraStep_" + source + ".csv";
                if (srcFile == null || !File.Exists(srcFile)) { MessageBox.Show("No mode to copy the settings from.", "camera_show"); return; }
                try {
                    File.Copy(srcFile, target);
                    Directory.CreateDirectory(StepConfig.ConfigDir + name + "/");
                } catch (Exception ex) { MessageBox.Show("Can not create the mode: " + ex.Message, "camera_show"); return; }
            }
            _setupStep[head] = name;
            SetStatus(head, "mode " + name + " - now Start live view, then ROI > Draw ROI around what must be present");
        }

        /// <summary>
        /// New Multi-LED mode: a mode whose name contains "multi_led". Its settings (camera port, frame size, LED colour range, mask, hold time)
        /// start as a copy of a Check LED mode, because it uses the same colour settings; then draw LED 1, 2, 3 ... with ROI > Draw ROI.
        /// </summary>
        private void CreateMultiLedMode(int head, string currentStep) {
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Name of the new Multi-LED mode.\r\nIt must contain \"multi_led\" (e.g. multi_led).\r\n" +
                "All LEDs of it are checked in the same picture.", "New Multi-LED mode", "multi_led", 500, 300);
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.IndexOf("multi_led", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("multiled", StringComparison.OrdinalIgnoreCase) < 0 ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
                MessageBox.Show("The name must contain \"multi_led\" and no special characters.", "camera_show");
                return;
            }

            string target = StepConfig.ConfigDir + "cameraStep_" + name + ".csv";
            if (!File.Exists(target)) {
                // prefer a Check LED mode as the source: same colour settings
                string source = StepNames().FirstOrDefault(n => KindOfName(n) == JobKind.CheckLed)
                             ?? (currentStep != null && !StepConfig.IsSetPortMode(currentStep) ? currentStep : StepNames().FirstOrDefault(n => !StepConfig.IsSetPortMode(n)));
                string srcFile = source == null ? null : StepConfig.ConfigDir + "cameraStep_" + source + ".csv";
                if (srcFile == null || !File.Exists(srcFile)) { MessageBox.Show("No mode to copy the settings from.", "camera_show"); return; }
                try {
                    File.Copy(srcFile, target);
                    Directory.CreateDirectory(StepConfig.ConfigDir + name + "/");
                    string sel = LedSelect.PathFor(StepConfig.ConfigDir + name + "/", head);
                    if (!File.Exists(sel)) File.WriteAllText(sel, new LedSelect().ToFileText());
                } catch (Exception ex) { MessageBox.Show("Can not create the mode: " + ex.Message, "camera_show"); return; }
            }
            _setupStep[head] = name;
            _ledStep[head] = 1;
            SetStatus(head, "mode " + name + " - Start live view, then ROI > Draw ROI: draw LED 1, then LED 2 ...  (ROI > LEDs to check: which of them count)");
        }

        private static JobKind? KindOfName(string stepName) {
            string reject;
            JobSpec j = JobSpec.FromStepName(1, stepName, "", out reject);
            return j == null ? (JobKind?)null : j.Kind;
        }

        /// <summary>ROI menu of a Multi-LED mode: which LEDs must be on / off (written to config\{mode}\{head}led_select.txt).</summary>
        private void AddLedSelectItems(ToolStripMenuItem roiMenu, int head, string step) {
            string folder = StepConfig.ConfigDir + step + "/";
            LedSelect cur = LedSelect.Load(folder, head);
            roiMenu.DropDownItems.Add(new ToolStripSeparator());

            string onText = cur.Error != null ? "(file has an error)" : cur.All ? "all taught LEDs" : LedSelect.Format(cur.On);
            var onItem = new ToolStripMenuItem("LEDs that must be ON  [" + onText + "]  (change...)");
            onItem.Click += (s, e) => {
                string input = Microsoft.VisualBasic.Interaction.InputBox(
                    "Which LEDs must be lit (all together)?\r\nExamples:   1-3,5-7,9-10     1,4,7     all\r\n" +
                    "LEDs not in the list are not looked at.", "LEDs that must be ON", cur.All ? "all" : LedSelect.Format(cur.On), 500, 300);
                if (string.IsNullOrWhiteSpace(input)) return;
                bool all; SortedSet<int> set; string err;
                if (!LedSelect.ParseList(input, out all, out set, out err)) { MessageBox.Show(err, "LEDs that must be ON"); return; }
                LedSelect n = LedSelect.Load(folder, head); n.Error = null;
                n.All = all; n.On = all ? new SortedSet<int>() : set;
                foreach (int x in n.On) n.Off.Remove(x);
                SaveLedSelect(head, folder, n);
            };
            roiMenu.DropDownItems.Add(onItem);

            var offItem = new ToolStripMenuItem("LEDs that must be OFF  [" + (cur.Off.Count == 0 ? "none" : LedSelect.Format(cur.Off)) + "]  (change...)");
            offItem.Click += (s, e) => {
                string input = Microsoft.VisualBasic.Interaction.InputBox(
                    "Which LEDs must be DARK? (optional)\r\nExamples:   4,8     none", "LEDs that must be OFF", cur.Off.Count == 0 ? "none" : LedSelect.Format(cur.Off), 500, 300);
                if (input == null) return;
                bool all; SortedSet<int> set; string err;
                string t = input.Trim();
                if (t.Length == 0 || string.Equals(t, "none", StringComparison.OrdinalIgnoreCase)) set = new SortedSet<int>();
                else if (!LedSelect.ParseList(t, out all, out set, out err) || all) { MessageBox.Show(err ?? "off=all makes no sense", "LEDs that must be OFF"); return; }
                LedSelect n = LedSelect.Load(folder, head); n.Error = null;
                n.Off = set;
                foreach (int x in n.Off) n.On.Remove(x);
                SaveLedSelect(head, folder, n);
            };
            roiMenu.DropDownItems.Add(offItem);
        }

        private void SaveLedSelect(int head, string folder, LedSelect n) {
            try {
                Directory.CreateDirectory(folder);
                File.WriteAllText(LedSelect.PathFor(folder, head), n.ToFileText());
                LivePreview p = _hub.GetPreview(head);
                if (p != null) p.RestartMode();                                         // the running live check reads the file again
                SetStatus(head, "LEDs to check saved: on=" + (n.All ? "all" : LedSelect.Format(n.On)) + (n.Off.Count > 0 ? "  off=" + LedSelect.Format(n.Off) : ""));
            } catch (Exception ex) { MessageBox.Show("Can not save: " + ex.Message, "camera_show"); }
        }

        /// <summary>runMode = true: run the check of the mode (like a real job). false: only show the picture.</summary>
        private void StartLive(int head, int port, bool persistPort, bool streaming = true, bool runMode = false) {
            string step = SetupStep(head);
            if (step == null) { MessageBox.Show("Choose a mode first (right-click > Mode).", "camera_show"); return; }
            SetStatus(head, "Opening camera");
            Task.Run(() => {
                string err;
                if (!_hub.StartPreview(head, step, port, persistPort, streaming, runMode, out err))
                    OnUi(() => SetStatus(head, "FAIL: " + err));
            });
        }

        private LivePreview RequireLive(int head) {
            LivePreview pv = _hub.GetPreview(head);
            if (pv == null) MessageBox.Show("Start the live view of head " + head + " first.", "camera_show");
            return pv;
        }

        /// <summary>One "name [value]" menu entry: InputBox -> CSV -> (live camera | restart for frame size).</summary>
        private void AddValue(ToolStripMenuItem parent, int head, string step, string label, string suffix,
                              string hint, CapProp? liveProp, bool restartLive) {
            var item = new ToolStripMenuItem(label + "  [" + Csv(step, head, suffix) + "]");
            item.Click += (s, e) => {
                string input = Microsoft.VisualBasic.Interaction.InputBox(
                    "Keys " + label + "\r\n" + hint, label, Csv(step, head, suffix), 500, 300);
                if (string.IsNullOrEmpty(input)) return;
                int v;
                if (!int.TryParse(input.Trim(), out v)) { MessageBox.Show("not format"); return; }
                if (!WriteCsv(step, head, suffix, v.ToString())) return;

                LivePreview pv = _hub.GetPreview(head);
                if (pv == null) return;
                pv.ReloadVision();                                         // ROI / rotation / colour may depend on it
                if (liveProp.HasValue) { CapProp prop = liveProp.Value; pv.Post(cap => CameraWorker.ApplyProp(pv.Skip, cap, prop, v)); }
                if (restartLive) StartLive(head, pv.Port, false, true, pv.RunMode);          // resolution is applied when the camera opens
            };
            parent.DropDownItems.Add(item);
        }

        private void SaveLiveValues(int head, string step) {
            LivePreview pv = RequireLive(head);
            if (pv == null || !Password()) return;
            string[] keys  = { HeadConfigKey.Zoom, HeadConfigKey.Pan, HeadConfigKey.Tilt, HeadConfigKey.Contrast,
                               HeadConfigKey.Brightness, HeadConfigKey.Saturation, HeadConfigKey.Sharpness,
                               HeadConfigKey.Gain, HeadConfigKey.Gamma };
            CapProp[] props = { CapProp.Zoom, CapProp.Pan, CapProp.Tilt, CapProp.Contrast,
                                CapProp.Brightness, CapProp.Saturation, CapProp.Sharpness, CapProp.Gain, CapProp.Gamma };
            pv.Post(cap => {                                  // runs on the preview thread, which owns the capture
                int saved = 0;
                for (int i = 0; i < keys.Length; i++) {
                    try {
                        double val = cap.GetCaptureProperty(props[i]);
                        string target = keys[i] == HeadConfigKey.Gamma ? StepConfig.PortStep(step) : step;
                        if (StepConfig.WriteValue(target, head, keys[i], val.ToString())) saved++;
                    } catch { }
                }
                OnUi(() => MessageBox.Show("Saved " + saved + " of " + keys.Length + " values of head " + head +
                                           " to cameraStep_" + step + ".csv", "camera_show"));
            });
        }

        private void SetGamma(int head, string step) {
            if (!Password()) return;
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Set gamma address", "Gamma Address", Csv(StepConfig.PortStep(step), head, HeadConfigKey.Gamma), 500, 300);
            if (string.IsNullOrEmpty(input)) return;
            int v;
            if (!int.TryParse(input.Trim(), out v)) { MessageBox.Show("not format"); return; }
            if (!WriteCsv(StepConfig.PortStep(step), head, HeadConfigKey.Gamma, v.ToString())) return;
            LivePreview pv = _hub.GetPreview(head);                          // also write it into the live camera
            if (pv != null) pv.Post(cap => cap.SetCaptureProperty(CapProp.Gamma, v));
        }

        // ── ROI drawing ──────────────────────────────────────────────────────

        private void ToggleDraw(int tile, int head) {
            if (!_hub.IsPreviewing(head)) { MessageBox.Show("Start the live view of head " + head + " first.", "camera_show"); return; }
            _roiDraw[tile] = !_roiDraw[tile];
            _pics[tile].Cursor = _roiDraw[tile] ? Cursors.Cross : Cursors.Default;
            SetStatus(head, _roiDraw[tile] ? "Drag a rectangle on the picture" : "Draw ROI cancelled");
        }

        private Rectangle DragRect(int tile) {
            Point a = _dragA[tile], b = _dragB[tile];
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private void FinishRoi(int tile) {
            int head = HeadOfTile(tile);
            string step = SetupStep(head);
            LivePreview pv = _hub.GetPreview(head);
            PointF a, b;
            if (pv == null || step == null || !_pics[tile].TryMapToFrame(_dragA[tile], out a) || !_pics[tile].TryMapToFrame(_dragB[tile], out b)) return;

            FrameInfo fi = _pics[tile].Image == null ? null : _pics[tile].Image.Tag as FrameInfo;
            Size fs = fi != null ? fi.FrameSize : pv.FrameSize;
            int x0 = Clamp((int)Math.Min(a.X, b.X), 0, fs.Width),  x1 = Clamp((int)Math.Max(a.X, b.X), 0, fs.Width);
            int y0 = Clamp((int)Math.Min(a.Y, b.Y), 0, fs.Height), y1 = Clamp((int)Math.Max(a.Y, b.Y), 0, fs.Height);
            var r = new Rectangle(x0, y0, x1 - x0, y1 - y0);

            int crop = RoiCrop();
            if (r.Width < crop || r.Height < crop) { SetStatus(head, "ROI too small (min " + crop + " px) - not saved"); return; }
            ApplyDrawnRoi(head, step, pv, r, crop);
        }

        /// <summary>What the old pictureBox1_MouseUp did for each mode.</summary>
        private void ApplyDrawnRoi(int head, string step, LivePreview pv, Rectangle r, int crop) {
            JobKind? kind = KindOf(head, step);
            string folder = StepConfig.ConfigDir + step + "/";

            try {
                if (kind == JobKind.Read2d) {
                    pv.DecodeCrop(r, text => OnUi(() => {
                        if (text != null) MessageBox.Show(text, "Read2D test decode (head " + head + ")");
                    }));
                } else if (kind == JobKind.CheckLed) {
                    int n = LedStep(head);
                    Directory.CreateDirectory(folder);
                    using (var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
                        bmp.Save(folder + head + "Image" + n + ".png", ImageFormat.Png);   // step marker the LED check looks for
                    SaveStepRect(folder, head, n, r);
                    if (n != 1) {
                        pv.SetRoi(r); pv.RestartMode();
                        SetStatus(head, "LED step " + n + " ROI saved (files in config\\" + step + ")");
                        return;                                                          // step 2+ never touches the CSV
                    }
                } else if (kind == JobKind.MultiLed) {
                    // every LED has its own box in the files (no LED uses the CSV box); draw LED 1, 2, 3 ... one after the other
                    int n = LedStep(head);
                    Directory.CreateDirectory(folder);
                    using (var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
                        bmp.Save(folder + head + "Image" + n + ".png", ImageFormat.Png);   // marker: LED n is taught
                    SaveStepRect(folder, head, n, r);
                    pv.SetRoi(r); pv.RestartMode();
                    if (n < 100) _ledStep[head] = n + 1;                                  // ready for the next LED
                    SetStatus(head, "LED " + n + " saved - now draw LED " + (n < 100 ? n + 1 : n) + " (or ROI > LED number to redraw another one)");
                    return;
                } else if (kind == JobKind.Compare) {
                    int n = LedStep(head);
                    // The picture on screen is the LIVE picture (workpiece / zoom may be anywhere) but the other symbols were taught in the
                    // TAUGHT picture. While the tracker follows the LCD, carry the drawn box back into the taught picture before saving.
                    double[] pn; string comp = "";
                    Rectangle drawn = r;
                    Rectangle frameRect = new Rectangle(Point.Empty, pv.FrameSize);
                    bool tracked = SymbolPoseBus.TryGet(head, out pn);
                    bool follow = tracked && !SymbolAligner.IsIdentity(pn);
                    bool othersTaught = n > 1 || File.Exists(folder + head + "Image2.png");
                    if (!tracked && othersTaught &&
                        MessageBox.Show("The LCD is not being tracked right now (the boxes are not on the workpiece).\r\n\r\n" +
                                        "This box would be saved at the place you drew, but the other symbols were taught at the taught position - " +
                                        "if the workpiece or zoom is not exactly as then, the new box will come out shifted.\r\n\r\n" +
                                        "Wait until the boxes follow the workpiece, then draw again.  Save anyway?",
                                        "camera_show - ROI", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) {
                        SetStatus(head, "ROI not saved (LCD not tracked)");
                        return;
                    }
                    if (follow) r = SymbolAligner.MapRect(pn, r);
                    // The saved picture can only hold what is inside the camera picture. The search box is later centred on the template
                    // (Search = template + padding), so when a box touches the picture edge the template must be cut the same way,
                    // or the box lands shifted by half of what was cut off (the symbol sits at the edge of the picture: draw it a bit smaller).
                    Rectangle inside = Rectangle.Intersect(r, frameRect);
                    if (inside != r && inside.Width > 0 && inside.Height > 0) { comp += " - box touched the picture edge, cut to " + inside.Width + "x" + inside.Height; r = inside; }
                    CameraHub.Log("ROI compare step " + n + " head " + head + ": drawn " + drawn + (follow ? " -> taught " + r + " (N=" + string.Join(",", Array.ConvertAll(pn, v => v.ToString("F3"))) + ")" : " (tracker not following: saved as drawn)") + ", picture " + frameRect.Size);
                    if (follow) {
                        pv.SaveCropWarped(pn, r, folder + head + "Image" + n + ".png");   // the template, cut from the picture carried back to the taught size
                        comp = " - shift / zoom compensated" + comp;
                    } else {
                        pv.SaveCrop(r, folder + head + "Image" + n + ".png");              // the template: what must be present, before padding
                    }
                    r = new Rectangle(r.X - crop, r.Y - crop, r.Width + crop * 2, r.Height + crop * 2);
                    SaveStepRect(folder, head, n, r);                                      // where to look for it
                    if (n != 1) {
                        pv.SetRoi(r); pv.RestartMode();
                        SetStatus(head, "Compare step " + n + " saved (files in config\\" + step + ")" + comp);
                        return;                                                            // step 2+ never touches the CSV
                    }
                } else if (kind == JobKind.CheckOcr) {
                    pv.SaveCrop(r, folder + head + "Image1.png");                        // reference crop, before padding
                    r = new Rectangle(r.X - crop, r.Y - crop, r.Width + crop * 2, r.Height + crop * 2);
                    SaveStepRect(folder, head, 1, r);
                }
            } catch (Exception ex) {
                MessageBox.Show("Can not save ROI files: " + ex.Message, "camera_show");
                return;
            }

            if (!WriteCsv(step, head, HeadConfigKey.RectX, r.X.ToString())) return;
            if (!WriteCsv(step, head, HeadConfigKey.RectY, r.Y.ToString())) return;
            if (!WriteCsv(step, head, HeadConfigKey.RectWidth, r.Width.ToString())) return;
            if (!WriteCsv(step, head, HeadConfigKey.RectHeight, r.Height.ToString())) return;
            pv.SetRoi(r); pv.RestartMode();
            SetStatus(head, "ROI saved " + r.X + "," + r.Y + " " + r.Width + "x" + r.Height);
        }

        /// <summary>Line n-1 of the four Rectangle*.txt files (same layout as Form1.SaveRectangle).</summary>
        private static void SaveStepRect(string folder, int head, int number, Rectangle r) {
            Directory.CreateDirectory(folder);
            string[] names = { AppFilePath.RectX, AppFilePath.RectY, AppFilePath.RectWidth, AppFilePath.RectHeight };
            int[] vals     = { r.X, r.Y, r.Width, r.Height };
            for (int k = 0; k < 4; k++) {
                string path = folder + head + names[k];
                var lines = new List<string>();
                try { lines = File.ReadAllLines(path).ToList(); } catch { }
                while (lines.Count < Math.Max(100, number)) lines.Add("");
                lines[number - 1] = vals[k].ToString();
                File.WriteAllLines(path, lines);
            }
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        /// <summary>Minimum ROI size and OCR padding, from the same file the old program used.</summary>
        private static int RoiCrop() {
            try {
                string t = File.ReadAllText(AppPaths.ConfigDir + "CameraShow_RoiCrop.txt");
                if (!string.IsNullOrEmpty(t)) return Convert.ToInt32(t);
            } catch { }
            return 10;
        }

        private void AddCropItem(ToolStripMenuItem parent) {
            var item = new ToolStripMenuItem("ROI crop (min size / OCR padding): " + RoiCrop());
            item.Click += (s, e) => {
                string input = Microsoft.VisualBasic.Interaction.InputBox("Keys Roi Crop\r\nDefault = 10", "Roi Crop", RoiCrop().ToString(), 500, 300);
                int v;
                if (string.IsNullOrEmpty(input)) return;
                if (!int.TryParse(input.Trim(), out v) || v < 0) { MessageBox.Show("not format"); return; }
                try { File.WriteAllText(AppPaths.ConfigDir + "CameraShow_RoiCrop.txt", v.ToString()); } catch (Exception ex) { MessageBox.Show(ex.Message); }
            };
            parent.DropDownItems.Add(item);
        }

        private int LedStep(int head) { int n; return _ledStep.TryGetValue(head, out n) ? n : 1; }

        private static string CsvRect(string step, int head) {
            return Csv(step, head, HeadConfigKey.RectX) + "," + Csv(step, head, HeadConfigKey.RectY) + "  " +
                   Csv(step, head, HeadConfigKey.RectWidth) + "x" + Csv(step, head, HeadConfigKey.RectHeight);
        }

        private static JobKind? KindOf(int head, string step) {
            if (step == null) return null;
            string reject;
            JobSpec j = JobSpec.FromStepName(head, step, "", out reject);
            return j == null ? (JobKind?)null : j.Kind;
        }

        /// <summary>True when the head's live view is currently running the mode (so a reopened view keeps doing so).</summary>
        private bool KeepRunMode(int head) { LivePreview p = _hub.GetPreview(head); return p != null && p.RunMode; }

        private void ReloadVision(int head) { LivePreview p = _hub.GetPreview(head); if (p != null) p.ReloadVision(); }

        private void SetFourcc(int head, string path, string fourcc) {
            try {
                if (fourcc == null) { if (File.Exists(path)) File.Delete(path); }
                else File.WriteAllText(path, fourcc);
            } catch (Exception ex) { MessageBox.Show("Can not change video format: " + ex.Message, "camera_show"); return; }
            LivePreview pv = _hub.GetPreview(head);
            if (pv != null) StartLive(head, pv.Port, false, true, pv.RunMode);   // reopen so the new format is negotiated
            else SetStatus(head, fourcc == null ? "video format: driver default" : "video format: " + fourcc);
        }

        // ── Small helpers ────────────────────────────────────────────────────

        /// <summary>Mode this tile edits: the one picked in the menu, else line 1 of test_head_N_steptest.txt.</summary>
        private string SetupStep(int head) {
            string chosen;
            if (_setupStep.TryGetValue(head, out chosen)) return chosen;
            try {
                string[] lines = File.ReadAllLines(StepConfig.ConfigDir + "test_head_" + head + "_steptest.txt");
                string name = lines.Length > 0 ? lines[0].Trim() : "";
                if (name.Length > 0 && File.Exists(StepConfig.ConfigDir + "cameraStep_" + name + ".csv")) return name;
            } catch { }
            return null;
        }

        private static IEnumerable<string> StepNames() {
            try {
                return Directory.GetFiles(StepConfig.ConfigDir, "cameraStep_*.csv")
                    .Select(f => Path.GetFileNameWithoutExtension(f).Substring("cameraStep_".Length))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            } catch { return new string[0]; }
        }

        private static string Csv(string step, int head, string suffix) {
            if (step == null) return "?";
            try { return StepConfig.Load(step).Get(head, suffix); } catch { return "?"; }
        }

        private static int CsvInt(string step, int head, string suffix) {
            int v; return int.TryParse(Csv(step, head, suffix), out v) ? v : -1;
        }

        private static bool WriteCsv(string step, int head, string suffix, string value) {
            try {
                if (StepConfig.WriteValue(step, head, suffix, value)) return true;
                MessageBox.Show("'Head " + head + suffix + "' is not in cameraStep_" + step + ".csv", "camera_show");
            } catch (Exception ex) {
                MessageBox.Show("Can not write cameraStep_" + step + ".csv (is it locked by another program?)\n" + ex.Message, "camera_show");
            }
            return false;
        }

        /// <summary>Same password gate as Form1 (CheckPasswordSetAddress).</summary>
        private static bool Password() {
            while (true) {
                var form = new KeyPassword();
                if (form.ShowDialog() != DialogResult.OK) return false;
                if (form.inputValue != "camera") { MessageBox.Show("Password Error!!"); continue; }
                return true;
            }
        }

        /// <summary>Marshals to the UI thread without blocking the camera thread. False if the form is gone.</summary>
        private bool OnUi(Action a) {
            if (IsDisposed || Disposing || !IsHandleCreated) return false;
            try { BeginInvoke(a); return true; } catch { return false; }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) foreach (var p in _pics) if (p != null && p.Image != null) p.Image.Dispose();
            base.Dispose(disposing);
        }
    }
}
