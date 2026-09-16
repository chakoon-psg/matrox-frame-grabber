using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MatroxFrameGrabber.Infrastructure;

namespace OnnxInspectorHarness
{
    /// <summary>
    /// Shows the locator working on a real frame: what the inspector found on the whole screen,
    /// what <see cref="RoiLocator"/> then decided look by look, and a picture with all of it
    /// drawn on.
    ///
    /// **Why a still and not the rig.** The region only moves when something is found, and the
    /// model in hand is a single-class "green" detector that finds nothing on these panels - a
    /// live run proves the wiring and nothing about the decision. A saved frame can be replayed,
    /// annotated, and put in a document, and `--simulate` lets the decision itself be watched
    /// without pretending the model produced it. Anything simulated says so on the picture.
    ///
    /// The stills this reads are the app's own: `StillRing` writes lossless PNG out of the same
    /// display buffer the locator reads, so the pixels here are the pixels it would see.
    /// </summary>
    internal static class LocateDemo
    {
        // Drawn in BGR, because that is the order the decoded pixels are in.
        private static readonly byte[] Before = { 80, 80, 255 };    // red    - region now
        private static readonly byte[] Found = { 80, 230, 80 };     // green  - what was found
        private static readonly byte[] After = { 255, 200, 60 };    // blue   - region after

        public static int Run(string model, string[] args)
        {
            string image = StringArg(args, "--image", null);
            if (image == null || !File.Exists(image))
            {
                Console.Error.WriteLine("locate needs --image <frame.png> (a still the app wrote)");
                return 1;
            }

            int looks = IntArg(args, "--looks", 6);
            float minScore = (float)DoubleArg(args, "--score", 0.5);
            string outPath = StringArg(args, "--out", null)
                          ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(image)) ?? ".",
                                          Path.GetFileNameWithoutExtension(image) + "-located.png");

            byte[] bgra = Decode(image, out int w, out int h);
            byte[] planar = ToPlanarGbrp(bgra, w, h);

            ChannelRoi start = RoiArg(args, "--roi") ?? ChannelRoi.FullFrame;
            Console.WriteLine($"frame  : {Path.GetFileName(image)}  {w}x{h}");
            Console.WriteLine($"region : {start}");
            Console.WriteLine();

            ScreenFinding[] findings;
            int count;
            bool simulated = false;

            ChannelRoi? fake = RoiArg(args, "--simulate");
            if (fake.HasValue)
            {
                simulated = true;
                float score = (float)DoubleArg(args, "--simulate-score", 0.92);
                findings = new[]
                {
                    new ScreenFinding(AnomalyKind.Unknown, "SIMULATED", score,
                                      fake.Value.OffsetX, fake.Value.OffsetY,
                                      fake.Value.Width, fake.Value.Height)
                };
                count = 1;
                Console.WriteLine($"*** SIMULATED finding {fake.Value} score {score:F2} - "
                                + "the model did not produce this ***");
            }
            else
            {
                using IScreenInspector inspector =
                    new MatroxFrameGrabber.Onnx.OnnxScreenInspector(model, minScore, 0.5f, 16, 0);
                Console.WriteLine($"model  : {inspector.Describe}");

                var box = new BgrLetterbox(w, h, inspector.InputWidth, inspector.InputHeight);
                Console.WriteLine($"letterbox: {box}");

                findings = new ScreenFinding[16];
                count = inspector.Inspect(box.Fill(planar, w, h, 0, 0), findings);

                // Model space to frame space, exactly as CameraChannel does it. The letterbox was
                // built for the whole frame, so what comes back is already in the region's space.
                for (int i = 0; i < count; i++)
                {
                    ScreenFinding f = findings[i];
                    box.ToFrame(f.X, f.Y, f.Width, f.Height,
                                out float fx, out float fy, out float fw, out float fh);
                    findings[i] = new ScreenFinding(f.Kind, f.Label, f.Score, fx, fy, fw, fh);
                }

                Console.WriteLine($"found  : {count}");
                for (int i = 0; i < count; i++)
                    Console.WriteLine($"   {findings[i]}");
            }

            Console.WriteLine();

            var locator = new RoiLocator(minScore: minScore);
            Console.WriteLine($"{locator}");
            Console.WriteLine();

            ChannelRoi roi = start;
            for (int look = 1; look <= looks; look++)
            {
                RoiLocatorDecision d = locator.Look(findings, count, roi, w, h, faultRunning: false);
                string moved = d.Moved ? $"  ->  {d.Roi}" : string.Empty;
                Console.WriteLine($"   look {look,2}: {d.Outcome,-12} overlap {d.Iou:F2}"
                                + (d.Streak > 0 ? $"  streak {d.Streak}" : string.Empty)
                                + moved
                                + (d.SizeChanged ? "   [SIZE CHANGED - thresholds no longer describe it]" : string.Empty));
                roi = d.Roi;
            }

            Console.WriteLine();
            Console.WriteLine($"region : {start}  ->  {roi}");
            Console.WriteLine($"         overlap with what was found: "
                            + $"{(count > 0 ? RoiLocator.Overlap(roi, Resolve(findings, count, w, h)) : 0.0):F2}");

