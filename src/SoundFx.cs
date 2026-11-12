using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace GrimsGyzer
{
    /// <summary>
    /// Soft, warm feedback sounds, synthesised in code (no sound files). Played asynchronously.
    /// Recordings never contain them: default system sound is captured as "everything except Grim's Gyzer",
    /// and on older Windows the loopback capture is muted for the short moment a sound plays.
    /// </summary>
    static class SoundFx
    {
        /// <summary>Instant replay reuses the record chimes: rising when you press save, falling when it's written.</summary>
        public enum Kind { Shutter, RecordStart, RecordStop }

        public static bool Enabled = true;
        public static int Volume = 50;   // 0–100

        /// <summary>Stopwatch timestamp until which plain speaker-loopback capture writes silence.</summary>
        public static long SuppressUntil;

        const int Rate = 48000;
        static readonly Dictionary<(Kind, int), byte[]> cache = new Dictionary<(Kind, int), byte[]>();

        public static void Play(Kind k)
        {
            if (!Enabled || Volume <= 0) return;
            byte[] wav;
            lock (cache)
                if (!cache.TryGetValue((k, Volume), out wav))
                    cache[(k, Volume)] = wav = Wav(Synth(k, Volume / 100.0));
            double seconds = (wav.Length - 44) / 2.0 / Rate;
            SuppressUntil = Stopwatch.GetTimestamp() + (long)((seconds + 0.35) * Stopwatch.Frequency);
            Task.Run(() =>
            {
                try { using (var ms = new MemoryStream(wav)) using (var p = new SoundPlayer(ms)) p.PlaySync(); }
                catch (Exception ex) { Log.Write("sound: " + ex.Message); }
            });
        }

        // ------------------------------------------------------------------ synthesis

        static float[] Synth(Kind k, double vol)
        {
            float[] s;
            switch (k)
            {
                case Kind.Shutter:
                    // Two soft wooden taps (no noise, so nothing snare-like): a gentle "tok-tok", high then lower.
                    s = new float[(int)(Rate * 0.20)];
                    Tap(s, 0.000, 1046.5, 1.0);    // C6
                    Tap(s, 0.050, 784.0, 0.8);     // G5
                    break;
                case Kind.RecordStart:
                    s = new float[(int)(Rate * 0.34)];
                    Tone(s, 0.00, 523.25, 0.16, 0.9);   // C5
                    Tone(s, 0.09, 783.99, 0.22, 0.8);   // G5
                    break;
                case Kind.RecordStop:
                    s = new float[(int)(Rate * 0.34)];
                    Tone(s, 0.00, 783.99, 0.16, 0.85);
                    Tone(s, 0.09, 523.25, 0.22, 0.8);
                    break;
                default:
                    s = new float[1];
                    break;
            }
            // Gentle low-pass for warmth, then normalise to a soft peak
            float y = 0, peak = 1e-6f;
            for (int i = 0; i < s.Length; i++) { y += 0.35f * (s[i] - y); s[i] = y; peak = Math.Max(peak, Math.Abs(y)); }
            float gain = (float)(0.45 * vol / peak);
            for (int i = 0; i < s.Length; i++) s[i] *= gain;
            int fade = Math.Min(s.Length, Rate / 100);
            for (int i = 0; i < fade; i++) s[s.Length - 1 - i] *= i / (float)fade;
            return s;
        }

/// <summary>A soft wooden tap, like a muted marimba: quick fade-in, short body, a whisper of overtone.</summary>
        static void Tap(float[] s, double at, double f, double amp)
        {
            int start = (int)(at * Rate);
            for (int i = 0; start + i < s.Length; i++)
            {
                double t = i / (double)Rate;
                double env = Math.Min(1, t / 0.003) * Math.Exp(-t / 0.035);
                double v = Math.Sin(2 * Math.PI * f * t)
                         + 0.15 * Math.Sin(2 * Math.PI * f * 3.93 * t) * Math.Exp(-t / 0.008)  // brief "wood" overtone
                         + 0.30 * Math.Sin(Math.PI * f * t) * Math.Exp(-t / 0.02);            // a little body underneath
                s[start + i] += (float)(amp * env * v);
            }
        }

        /// <summary>A warm sine note with a little second harmonic.</summary>
        static void Tone(float[] s, double at, double f, double len, double amp)
        {
            int start = (int)(at * Rate), n = (int)(len * Rate);
            for (int i = 0; i < n && start + i < s.Length; i++)
            {
                double t = i / (double)Rate;
                double env = Math.Min(1, t / 0.008) * Math.Exp(-t / 0.075);
                double v = Math.Sin(2 * Math.PI * f * t) + 0.25 * Math.Sin(4 * Math.PI * f * t);
                s[start + i] += (float)(amp * env * v);
            }
        }

        static void Bell(float[] s, double at, double f, double amp)
        {
            int start = (int)(at * Rate);
            for (int i = 0; start + i < s.Length; i++)
            {
                double t = i / (double)Rate;
                double env = Math.Min(1, t / 0.004);
                double v = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t / 0.14)
                         + 0.35 * Math.Sin(2 * Math.PI * f * 2.76 * t) * Math.Exp(-t / 0.06)
                         + 0.12 * Math.Sin(2 * Math.PI * f * 5.4 * t) * Math.Exp(-t / 0.03);
                s[start + i] += (float)(amp * 0.6 * env * v);
            }
        }

        static byte[] Wav(float[] s)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int len = s.Length * 2;
                w.Write(0x46464952); w.Write(36 + len); w.Write(0x45564157);
                w.Write(0x20746d66); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(0x61746164); w.Write(len);
                foreach (var v in s) w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Writes a sound to a WAV file (for the dev test switch).</summary>
        public static void Export(Kind k, string path) => File.WriteAllBytes(path, Wav(Synth(k, Volume / 100.0)));
    }
}
