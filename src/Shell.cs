using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GrimsGyzer
{
    static class Shell
    {
        const string LinkName = "Grim's Gyzer.lnk";

        /// <summary>
        /// Start Menu entry (makes the app show up in Windows search) kept pointing at this exe,
        /// plus a one-time Desktop shortcut as the one-click launcher.
        /// </summary>
        public static void EnsureShortcuts(Settings s)
        {
            string exe = Application.ExecutablePath;
            try
            {
                MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName), exe, false);
                bool installed = File.Exists(Path.Combine(Path.GetDirectoryName(exe), "Setup.exe")); // installer owns the desktop icon
                if (!s.DesktopShortcutCreated && !installed)
                {
                    MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName), exe, true);
                    s.DesktopShortcutCreated = true;
                    s.Save();
                }
            }
            catch (Exception ex) { Log.Write("shortcuts: " + ex.Message); }
        }

        static void MakeShortcut(string lnk, string exe, bool force)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var t = sc.GetType();
            string current = (string)t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
            if (!force && File.Exists(lnk) && string.Equals(current, exe, StringComparison.OrdinalIgnoreCase)) return;
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Private screenshots and screen recording" });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        /// <summary>Images open in the built-in browser editor; videos open directly in the browser's player.</summary>
        public static void OpenInBrowser(string file)
        {
            try
            {
                bool image = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                string uri = image ? EditorServer.UrlFor(file) : new Uri(file).AbsoluteUri;
                string browser = DefaultBrowserExe();
                if (browser != null && File.Exists(browser))
                    Process.Start(new ProcessStartInfo(browser, "\"" + uri + "\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("msedge.exe", "\"" + uri + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write("open in browser: " + ex.Message); }
        }

        static string DefaultBrowserExe()
        {
            foreach (var scheme in new[] { "https", "http" })
            {
                using (var k = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice"))
                {
                    var progId = k?.GetValue("ProgId") as string;
                    if (string.IsNullOrEmpty(progId)) continue;
                    using (var c = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                    {
                        var cmd = (c?.GetValue(null) as string)?.Trim();
                        if (string.IsNullOrEmpty(cmd)) continue;
                        if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); if (end > 1) return cmd.Substring(1, end - 1); }
                        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                        if (exe > 0) return cmd.Substring(0, exe + 4);
                    }
                }
            }
            return null;
        }

        /// <summary>Pops the tray menu at the cursor, exactly as a right-click would (handles focus/dismissal correctly).</summary>
        public static void ShowTrayMenu(NotifyIcon icon)
        {
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(icon, null);
        }
    }
}
