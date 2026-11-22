namespace camera_show {
    /// <summary>
    /// File-path values for every text file used to communicate with fMain
    /// or store per-head configuration.  The config folder and the output folder
    /// (result / call_exe_tric / list files) come from <see cref="AppPaths"/>, so the exe
    /// also works when another project runs it from a different folder.
    /// The values here must NOT be changed — fMain depends on the exact names.
    /// </summary>
    public static class AppFilePath {
        // ── Files written/read by fMain ──────────────────────────────────────
        public static readonly string Head   = AppPaths.ConfigDir + "head.txt";
        public static readonly string List   = AppPaths.OutputDir + "camera_show_list.txt";
        public static readonly string TricExe = AppPaths.OutputDir + "call_exe_tric.txt";
        public static readonly string SetPort = AppPaths.OutputDir + "CameraSetPort.txt";

        // ── Per-head config suffixes (appended to headTxt path) ──────────────
        public static readonly string StepTest    = "_steptest.txt";
        public static readonly string Debug       = "_debug.txt";
        public static readonly string TimeOut     = "_timeout.txt";
        public static readonly string AutoFocus   = "_autoFocus.txt";
        /// <summary>
        /// NEW: one CapProp name per line; any property listed here is silently
        /// skipped when applying camera settings (Issue #3).
        /// Example content:  Focus\nExposure
        /// </summary>
        public static readonly string SkipSettings = "_skip_settings.txt";

        // ── Config folder root ───────────────────────────────────────────────
        public static readonly string Folder = AppPaths.ConfigDir + "test_head_";

        // ── Rectangle data filenames (no directory prefix) ───────────────────
        public static readonly string RectX      = "RectangleX.txt";
        public static readonly string RectY      = "RectangleY.txt";
        public static readonly string RectWidth  = "RectangleWidth.txt";
        public static readonly string RectHeight = "RectangleHeight.txt";
        public static readonly string OcrSettings = "text.txt";
        public static readonly string TessData   = AppPaths.ConfigDir + "tessdata";
    }
}
