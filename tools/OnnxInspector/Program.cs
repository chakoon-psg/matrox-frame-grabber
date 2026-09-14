using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

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
                    "       OnnxInspector selftest <model.onnx>");
                return 1;
            }

            string verb = args[0];
            string model = args[1];
            if (!File.Exists(model))
            {
                Console.Error.WriteLine($"no such model: {model}");
                return 1;
            }

            switch (verb)
            {
                case "probe": return Probe(model);
                case "bench": return Bench(model, args);
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
