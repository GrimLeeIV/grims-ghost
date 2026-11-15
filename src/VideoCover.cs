using System;
using System.Diagnostics;
using System.IO;

namespace GrimsGhost
{
    /// <summary>
    /// Explorer (and anything using Windows thumbnails) only draws a video thumbnail when Windows itself can decode
    /// the video, and AV1/HEVC need extra Microsoft Store extensions. Embedding a cover picture (a frame from the
    /// video) makes the thumbnail show everywhere. Lossless: the video and sound are copied, not re-encoded.
    /// </summary>
    static class VideoCover
    {
        /// <summary>Adds a cover to an MP4 in place. Never throws: a missing cover isn't worth failing a save for.</summary>
        public static bool Embed(string ffmpeg, string video, string work)
        {
            if (!video.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(video)) return false;
            string jpg = Path.Combine(work, "cover-" + Guid.NewGuid().ToString("N") + ".jpg");
            string tmp = Path.Combine(Path.GetDirectoryName(video), "~" + Path.GetFileNameWithoutExtension(video) + ".cover.mp4");
            try
            {
                Directory.CreateDirectory(work);
                string info = Run(ffmpeg, $"-i \"{video}\"", work, 30000, out _);
                if (info.Contains("(attached pic)")) return true;   // already has one

                const string scale = "-frames:v 1 -vf \"scale='min(640,iw)':-2\" -q:v 3";
                Run(ffmpeg, $"-ss 1 -i \"{video}\" {scale} \"{jpg}\"", work, 30000, out _);
                if (!Exists(jpg)) Run(ffmpeg, $"-i \"{video}\" {scale} \"{jpg}\"", work, 30000, out _);   // shorter than a second
                if (!Exists(jpg)) { Log.Write("cover: no frame from " + video); return false; }

                Run(ffmpeg, $"-i \"{video}\" -i \"{jpg}\" -map 0 -map 1 -c copy -disposition:v:1 attached_pic -movflags +faststart \"{tmp}\"",
                    work, 120000, out int code);
                if (code != 0 || !Exists(tmp) || new FileInfo(tmp).Length < new FileInfo(video).Length)
                {
                    Log.Write($"cover: couldn't add to {video} (ffmpeg {code})");
                    return false;
                }
                File.Replace(tmp, video, null);
                return true;
            }
            catch (Exception ex) { Log.Write("cover: " + ex.Message); return false; }
            finally
            {
                try { if (File.Exists(jpg)) File.Delete(jpg); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        static bool Exists(string f) => File.Exists(f) && new FileInfo(f).Length > 0;

        static string Run(string ffmpeg, string args, string work, int timeoutMs, out int exitCode)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, "-hide_banner -nostdin -y -v info " + args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = work }
            };
            p.Start();
            Native.KillWithUs(p);
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } exitCode = -1; return ""; }
            exitCode = p.ExitCode;
            return err.Result;
        }
    }
}