            Draw(bgra, w, h, start, findings, count, roi, simulated);
            Encode(outPath, bgra, w, h);
            Console.WriteLine();
            Console.WriteLine($"wrote  : {outPath}");
            Console.WriteLine("         red = region before, green = what was found, blue = region after");
            return 0;
        }

        private static ChannelRoi Resolve(ScreenFinding[] f, int count, int w, int h)
        {
            ScreenFinding best = f[0];
            for (int i = 1; i < count; i++) if (f[i].Score > best.Score) best = f[i];
            return new ChannelRoi((int)best.X, (int)best.Y, (int)best.Width, (int)best.Height);
        }

        // ---- picture ---------------------------------------------------------------------

        /// <summary>`peek` needs the same decode, and a second copy would drift from this one.</summary>
        internal static byte[] DecodePublic(string path, out int width, out int height) =>
            Decode(path, out width, out height);

        /// <summary>Same reason as DecodePublic.</summary>
        internal static byte[] ToPlanarGbrpPublic(byte[] bgra, int w, int h) => ToPlanarGbrp(bgra, w, h);

        private static byte[] Decode(string path, out int width, out int height)
        {
            using FileStream fs = File.OpenRead(path);
            BitmapSource src = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            width = converted.PixelWidth;
            height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        private static void Encode(string path, byte[] bgra, int w, int h)
        {
            BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using FileStream fs = File.Create(path);
            encoder.Save(fs);
        }

        /// <summary>
        /// BGRA to the planar gbrp the letterbox reads - the same layout `FrameExtractor`
        /// produces. The plane order is G, B, R and getting it wrong is silent.
        /// </summary>
        private static byte[] ToPlanarGbrp(byte[] bgra, int w, int h)
        {
            int plane = w * h;
            var p = new byte[plane * 3];
            for (int i = 0; i < plane; i++)
            {
                p[i] = bgra[i * 4 + 1];              // G
                p[plane + i] = bgra[i * 4];          // B
                p[plane * 2 + i] = bgra[i * 4 + 2];  // R
            }
            return p;
        }

        private static void Draw(byte[] bgra, int w, int h, ChannelRoi before,
                                 ScreenFinding[] findings, int count, ChannelRoi after,
                                 bool simulated)
        {
            Rect(bgra, w, h, before.IsFullFrame ? new ChannelRoi(0, 0, w, h) : before, Before, 2);

            for (int i = 0; i < count; i++)
            {
                ScreenFinding f = findings[i];
                Rect(bgra, w, h, new ChannelRoi((int)f.X, (int)f.Y, (int)f.Width, (int)f.Height), Found, 2);
            }

            Rect(bgra, w, h, after.IsFullFrame ? new ChannelRoi(0, 0, w, h) : after, After, 3);

            if (simulated)
            {
                // A band of the "found" colour along the top, so a picture that left this program
                // with a simulated box in it cannot be mistaken for one the model produced.
                for (int y = 0; y < 8 && y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        bgra[i] = Found[0]; bgra[i + 1] = Found[1]; bgra[i + 2] = Found[2];
                    }
            }
        }

        /// <summary>`green` draws on the same pictures; a second copy would drift from this one.</summary>
        internal static void RectPublic(byte[] bgra, int w, int h, ChannelRoi r, byte[] colour, int thickness) =>
            Rect(bgra, w, h, r, colour, thickness);

        /// <summary>Same reason as RectPublic.</summary>
        internal static void EncodePublic(string path, byte[] bgra, int w, int h) => Encode(path, bgra, w, h);

        private static void Rect(byte[] bgra, int w, int h, ChannelRoi r, byte[] colour, int thickness)
        {
            for (int t = 0; t < thickness; t++)
            {
                HLine(bgra, w, h, r.OffsetX, r.OffsetX + r.Width, r.OffsetY + t, colour);
                HLine(bgra, w, h, r.OffsetX, r.OffsetX + r.Width, r.OffsetY + r.Height - 1 - t, colour);
                VLine(bgra, w, h, r.OffsetY, r.OffsetY + r.Height, r.OffsetX + t, colour);
                VLine(bgra, w, h, r.OffsetY, r.OffsetY + r.Height, r.OffsetX + r.Width - 1 - t, colour);
            }
        }

        private static void HLine(byte[] bgra, int w, int h, int x0, int x1, int y, byte[] c)
        {
            if (y < 0 || y >= h) return;
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = c[0]; bgra[i + 1] = c[1]; bgra[i + 2] = c[2];
            }
        }

        private static void VLine(byte[] bgra, int w, int h, int y0, int y1, int x, byte[] c)
        {
            if (x < 0 || x >= w) return;
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = c[0]; bgra[i + 1] = c[1]; bgra[i + 2] = c[2];
            }
        }

        // ---- arguments -------------------------------------------------------------------

        private static ChannelRoi? RoiArg(string[] args, string name)
        {
            string v = StringArg(args, name, null);
            if (v == null) return null;

            string[] parts = v.Split(',');
            if (parts.Length != 4) return null;
            var n = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]))
                    return null;
            return new ChannelRoi(n[0], n[1], n[2], n[3]);
        }

        private static string StringArg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }

        private static int IntArg(string[] args, string name, int fallback)
        {
            string v = StringArg(args, name, null);
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n : fallback;
        }

        private static double DoubleArg(string[] args, string name, double fallback)
        {
            string v = StringArg(args, name, null);
            return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                ? n : fallback;
        }
    }
}
