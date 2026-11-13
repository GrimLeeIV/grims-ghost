using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GrimsGhost
{
    /// <summary>Text recognition with Windows' own OCR engine: offline, private, nothing to download.</summary>
    static class Ocr
    {
        public sealed class Result { public string Text, Language; public int Lines; }

        /// <summary>Blocking helper for background threads: text in a bitmap ("" if none).</summary>
        public static string ReadBitmap(System.Drawing.Bitmap bmp)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                lock (bmp) bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ReadAsync(ms.ToArray()).GetAwaiter().GetResult().Text ?? "";
            }
        }

        public static async Task<Result> ReadAsync(byte[] png)
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException("Windows has no text-recognition language installed (Settings → Time & Language → Language).");

            using (var ras = new InMemoryRandomAccessStream())
            {
                await ras.WriteAsync(png.AsBuffer());
                ras.Seek(0);
                var dec = await BitmapDecoder.CreateAsync(ras);
                uint w = dec.PixelWidth, h = dec.PixelHeight, longest = Math.Max(w, h);
                // Small UI text reads much better enlarged; stay within the engine's size limit.
                double k = longest <= 3000 ? 2 : 1;
                if (longest * k > OcrEngine.MaxImageDimension) k = (double)OcrEngine.MaxImageDimension / longest;
                var tf = new BitmapTransform
                {
                    ScaledWidth = Math.Max(1, (uint)(w * k)),
                    ScaledHeight = Math.Max(1, (uint)(h * k)),
                    InterpolationMode = BitmapInterpolationMode.Fant,
                };
                using (var bmp = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tf,
                                                                   ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage))
                {
                    var r = await engine.RecognizeAsync(bmp);
                    return new Result
                    {
                        Text = string.Join(Environment.NewLine, r.Lines.Select(l => l.Text)),
                        Lines = r.Lines.Count,
                        Language = engine.RecognizerLanguage.DisplayName,
                    };
                }
            }
        }
    }
}
