using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace GrimsGyzer
{
    /// <summary>Builds ffmpeg capture + encoder arguments for every codec / GPU vendor / rate-control combination.</summary>
    static class Encoders
    {
        public static readonly string[] Vendors = { "nvenc", "amf", "qsv", "sw" };

        public static string Name(VideoCodec c, string vendor)
        {
            if (vendor == "sw") return c == VideoCodec.H264 ? "libx264" : c == VideoCodec.HEVC ? "libx265" : null; // no realtime software AV1
            return (c == VideoCodec.H264 ? "h264" : c == VideoCodec.HEVC ? "hevc" : "av1") + "_" + vendor;
        }

        /// <summary>Attempts in preference order: (codec actually used, vendor). Falls back to H.264 if the codec isn't possible.</summary>
        public static IEnumerable<(VideoCodec Codec, string Vendor)> Attempts(VideoCodec wanted, string preferredVendor)
        {
            var vendors = new List<string>(Vendors);
            if (preferredVendor != null) { vendors.Remove(preferredVendor); vendors.Insert(0, preferredVendor); }
            foreach (var v in vendors) if (Name(wanted, v) != null) yield return (wanted, v);
            if (wanted != VideoCodec.H264) foreach (var v in vendors) yield return (VideoCodec.H264, v);
        }

        public static string Input(bool dda, int outputIndex, Rectangle outputBounds, Rectangle r, int fps) =>
            dda
                ? $"-f lavfi -i ddagrab=output_idx={outputIndex}:framerate={fps}:video_size={r.Width}x{r.Height}:offset_x={r.X - outputBounds.X}:offset_y={r.Y - outputBounds.Y}:draw_mouse=1 "
                : $"-f gdigrab -framerate {fps} -offset_x {r.X} -offset_y {r.Y} -video_size {r.Width}x{r.Height} -draw_mouse 1 -i desktop ";

        public static string Video(VideoQuality q, VideoCodec codec, string vendor, bool dda, int gop)
        {
            var sb = new StringBuilder();
            int b = q.BitrateMbps, s = (int)q.Speed;

            // Frame format: GPU encoders take Desktop-Duplication frames directly; others need a download.
            if (vendor == "sw") sb.Append(dda ? "-vf hwdownload,format=bgra,format=yuv420p " : "-pix_fmt yuv420p ");
            else if (vendor == "qsv") sb.Append(dda ? "-vf hwdownload,format=bgra,format=nv12 " : "-pix_fmt nv12 ");
            else if (!dda) sb.Append("-pix_fmt yuv420p ");

            sb.Append($"-c:v {Name(codec, vendor)} ");
            switch (vendor)
            {
                case "nvenc":
                    sb.Append(new[] { "-preset p2 ", "-preset p4 ", "-preset p6 " }[s]);
                    if (q.Rate == RateMode.Quality) sb.Append($"-rc vbr -cq {q.Quality} -b:v 0 ");
                    else if (q.Rate == RateMode.VBR) sb.Append($"-rc vbr -b:v {b}M -maxrate {b * 3 / 2}M -bufsize {b * 2}M ");
                    else sb.Append($"-rc cbr -b:v {b}M -maxrate {b}M -bufsize {b * 2}M ");
                    break;
                case "amf":
                    sb.Append(new[] { "-quality speed ", "-quality balanced ", "-quality quality " }[s]);
                    if (q.Rate == RateMode.Quality) sb.Append($"-rc cqp -qp_i {q.Quality} -qp_p {q.Quality + 2} ");
                    else if (q.Rate == RateMode.VBR) sb.Append($"-rc vbr_peak -b:v {b}M -maxrate {b * 3 / 2}M ");
                    else sb.Append($"-rc cbr -b:v {b}M ");
                    break;
                case "qsv":
                    sb.Append(new[] { "-preset veryfast ", "-preset medium ", "-preset slower " }[s]);
                    if (q.Rate == RateMode.Quality) sb.Append($"-global_quality {q.Quality} ");
                    else if (q.Rate == RateMode.VBR) sb.Append($"-b:v {b}M -maxrate {b * 3 / 2}M ");
                    else sb.Append($"-b:v {b}M -maxrate {b}M -bufsize {b * 2}M ");
                    break;
                default: // libx264 / libx265, kept fast enough for realtime
                    sb.Append(new[] { "-preset ultrafast ", "-preset superfast ", "-preset veryfast " }[s]);
                    if (q.Rate == RateMode.Quality) sb.Append($"-crf {q.Quality} ");
                    else if (q.Rate == RateMode.VBR) sb.Append($"-b:v {b}M -maxrate {b * 3 / 2}M -bufsize {b * 2}M ");
                    else sb.Append($"-b:v {b}M -minrate {b}M -maxrate {b}M -bufsize {b * 2}M ");
                    break;
            }
            sb.Append($"-g {gop} ");
            if (codec == VideoCodec.HEVC) sb.Append("-tag:v hvc1 ");
            return sb.ToString();
        }
    }
}
