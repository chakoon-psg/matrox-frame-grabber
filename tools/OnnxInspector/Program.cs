using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MatroxFrameGrabber.Infrastructure;

namespace OnnxInspectorHarness
{
    /// <summary>
    /// A harness for the screen inspector, with no app and no MIL around it.
    ///
    /// `tools/MilVideoSink/` set the precedent: build the thing on its own, measure it there, and
    /// only then put it in the acquisition path. The numbers that decided the OpenCV question and
    /// the thread count came from a harness like this one, and until now that harness lived in a
    /// scratch directory and was thrown away - so the numbers in `docs/adr/0002` could not be
    /// reproduced by anyone. This one is in the repository.
    ///
    ///   OnnxInspector probe  &lt;model.onnx&gt;
    ///       what the graph says it takes and returns. Needed before a decoder can be written.
    ///
    ///   OnnxInspector bench  &lt;model.onnx&gt; [--threads N] [--iterations N] [--warmup N]
    ///       inference latency. Run it beside a grab to get the number that matters.
    ///
    ///   OnnxInspector locator &lt;model.onnx&gt; [--threads N] [--iterations N] [--warmup N]
    ///                         [--frame WxH] [--region WxH]
    ///       end to end: letterbox out of a planar frame, run the graph, decode - which is
    ///       exactly what `CameraChannel.Inspect` does. `bench` answers how fast the graph is;
    ///       this answers how many times a second a channel could be inspected, which is the
    ///       question a patrol rate is set from. With no geometry given it walks the three the
    ///       app can actually hand it (ROI crop, full frame at decimation 2, full frame at 1).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine(
                    "usage: OnnxInspector probe <model.onnx>\n" +
                    "       OnnxInspector bench <model.onnx> [--threads N] [--iterations N] [--warmup N]\n" +
                    "       OnnxInspector locator <model.onnx> [--threads N] [--iterations N] [--warmup N]\n" +
                    "                             [--frame WxH] [--region WxH]\n" +
                    "       OnnxInspector locate <model.onnx> --image <frame.png> [--roi x,y,w,h]\n" +
                    "                            [--simulate x,y,w,h] [--looks N] [--out out.png]\n" +
                    "       OnnxInspector selftest <model.onnx>");
                return 1;
            }

            string verb = args[0];
            string model = args[1];

            // `green` carries no model - it is the model-free finder - so the file check that
            // every other verb needs would refuse it for the wrong reason.
            if (string.Equals(verb, "green", StringComparison.Ordinal))
                return GreenDemo.Run(args);

            if (!File.Exists(model))
            {
                Console.Error.WriteLine($"no such model: {model}");
                return 1;
            }

            switch (verb)
            {
                case "probe": return Probe(model);
                case "bench": return Bench(model, args);
                case "locator": return Locator(model, args);
                case "locate": return LocateDemo.Run(model, args);
                case "peek": return PeekDemo.Run(model, args);
                case "green": return GreenDemo.Run(args);
                case "selftest": return SelfTest(model);
                default:
                    Console.Error.WriteLine($"unknown verb: {verb}");
                    return 1;
            }
        }

        /// <summary>
        /// Prints what the graph actually declares.
        ///
        /// This exists because the file name lied once: a model called `192` took 256x320. The
        /// cs project caught that with a runtime check against the graph, and that check is the
        /// part of their code worth keeping verbatim.
        /// </summary>
        private static int Probe(string model)
        {
            using var session = new InferenceSession(model);

            Console.WriteLine($"model : {Path.GetFileName(model)}");
            Console.WriteLine($"size  : {new FileInfo(model).Length / 1048576.0:F1} MB");

            Console.WriteLine("inputs:");
            foreach (KeyValuePair<string, NodeMetadata> i in session.InputMetadata)
                Console.WriteLine($"  {i.Key,-16} {i.Value.ElementType.Name,-8} [{Shape(i.Value)}]");

            Console.WriteLine("outputs:");
            foreach (KeyValuePair<string, NodeMetadata> o in session.OutputMetadata)
                Console.WriteLine($"  {o.Key,-16} {o.Value.ElementType.Name,-8} [{Shape(o.Value)}]");

            if (session.ModelMetadata != null)
            {
                ModelMetadata m = session.ModelMetadata;
                Console.WriteLine($"producer: {m.ProducerName} / graph {m.GraphName} / v{m.Version}");
                foreach (KeyValuePair<string, string> kv in m.CustomMetadataMap)
                    Console.WriteLine($"  meta {kv.Key} = {kv.Value}");
            }

            return 0;
        }

        private static string Shape(NodeMetadata m) =>
            string.Join(",", m.Dimensions.Select(d => d < 0 ? "?" : d.ToString(CultureInfo.InvariantCulture)));

        /// <summary>
        /// Times inference on a synthetic frame.
        ///
        /// The input is noise rather than a picture on purpose: this measures the cost of running
        /// the graph, which does not depend on what is in the tensor, and a picture would invite
        /// reading the findings as if they meant something.
        ///
        /// **Run it beside a real grab.** The published 2.77 ms was an idle machine; what decides
        /// whether this can be adopted is the number with three channels acquiring, and the frames
        /// the app reports missing while it runs.
        /// </summary>
        private static int Bench(string model, string[] args)
        {
            int threads = IntArg(args, "--threads", 0);
            int iterations = IntArg(args, "--iterations", 300);
            int warmup = IntArg(args, "--warmup", 30);

            var options = new SessionOptions();
            if (threads > 0) options.IntraOpNumThreads = threads;
            // The spin the thread pool does by default burns a core between calls, which is the
            // same finding the cs project recorded as section 6.5.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

            using var session = new InferenceSession(model, options);

            KeyValuePair<string, NodeMetadata> input = session.InputMetadata.First();
            int[] dims = input.Value.Dimensions.Select(d => d < 0 ? 1 : d).ToArray();
            int count = dims.Aggregate(1, (a, b) => a * b);

            Console.WriteLine($"input {input.Key} [{string.Join(",", dims)}] "
                            + $"{input.Value.ElementType.Name}, threads={(threads > 0 ? threads.ToString() : "default")}");

            var rng = new Random(1);
            var bytes = new byte[count];
            rng.NextBytes(bytes);
            var tensor = new DenseTensor<byte>(bytes, dims);
            var feed = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(input.Key, tensor) };

            for (int i = 0; i < warmup; i++)
                using (session.Run(feed)) { }

            var ms = new double[iterations];
            var sw = new Stopwatch();
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                using (session.Run(feed)) { }
                sw.Stop();
                ms[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(ms);
            Console.WriteLine($"n={iterations}  median={Pct(ms, 0.50):F3}  p90={Pct(ms, 0.90):F3}  "
                            + $"p99={Pct(ms, 0.99):F3}  max={ms[ms.Length - 1]:F3} ms  "
                            + $"({1000.0 / Pct(ms, 0.50):F0}/s at the median)");
            return 0;
        }


        /// <summary>
        /// Times one whole inspection - letterbox, graph, decode - for the geometries the app
        /// can hand it, and prints how many of those fit in a second.
        ///
        /// **Why this is not `bench`.** `bench` feeds the graph a tensor that is already the right
        /// shape, so it measures the graph. What decides a patrol rate is the whole call:
        /// `CameraChannel.Inspect` fills the letterbox out of the shared planar frame first, and
        /// that cost depends on which geometry the region has. Measured 2026-09-16, the letterbox
        /// is 0.4 ms for an ROI crop and 1.5 ms for a full frame beside a grab - small beside the
        /// inference, but not nothing, and the ratio is not the ratio of the pixel counts (12.5x
        /// of them, 2.9x of the time: the crop is an upscale, so its loop reads one source pixel
        /// per destination pixel and pays loop overhead instead of bandwidth).
        ///
        /// **Run it beside a grab.** Idle numbers are about 2.4x optimistic here - the same trap
        /// the published 2.77 ms was. And check what the app reports missing afterwards: on
        /// 2026-09-16 a control run with this harness switched off lost *more* frames than the
        /// run beside it, which is how "the inspector does not eat frames" was established rather
        /// than assumed.
        ///
        /// The frame is synthetic. A graph's timing does not depend on what is in the tensor, and
        /// a picture would invite reading the findings as if they meant something.
        /// </summary>
        private static int Locator(string model, string[] args)
        {
            int threads = IntArg(args, "--threads", 0);
            int iterations = IntArg(args, "--iterations", 200);
            int warmup = IntArg(args, "--warmup", 20);

            using IScreenInspector inspector =
                new MatroxFrameGrabber.Onnx.OnnxScreenInspector(model, 0.3f, 0.5f, 16, threads);

            Console.WriteLine($"inspector : {inspector.Describe}");
            Console.WriteLine($"iterations: {iterations} (warmup {warmup})");
            Console.WriteLine();

            bool haveFrame = TrySize(args, "--frame", out int fw, out int fh);
            bool haveRegion = TrySize(args, "--region", out int rw, out int rh);

            if (haveFrame || haveRegion)
            {
                if (!haveFrame) { fw = rw; fh = rh; }
                if (!haveRegion) { rw = fw; rh = fh; }
                if (rw > fw || rh > fh)
                {
                    Console.Error.WriteLine($"region {rw}x{rh} does not fit in frame {fw}x{fh}");
                    return 1;
                }
                LocatorCase(inspector, "custom", fw, fh, rw, rh, iterations, warmup);
                return 0;
            }

            // The three the app can actually produce. The ROI is one of the rig's measured ones
            // (Camera 0, 284x176) rather than a round number, because the point of the first row
            // is that the current wiring UPSCALES - the region is smaller than the model input.
            LocatorCase(inspector, "A  ROI crop, decim 2   (current wiring)", 1024, 772, 284, 176, iterations, warmup);
            LocatorCase(inspector, "B  full frame, decim 2 (locator)       ", 1024, 772, 1024, 772, iterations, warmup);
            LocatorCase(inspector, "C  full frame, decim 1 (locator)       ", 2048, 1544, 2048, 1544, iterations, warmup);
            return 0;
        }

        private static void LocatorCase(IScreenInspector inspector, string name,
                                        int frameW, int frameH, int roiW, int roiH,
                                        int iterations, int warmup)
        {
            byte[] planar = SyntheticFrame(frameW, frameH, roiW, roiH, out int roiX, out int roiY);
            var box = new BgrLetterbox(roiW, roiH, inspector.InputWidth, inspector.InputHeight);
            var findings = new ScreenFinding[16];

            for (int i = 0; i < warmup; i++)
                inspector.Inspect(box.Fill(planar, frameW, frameH, roiX, roiY), findings);

            var fill = new double[iterations];
            var infer = new double[iterations];
            var total = new double[iterations];
            var sw = new Stopwatch();
            int found = 0;

            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                byte[] bgr = box.Fill(planar, frameW, frameH, roiX, roiY);
                sw.Stop();
                fill[i] = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                found = inspector.Inspect(bgr, findings);
                sw.Stop();
                infer[i] = sw.Elapsed.TotalMilliseconds;

                total[i] = fill[i] + infer[i];
            }

            Array.Sort(fill);
            Array.Sort(infer);
            Array.Sort(total);
            double median = Pct(total, 0.50);

            Console.WriteLine($"{name}   region {roiW}x{roiH} in {frameW}x{frameH}  ->  {box}");
            Console.WriteLine($"   letterbox  median {Pct(fill, 0.50),7:F3}  p90 {Pct(fill, 0.90),7:F3}  max {fill[fill.Length - 1],7:F3} ms");
            Console.WriteLine($"   inspect    median {Pct(infer, 0.50),7:F3}  p90 {Pct(infer, 0.90),7:F3}  max {infer[infer.Length - 1],7:F3} ms");
            Console.WriteLine($"   TOTAL      median {median,7:F3}  p90 {Pct(total, 0.90),7:F3}  max {total[total.Length - 1],7:F3} ms");
            Console.WriteLine($"   -> {1000.0 / median:F0} inspections/s for one channel; "
                            + $"a round-robin patrol at 2/s would use {2.0 * median / 10.0:F1}% of that");
            Console.WriteLine($"   findings: {found}");
            Console.WriteLine();
        }

        /// <summary>
        /// A planar gbrp frame - the layout `FrameExtractor` produces - with a brighter rectangle
        /// where the panel would be.
        ///
        /// Noise rather than flat fills, so a resize that silently did nothing would not produce
        /// the same numbers as one that worked.
        /// </summary>
        private static byte[] SyntheticFrame(int w, int h, int roiW, int roiH,
                                             out int roiX, out int roiY)
        {
            roiX = ((w - roiW) / 2) & ~1;
            roiY = ((h - roiH) / 2) & ~1;

            int plane = w * h;
            var b = new byte[plane * 3];
            var rng = new Random(7);

            for (int i = 0; i < plane; i++)
            {
                byte n = (byte)rng.Next(6, 14);
                b[i] = n;                  // G
                b[plane + i] = n;          // B
                b[plane * 2 + i] = n;      // R
            }

            for (int y = roiY; y < roiY + roiH && y < h; y++)
                for (int x = roiX; x < roiX + roiW && x < w; x++)
                {
                    int i = y * w + x;
                    b[i] = (byte)rng.Next(180, 230);
                    b[plane + i] = (byte)rng.Next(30, 60);
                    b[plane * 2 + i] = (byte)rng.Next(30, 60);
                }

            return b;
        }

        /// <summary>Reads <c>--frame 1024x772</c>. Anything unparseable counts as absent.</summary>
        private static bool TrySize(string[] args, string name, out int w, out int h)
        {
            w = h = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = args[i + 1].Split('x', 'X');
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
                    && w > 0 && h > 0)
                    return true;

                w = h = 0;
                return false;
            }
            return false;
        }

        /// <summary>
        /// Puts a bright green rectangle at a known place and reports where the heatmap peaks.
        ///
        /// This is the only check on the output layout that can be made without an image whose
        /// ground truth we know. It can confirm **which channel is the heatmap** - the peak has
        /// to land on the rectangle - and it shows the magnitudes of the other four, which says
        /// whether they are offsets in cells and sizes in cells rather than pixels or fractions.
        ///
        /// It cannot confirm the order within the offset pair or the size pair. That needs a
        /// labelled box, which needs a model trained on our problem.
        /// </summary>
        private static int SelfTest(string model)
        {
            using var session = new InferenceSession(model);
            KeyValuePair<string, NodeMetadata> input = session.InputMetadata.First();
            KeyValuePair<string, NodeMetadata> output = session.OutputMetadata.First();

            int h = input.Value.Dimensions[1], w = input.Value.Dimensions[2];
            int rows = output.Value.Dimensions[1], cols = output.Value.Dimensions[2];
            int ch = output.Value.Dimensions[3];
            float strideY = (float)h / rows, strideX = (float)w / cols;

            // A rectangle in the lower right quadrant, so a transposed or flipped read is obvious.
            int rx = (int)(w * 0.55), ry = (int)(h * 0.55);
            int rw = (int)(w * 0.20), rh = (int)(h * 0.20);

            var pixels = new byte[w * h * 3];
            for (int y = ry; y < ry + rh; y++)
                for (int x = rx; x < rx + rw; x++)
                {
                    int i = (y * w + x) * 3;
                    pixels[i] = 40;        // B
                    pixels[i + 1] = 230;   // G
                    pixels[i + 2] = 40;    // R
                }

            var tensor = new DenseTensor<byte>(pixels, new[] { 1, h, w, 3 });
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> res =
                session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(input.Key, tensor) });
            ReadOnlySpan<float> flat = ((DenseTensor<float>)res.First().AsTensor<float>()).Buffer.Span;

            Console.WriteLine($"rectangle at {rx},{ry} {rw}x{rh} in {w}x{h}");
            Console.WriteLine($"  expected grid cell about ({(ry + rh / 2) / strideY:F1} row, "
                            + $"{(rx + rw / 2) / strideX:F1} col) of {rows}x{cols}");

            int bestRow = -1, bestCol = -1;
            float best = float.NegativeInfinity;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    float v = flat[(r * cols + c) * ch + 0];
                    if (v > best) { best = v; bestRow = r; bestCol = c; }
                }

            int at = (bestRow * cols + bestCol) * ch;
            Console.WriteLine($"  channel 0 peaks at (row {bestRow}, col {bestCol}) "
                            + $"logit {best:F3} -> score {Sigmoid(best):F3}");
            Console.Write("  channels there:");
            for (int k = 0; k < ch; k++) Console.Write($"  [{k}]={flat[at + k]:F3}");
            Console.WriteLine();

            // Per-channel statistics over the whole grid. This is the strong evidence about
            // layout, and it does not depend on the model actually responding: an offset head is
            // trained to emit a fraction of a cell and a size head emits a count of cells, so
            // their ranges are different by construction.
            Console.WriteLine("  channel ranges over the whole grid:");
            for (int k = 0; k < ch; k++)
            {
                float lo = float.MaxValue, hi = float.MinValue, sum = 0f;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        float v = flat[(r * cols + c) * ch + k];
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                        sum += v;
                    }
                float mean = sum / (rows * cols);
                string guess = (lo >= -0.01f && hi <= 1.01f) ? "  <- fraction of a cell (offset?)"
                             : (lo >= -0.01f && hi < 64f) ? "  <- count of cells (size?)"
                             : "  <- signed and wide (logit?)";
                Console.WriteLine($"    [{k}] min {lo,8:F3}  max {hi,8:F3}  mean {mean,8:F3}{guess}");
            }

            bool onTarget = Math.Abs(bestRow - (ry + rh / 2f) / strideY) <= 2f
                         && Math.Abs(bestCol - (rx + rw / 2f) / strideX) <= 2f;
            // Two separate questions, and conflating them made the first version of this
            // message misleading. Whether channel 0 is the heatmap is answered by its range;
            // whether the model found the rectangle is answered by the peak's value.
            bool looksLikeHeat = best < 0f;      // a heatmap that found nothing is all negative
            bool responded = Sigmoid(best) >= 0.3f;

            Console.WriteLine(looksLikeHeat
                ? "  channel 0 ranges like a heatmap (all negative here = found nothing)"
                : "  channel 0 does NOT range like a heatmap - check the layout");
            Console.WriteLine(responded
                ? (onTarget ? "  and it responded on the rectangle"
                            : "  and it responded, but NOT on the rectangle")
                : $"  and it did not respond to a synthetic block (best score {Sigmoid(best):F3}) "
                + "- expected, since this model was trained on photographs");
            return 0;
        }

        private static float Sigmoid(float x) =>
            x >= 0f ? 1f / (1f + MathF.Exp(-x)) : MathF.Exp(x) / (1f + MathF.Exp(x));

        private static double Pct(double[] sorted, double p) =>
            sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];

        private static int IntArg(string[] args, string name, int fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                    out int v))
                    return v;
            return fallback;
        }
    }
}
