using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GrimsGhostSetup
{
    static class Program
    {
        public const string AppName = "Grim's Ghost";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GrimsGhost";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--uninstall")) { Uninstaller.Run(); return; }
            if (args.Contains("--quiet")) { Environment.ExitCode = Quiet(args); return; }
            Application.Run(new SetupForm(args.Contains("--modify")));
        }

        /// <summary>
        /// Unattended install for IT: GrimsGhostSetup.exe --quiet [--screenshots-only] [--no-desktop] [--startup] [--no-launch] [--dir "path"]
        /// </summary>
        static int Quiet(string[] args)
        {
            try
            {
                int i = Array.IndexOf(args, "--dir");
                string dir = i >= 0 && i + 1 < args.Length ? args[i + 1]
                    : InstalledDir() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
                using (var f = new SetupForm(false))
                    f.Install(dir, !args.Contains("--screenshots-only"), !args.Contains("--no-desktop"), args.Contains("--startup"));
                if (!args.Contains("--no-launch")) Process.Start(Path.Combine(dir, "GrimsGhost.exe"));
                return 0;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "GrimsGhostSetup.log"), DateTime.Now + " " + ex + Environment.NewLine); } catch { }
                return 1;
            }
        }

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k?.GetValue("InstallLocation") as string;
        }

        public static void StopApp()
        {
            foreach (var name in new[] { "GrimsGhost", OldName })
                foreach (var p in Process.GetProcessesByName(name))
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        // ---- upgrading from the app's earlier name, "Grim's Gyzer" ----
        public const string OldName = "GrimsGyzer";
        const string OldDisplay = "Grim's Gyzer";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string OldInstallDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + OldName))
                return k?.GetValue("InstallLocation") as string;
        }
        public static bool OldStartupOn() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue(OldName) != null; }
        public static bool OldDesktopIcon() => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), OldDisplay + ".lnk"));

        /// <summary>Removes the old-named install (shortcuts, startup entry, uninstall entry, program folder). Captures are untouched.</summary>
        public static void RemoveOldVersion()
        {
            foreach (var lnk in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), OldDisplay + ".lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), OldDisplay + ".lnk") })
                try { File.Delete(lnk); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k?.DeleteValue(OldName, false); } catch { }
            string dir = OldInstallDir();
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + OldName, false); } catch { }
            if (dir != null && File.Exists(Path.Combine(dir, OldName + ".exe")))
                try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), OldName), true); } catch { }
        }

        public static void Shortcut(string lnk, string target, string args = "")
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Private screenshots and screen recording" });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
    }

    /// <summary>What this PC already has, and what each feature level needs.</summary>
    static class Detect
    {
        public static string ExistingFfmpeg(string installDir)
        {
            var candidates = new[] { Path.Combine(installDir ?? "", "ffmpeg.exe") }.ToList();
            // Real winget package folder first (it has the LICENSE beside it), then anything on PATH.
            var pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.GetDirectories(pk, "Gyan.FFmpeg*"))
                    candidates.AddRange(Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories));
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" + (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
            foreach (var d in path.Split(';').Where(x => x.Trim().Length > 0))
                try { candidates.Add(Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe")); } catch { }
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>(description, has a hardware encoder, has hardware AV1)</summary>
        public static (string Text, bool Encoder, bool Av1) Gpu()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (ManagementObject o in q.Get())
                    {
                        string n = (o["Name"] as string ?? "").Trim();
                        string u = n.ToUpperInvariant();
                        if (u.Contains("NVIDIA"))
                        {
                            bool av1 = System.Text.RegularExpressions.Regex.IsMatch(u, @"RTX\s*(40|50)\d\d|RTX\s*(PRO|[456]000)\s*ADA|BLACKWELL");
                            return ($"{n}: NVENC (H.264, HEVC{(av1 ? ", AV1" : "")})", true, av1);
                        }
                        if (u.Contains("RADEON") || u.Contains("AMD"))
                        {
                            bool av1 = System.Text.RegularExpressions.Regex.IsMatch(u, @"RX\s*[79]\d\d\d");
                            return ($"{n}: AMF (H.264, HEVC{(av1 ? ", AV1" : "")})", true, av1);
                        }
                        if (u.Contains("INTEL") && !u.Contains("BASIC"))
                        {
                            bool av1 = u.Contains("ARC");
                            return ($"{n}: Quick Sync (H.264, HEVC{(av1 ? ", AV1" : "")})", true, av1);
                        }
                    }
            }
            catch { }
            return ("No hardware video encoder found: video will use the CPU", false, false);
        }
    }

    sealed class SetupForm : Form
    {
        const string FfmpegZip = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        readonly bool modify;
        readonly CheckBox fCore, fVideo, fReplay, dNet, dFfmpeg, oDesktop, oStartup, oLaunch;
        readonly Label lFfmpeg, lGpu, status;
        readonly TextBox folder;
        readonly ProgressBar bar;
        readonly Button go, cancel;
        string existingFfmpeg;
        readonly (string Text, bool Encoder, bool Av1) gpu;

        public SetupForm(bool modify)
        {
            this.modify = modify;
            Text = modify ? "Grim's Ghost: add or remove features" : "Install Grim's Ghost";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            string installed = Program.InstalledDir();
            string defaultDir = installed ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Program.AppName);
            existingFfmpeg = Detect.ExistingFfmpeg(installed) ?? Detect.ExistingFfmpeg(Program.OldInstallDir());
            gpu = Detect.Gpu();
            bool hadFfmpeg = installed != null && File.Exists(Path.Combine(installed, "ffmpeg.exe"));

            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(Px(16), Px(10), Px(16), Px(14)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(t);
            int row = 0;
            void Add(Control c, int col = 0, int span = 2) { c.Anchor = AnchorStyles.Left; t.Controls.Add(c, col, row); t.SetColumnSpan(c, span); if (col + span >= 2) row++; }
            Label H(string s) => new Label { Text = s, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold), ForeColor = Color.FromArgb(200, 40, 40), Margin = new Padding(0, Px(row == 0 ? 0 : 12), 0, Px(4)) };
            Label N(string s) => new Label { Text = s, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(Px(26), 0, 0, Px(4)) };
            CheckBox C(string s, bool on) => new CheckBox { Text = s, Checked = on, AutoSize = true, Margin = new Padding(Px(6), Px(3), Px(3), Px(1)) };

            Add(H("Features"));
            fCore = C("Screenshots + browser editor", true); fCore.Enabled = false; Add(fCore);
            Add(N("Always included. Nothing extra to download."));
            fVideo = C("Video recording", !modify || hadFfmpeg); Add(fVideo);
            Add(N("Record any area with sound. Needs ffmpeg."));
            fReplay = C("Instant replay (save the last few seconds)", !modify || hadFfmpeg); Add(fReplay);
            Add(N("Light background buffer like ShadowPlay. Needs ffmpeg + a GPU video encoder."));

            Add(H("Dependencies  (ticked automatically for the features you pick)"));
            dNet = C(".NET Framework 4.8", true); dNet.Enabled = false; Add(dNet);
            Add(N("Built into Windows 10/11 ✓"));
            dFfmpeg = C("ffmpeg (video engine)", false); Add(dFfmpeg);
            lFfmpeg = N(""); Add(lFfmpeg);
            var dGpu = C("GPU video encoder", gpu.Encoder); dGpu.Enabled = false; Add(dGpu);
            lGpu = N(gpu.Text); Add(lGpu);

            Add(H("Options"));
            folder = new TextBox { Text = defaultDir, Width = Px(360), Enabled = !modify };
            var browse = new Button { Text = "Browse…", AutoSize = true, Enabled = !modify };
            browse.Click += (a, b) => { using (var d = new FolderBrowserDialog { SelectedPath = folder.Text }) if (d.ShowDialog(this) == DialogResult.OK) folder.Text = Path.Combine(d.SelectedPath, Program.AppName); };
            var fl = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(Px(6), Px(3), 0, Px(3)) };
            fl.Controls.Add(folder); fl.Controls.Add(browse);
            Add(fl);
            oDesktop = C("Desktop shortcut (one-click launcher)", !modify || File.Exists(Program.DesktopLink)); Add(oDesktop);
            oStartup = C("Start with Windows", (modify && Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("GrimsGhost") != null) || Program.OldStartupOn()); Add(oStartup);
            oLaunch = C("Launch Grim's Ghost when done", true); Add(oLaunch);

            bar = new ProgressBar { Width = Px(440), Height = Px(14), Visible = false, Margin = new Padding(Px(6), Px(12), 0, 0) };
            Add(bar);
            status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(Px(6), Px(4), 0, 0), MaximumSize = new Size(Px(460), 0) };
            Add(status);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Px(12), 0, 0) };
            go = new Button { Text = modify ? "Apply" : "Install", AutoSize = true, MinimumSize = new Size(Px(96), 0) };
            cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(Px(96), 0) };
            cancel.Click += (a, b) => Close();
            go.Click += async (a, b) => await RunInstall();
            buttons.Controls.Add(cancel); buttons.Controls.Add(go);
            Add(buttons);
            AcceptButton = go;

            fVideo.CheckedChanged += (a, b) => Sync();
            fReplay.CheckedChanged += (a, b) => Sync();
            Sync();
        }

        int Px(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        bool NeedsFfmpeg => fVideo.Checked || fReplay.Checked;

        /// <summary>Required dependencies get ticked and locked; unneeded ones become optional.</summary>
        void Sync()
        {
            if (NeedsFfmpeg) { dFfmpeg.Checked = true; dFfmpeg.Enabled = false; }
            else { dFfmpeg.Enabled = true; dFfmpeg.Checked = false; }
            string src = existingFfmpeg != null ? "already on this PC, will be copied (no download)" : "≈110 MB download from gyan.dev, checksum-verified";
            lFfmpeg.Text = (NeedsFfmpeg ? "Required by the features above: " : "Optional: ") + src;
            lGpu.Text = gpu.Text + (fReplay.Checked && !gpu.Encoder ? "\nInstant replay will work but is heavier without one." : "");
        }

        void Status(string s, int pct = -1)
        {
            if (!IsHandleCreated) return; // quiet mode
            if (InvokeRequired) { BeginInvoke(new Action(() => Status(s, pct))); return; }
            status.Text = s;
            if (pct >= 0) { bar.Style = ProgressBarStyle.Continuous; bar.Value = Math.Min(100, pct); }
            else bar.Style = ProgressBarStyle.Marquee;
        }

        async Task RunInstall()
        {
            go.Enabled = cancel.Enabled = false;
            foreach (Control c in new Control[] { fVideo, fReplay, dFfmpeg, oDesktop, oStartup, oLaunch, folder }) c.Enabled = false;
            bar.Visible = true;
            string dir = folder.Text.Trim();
            bool wantFfmpeg = dFfmpeg.Checked, desktop = oDesktop.Checked, startup = oStartup.Checked, launch = oLaunch.Checked;
            try
            {
                await Task.Run(() => Install(dir, wantFfmpeg, desktop, startup));
                Status("Done. Grim's Ghost is installed: search \"Ghost\" in the Start menu any time.", 100);
                if (launch) Process.Start(Path.Combine(dir, "GrimsGhost.exe"));
                cancel.Text = "Close"; cancel.Enabled = true; AcceptButton = cancel;
            }
            catch (Exception ex)
            {
                Status("Setup failed: " + ex.Message, 0);
                cancel.Text = "Close"; cancel.Enabled = true;
            }
        }

        internal void Install(string dir, bool wantFfmpeg, bool desktop, bool startup)
        {
            Status("Closing Grim's Ghost if it's running…");
            bool upgrading = Program.OldInstallDir() != null;
            if (upgrading) { startup |= Program.OldStartupOn(); desktop |= Program.OldDesktopIcon(); }
            Program.StopApp();
            Directory.CreateDirectory(dir);

            Status("Installing the app…");
            string exe = Path.Combine(dir, "GrimsGhost.exe");
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.GrimsGhost.exe"))
            using (var f = File.Create(exe)) res.CopyTo(f);
            string setupCopy = Path.Combine(dir, "Setup.exe");
            if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
                File.Copy(Application.ExecutablePath, setupCopy, true);

            string ffTarget = Path.Combine(dir, "ffmpeg.exe");
            if (wantFfmpeg && !File.Exists(ffTarget))
            {
                if (existingFfmpeg != null && File.Exists(existingFfmpeg))
                {
                    Status("Copying ffmpeg from this PC…");
                    File.Copy(existingFfmpeg, ffTarget, true);
                    var lic = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(existingFfmpeg)) ?? "", "LICENSE");
                    if (File.Exists(lic)) File.Copy(lic, Path.Combine(dir, "ffmpeg-LICENSE.txt"), true);
                }
                else DownloadFfmpeg(dir);
            }
            else if (!wantFfmpeg)
            {
                foreach (var f in new[] { "ffmpeg.exe", "ffmpeg-LICENSE.txt" })
                    try { File.Delete(Path.Combine(dir, f)); } catch { }
            }

            if (upgrading) { Status("Removing the old Grim's Gyzer install…"); Program.RemoveOldVersion(); }
            Status("Adding shortcuts…");
            Program.Shortcut(Program.StartMenuLink, exe);
            if (desktop) Program.Shortcut(Program.DesktopLink, exe);
            else if (File.Exists(Program.DesktopLink)) File.Delete(Program.DesktopLink);

            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (startup) k.SetValue("GrimsGhost", "\"" + exe + "\" --startup");
                else k.DeleteValue("GrimsGhost", false);
            }

            long kb = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length) / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
            {
                k.SetValue("DisplayName", Program.AppName);
                k.SetValue("DisplayVersion", Application.ProductVersion);
                k.SetValue("Publisher", Program.AppName);
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + setupCopy + "\" --uninstall");
                k.SetValue("ModifyPath", "\"" + setupCopy + "\" --modify");
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        void DownloadFfmpeg(string dir)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string tmp = Path.Combine(Path.GetTempPath(), "GrimsGhost-ffmpeg-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "GrimsGhostSetup";
                    Status("Checking the ffmpeg download…");
                    string expected = wc.DownloadString(FfmpegZip + ".sha256").Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                    if (expected.Length != 64) throw new Exception("couldn't read the ffmpeg checksum");

                    // Plain streaming download: works on any thread (no event marshalling, so no deadlocks in --quiet mode).
                    var req = (HttpWebRequest)WebRequest.Create(FfmpegZip);
                    req.UserAgent = "GrimsGhostSetup";
                    req.AllowAutoRedirect = true;
                    using (var resp = req.GetResponse())
                    using (var net = resp.GetResponseStream())
                    using (var file = File.Create(tmp))
                    {
                        long total = resp.ContentLength, got = 0, lastShown = -1;
                        var buf = new byte[1 << 16];
                        int n;
                        while ((n = net.Read(buf, 0, buf.Length)) > 0)
                        {
                            file.Write(buf, 0, n);
                            got += n;
                            long mb = got / 1048576;
                            if (mb != lastShown)
                            {
                                lastShown = mb;
                                Status($"Downloading ffmpeg… {mb} of {Math.Max(1, total / 1048576)} MB", total > 0 ? (int)(got * 100 / total) : -1);
                            }
                        }
                        if (total > 0 && got != total) throw new Exception("ffmpeg download was interrupted");
                    }

                    Status("Verifying ffmpeg…");
                    string actual;
                    using (var sha = SHA256.Create()) using (var f = File.OpenRead(tmp))
                        actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                    if (actual != expected) throw new Exception("the ffmpeg download didn't match its checksum, so it wasn't installed");
                }

                Status("Unpacking ffmpeg…");
                using (var zip = ZipFile.OpenRead(tmp))
                {
                    var ff = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                             ?? throw new Exception("ffmpeg.exe wasn't in the download");
                    ff.ExtractToFile(Path.Combine(dir, "ffmpeg.exe"), true);
                    zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/LICENSE", StringComparison.OrdinalIgnoreCase))
                       ?.ExtractToFile(Path.Combine(dir, "ffmpeg-LICENSE.txt"), true);
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }

    static class Uninstaller
    {
        public static void Run()
        {
            string dir = Program.InstalledDir() ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (MessageBox.Show("Remove Grim's Ghost from this PC?\n\nYour screenshots and recordings are kept.",
                    "Uninstall Grim's Ghost", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Program.StopApp();
            foreach (var l in new[] { Program.StartMenuLink, Program.DesktopLink }) try { File.Delete(l); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) k?.DeleteValue("GrimsGhost", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, false); } catch { }
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrimsGhost"), true); } catch { }

            // Remove the install folder once this process (which lives in it) has exited.
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "GrimsGhost.exe")))
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{dir}\"")
                { CreateNoWindow = true, UseShellExecute = false });
            MessageBox.Show("Grim's Ghost was removed.", "Uninstall Grim's Ghost");
        }
    }
}
