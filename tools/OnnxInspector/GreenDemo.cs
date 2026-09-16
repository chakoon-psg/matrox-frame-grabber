using System;
using System.Globalization;
using System.IO;
using MatroxFrameGrabber.Infrastructure;

namespace OnnxInspectorHarness
{
    /// <summary>
    /// Runs the model-free green screen finder over a real frame, prints what it found, and draws
    /// it - the same shape as `locate`, so the two answers can be held against each other on the
    /// same picture.
    ///
    /// This is where its thresholds were chosen. `--scan` walks the luma floor and prints the box
    /// at each step, which is the only honest way to pick that number: the panel and the light it
    /// throws on the room are both green, and where one ends and the other begins is a property of
    /// the rig rather than something to reason out.
    /// </summary>
    internal static class GreenDemo
    {
        private static readonly byte[] Found = { 80, 80, 255 };   // red, as the app draws it
        private static readonly byte[] Second = { 200, 160, 60 }; // blue for anything after the first

        public static int Run(string[] args)
        {
            string image = StringArg(args, "--image", null);
            if (image == null || !File.Exists(image))
            {
                Console.Error.WriteLine("green needs --image <frame.png>");
                return 1;
            }

            double share = DoubleArg(args, "--share", 0.42);
            int luma = IntArg(args, "--luma", 60);
            double minArea = DoubleArg(args, "--area", 0.004);
            bool scan = HasFlag(args, "--scan");

            byte[] bgra = LocateDemo.DecodePublic(image, out int w, out int h);
            byte[] planar = LocateDemo.ToPlanarGbrpPublic(bgra, w, h);

            // The app hands the inspector a letterboxed block, so this does too - a number measured
            // on the raw PNG would not be a number about the app.
            const int inW = 320, inH = 256;
            var box = new BgrLetterbox(w, h, inW, inH);
            byte[] bgr = box.Fill(planar, w, h, 0, 0);

            Console.WriteLine($"frame  : {Path.GetFileName(image)}  {w}x{h}");
            Console.WriteLine($"letterbox: {box}");
            Console.WriteLine();

            if (scan)
            {
                Console.WriteLine("luma floor scan - what the box does as the floor rises:");
                Console.WriteLine("  luma   mask px   blobs   box (frame pixels)                fill");
                foreach (int l in new[] { 0, 20, 30, 40, 50, 60, 70, 80, 100, 120, 150 })
                {
                    var f = new GreenScreenInspector(inW, inH, share, l, minArea);
                    var into = new ScreenFinding[8];
                    int n = f.Inspect(bgr, into);
                    string where = n > 0 ? Describe(box, into[0]) : "(nothing)";
                    string fill = n > 0 ? $"{into[0].Score:F2}" : "-";
                    Console.WriteLine($"  {l,4}  {f.LastMaskPixels,8}  {f.LastBlobs,6}   {where,-34} {fill}");
                }
                Console.WriteLine();
            }

            var finder = new GreenScreenInspector(inW, inH, share, luma, minArea);
            var findings = new ScreenFinding[8];
            int count = finder.Inspect(bgr, findings);

            Console.WriteLine($"finder : {finder.Describe}");
            Console.WriteLine($"mask   : {finder.LastMaskPixels} px of {inW * inH} "
                            + $"({finder.LastMaskPixels * 100.0 / (inW * inH):F1}%), {finder.LastBlobs} blob(s)");
            Console.WriteLine($"found  : {count}");
            for (int i = 0; i < count; i++)
                Console.WriteLine($"   {i + 1}. {Describe(box, findings[i])}  fill {findings[i].Score:F2}");

            string outPath = StringArg(args, "--out", null);
            if (outPath != null)
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    ScreenFinding f = findings[i];
                    box.ToFrame(f.X, f.Y, f.Width, f.Height,
                                out float fx, out float fy, out float fw, out float fh);
                    LocateDemo.RectPublic(bgra, w, h,
                        new ChannelRoi((int)fx, (int)fy, (int)fw, (int)fh),
                        i == 0 ? Found : Second, i == 0 ? 3 : 2);
                }
                LocateDemo.EncodePublic(outPath, bgra, w, h);
                Console.WriteLine($"wrote  : {outPath}  (red = biggest)");
            }
            return 0;
        }

        /// <summary>A finding in frame pixels, which is the space anyone reading this thinks in.</summary>
        private static string Describe(BgrLetterbox box, ScreenFinding f)
        {
            box.ToFrame(f.X, f.Y, f.Width, f.Height,
                        out float x, out float y, out float w, out float h);
            return $"{w:F0}x{h:F0} @ {x:F0},{y:F0}";
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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
