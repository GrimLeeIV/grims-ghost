using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GrimsGyzer
{
    /// <summary>
    /// Tiny private web server for the in-browser image editor.
    /// Loopback only, every image gets an unguessable token, and the Host header must match exactly
    /// (blocks DNS-rebinding), so no other site or machine can read your captures through it.
    /// </summary>
    static class EditorServer
    {
        static TcpListener listener;
        static int port;
        static readonly ConcurrentDictionary<string, string> files = new ConcurrentDictionary<string, string>();
        static readonly ConcurrentDictionary<string, string> lastSaved = new ConcurrentDictionary<string, string>();
        static string html;

        public static string UrlFor(string imagePath)
        {
            EnsureStarted();
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            files[token] = imagePath;
            return $"http://127.0.0.1:{port}/edit/{token}";
        }

        static void EnsureStarted()
        {
            if (listener != null) return;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GrimsGyzer.editor.html"))
            using (var r = new StreamReader(s, Encoding.UTF8)) html = r.ReadToEnd();
            // A stable port keeps the editor's remembered toolbar position; fall back to any free port.
            try { listener = new TcpListener(IPAddress.Loopback, 47851); listener.Start(); }
            catch (SocketException) { listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); }
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true, Name = "editor-server" }.Start();
            Log.Write("editor server on 127.0.0.1:" + port);
        }

        static void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } catch (Exception ex) { Log.Write("editor request: " + ex.Message); } finally { c.Close(); } });
            }
        }

        static void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 15000;
            var ns = client.GetStream();

            // --- read headers
            var head = new MemoryStream();
            int b, matched = 0;
            while (matched < 4 && (b = ns.ReadByte()) != -1)
            {
                head.WriteByte((byte)b);
                matched = (b == '\r' && (matched == 0 || matched == 2)) || (b == '\n' && (matched == 1 || matched == 3)) ? matched + 1 : 0;
                if (head.Length > 16384) return;
            }
            var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var req = lines[0].Split(' ');
            if (req.Length < 2) return;
            string method = req[0], path = req[1], host = null;
            long length = 0;
            foreach (var l in lines)
            {
                int i = l.IndexOf(':');
                if (i <= 0) continue;
                string k = l.Substring(0, i).Trim().ToLowerInvariant(), v = l.Substring(i + 1).Trim();
                if (k == "host") host = v;
                else if (k == "content-length") long.TryParse(v, out length);
            }
            if (host != "127.0.0.1:" + port) { Send(ns, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")); return; }

            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !files.TryGetValue(parts[1], out var file)) { Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return; }
            string token = parts[1];

            if (method == "GET" && parts[0] == "edit")
            {
                string page = html.Replace("{{TOKEN}}", token).Replace("{{NAME}}", WebUtility.HtmlEncode(Path.GetFileName(file)));
                Send(ns, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(page));
            }
            else if (method == "GET" && parts[0] == "img")
                Send(ns, 200, "image/png", File.ReadAllBytes(file));
            else if (method == "POST" && parts[0] == "saveas")
            {
                if (length <= 0 || length > 400L * 1024 * 1024) { Send(ns, 413, "text/plain", Encoding.UTF8.GetBytes("too large")); return; }
                var body = new byte[length];
                int read = 0;
                while (read < length) { int n = ns.Read(body, read, (int)Math.Min(65536, length - read)); if (n <= 0) break; read += n; }
                if (read != length || body.Length < 8 || body[0] != 0x89 || body[1] != (byte)'P') { Send(ns, 400, "text/plain", Encoding.UTF8.GetBytes("bad image")); return; }

                string outPath = AskSavePath(file);
                if (outPath == null) { Send(ns, 200, "application/json", Encoding.UTF8.GetBytes("{\"cancelled\":true}")); return; }
                if (outPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || outPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    SaveJpeg(body, outPath);
                else
                    File.WriteAllBytes(outPath, body);
                lastSaved[token] = outPath;
                Log.Write("editor saved " + outPath);
                Send(ns, 200, "application/json", Encoding.UTF8.GetBytes("{\"name\":\"" + Path.GetFileName(outPath).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}"));
            }
            else if (method == "POST" && parts[0] == "reveal")
            {
                string show = lastSaved.TryGetValue(token, out var saved) && File.Exists(saved) ? saved : file;
                Process.Start("explorer.exe", "/select,\"" + show + "\"");
                Send(ns, 200, "application/json", Encoding.UTF8.GetBytes("{}"));
            }
            else Send(ns, 405, "text/plain", Encoding.UTF8.GetBytes("method not allowed"));
        }

        static string LastFolderFile => Path.Combine(Settings.AppDataDir, "editor-last-folder.txt");

        /// <summary>
        /// Native Windows "Save as" window, opened in front of the browser. Starts in the folder you last saved to,
        /// or the image's own day folder. Returns null if cancelled.
        /// </summary>
        static string AskSavePath(string original)
        {
            string test = Environment.GetEnvironmentVariable("GYZER_TEST_SAVEAS"); // dev self-test only
            if (!string.IsNullOrEmpty(test)) return test;

            string result = null;
            var t = new Thread(() =>
            {
                string initial = null;
                try { if (File.Exists(LastFolderFile)) initial = File.ReadAllText(LastFolderFile).Trim(); } catch { }
                if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial)) initial = Path.GetDirectoryName(original);

                using (var owner = new System.Windows.Forms.Form
                {
                    TopMost = true, ShowInTaskbar = false, FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual, Size = new System.Drawing.Size(1, 1),
                    Location = System.Windows.Forms.Cursor.Position, Opacity = 0,
                })
                using (var d = new System.Windows.Forms.SaveFileDialog
                {
                    Title = "Save edited image as",
                    InitialDirectory = initial,
                    FileName = Path.GetFileName(EditedPath(original)),
                    Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
                    DefaultExt = "png", AddExtension = true, OverwritePrompt = true,
                })
                {
                    owner.Show();
                    Native.SetForegroundWindow(owner.Handle);
                    if (d.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    {
                        result = d.FileName;
                        try { File.WriteAllText(LastFolderFile, Path.GetDirectoryName(result)); } catch { }
                    }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return result;
        }

        /// <summary>JPEG has no transparency, so transparent corners (from rotating) become white.</summary>
        static void SaveJpeg(byte[] png, string path)
        {
            using (var ms = new MemoryStream(png))
            using (var src = new System.Drawing.Bitmap(ms))
            using (var flat = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(flat)) { g.Clear(System.Drawing.Color.White); g.DrawImage(src, 0, 0, src.Width, src.Height); }
                var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                using (var p = new System.Drawing.Imaging.EncoderParameters(1))
                {
                    p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                    flat.Save(path, codec, p);
                }
            }
        }

        static string EditedPath(string original)
        {
            string dir = Path.GetDirectoryName(original), name = Path.GetFileNameWithoutExtension(original);
            if (name.EndsWith(" (edited)")) name = name.Substring(0, name.Length - 9);
            string p = Path.Combine(dir, name + " (edited).png");
            for (int n = 2; File.Exists(p); n++) p = Path.Combine(dir, $"{name} (edited {n}).png");
            return p;
        }

        static void Send(Stream s, int code, string type, byte[] body)
        {
            string status = code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Error";
            var h = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {code} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
            s.Write(h, 0, h.Length);
            s.Write(body, 0, body.Length);
        }
    }
}
