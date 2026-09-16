using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MatroxFrameGrabber.Infrastructure;

namespace OnnxInspectorHarness
{
    /// <summary>
    /// Prints the raw output cells for a real image, so what the graph says can be compared with
    /// what the decoder makes of it.
    ///
    /// **This exists because a decoded box can be wrong in a way that looks right.** `locate` on
    /// three green rectangles of 120x80, 220x150 and 406x275 gave boxes of 95x133, 94x118 and
    /// 90x127 - a box whose size does not move when the object triples is not a box, and no
    /// amount of staring at the picture says whether the model or the decode is at fault. The
    /// answer is in the five numbers at the peak cell, which nothing else prints.
    ///
    /// `selftest` does something similar but only for its own synthetic block, which the model
    /// barely responds to. This takes the frame the question is actually about.
    /// </summary>
    internal static class PeekDemo
    {
        public static int Run(string model, string[] args)
        {
            string image = StringArg(args, "--image", null);
            if (image == null || !File.Exists(image))
            {
                Console.Error.WriteLine("peek needs --image <frame.png>");
                return 1;
            }
            int top = IntArg(args, "--top", 3);

            using var session = new InferenceSession(model);
            KeyValuePair<string, NodeMetadata> input = session.InputMetadata.First();
            KeyValuePair<string, NodeMetadata> output = session.OutputMetadata.First();

            int inH = input.Value.Dimensions[1], inW = input.Value.Dimensions[2];
            int rows = output.Value.Dimensions[1], cols = output.Value.Dimensions[2];
            int ch = output.Value.Dimensions[3];
            float strideY = (float)inH / rows, strideX = (float)inW / cols;

            byte[] bgra = LocateDemo.DecodePublic(image, out int w, out int h);
            byte[] planar = LocateDemo.ToPlanarGbrpPublic(bgra, w, h);
            var box = new BgrLetterbox(w, h, inW, inH);
            byte[] bgr = box.Fill(planar, w, h, 0, 0);

            var tensor = new DenseTensor<byte>(bgr, new[] { 1, inH, inW, 3 });
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> res =
                session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(input.Key, tensor) });
            ReadOnlySpan<float> flat = ((DenseTensor<float>)res.First().AsTensor<float>()).Buffer.Span;

            Console.WriteLine($"image  : {Path.GetFileName(image)}  {w}x{h}");
            Console.WriteLine($"letterbox: {box}");
            Console.WriteLine($"grid   : {cols}x{rows}, stride {strideX}x{strideY}, {ch} channels");
            Console.WriteLine();

            // The top cells by channel 0, which selftest established is the heatmap.
            var order = new List<(float heat, int r, int c)>(rows * cols);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    order.Add((flat[(r * cols + c) * ch + 0], r, c));
            order.Sort((a, b) => b.heat.CompareTo(a.heat));

            Console.WriteLine("peak cells (channel 0 = heat, as logits):");
            for (int i = 0; i < top && i < order.Count; i++)
            {
                (float heat, int r, int c) = order[i];
                int at = (r * cols + c) * ch;
                Console.Write($"  #{i + 1} cell (row {r,2}, col {c,2}) score {Sigmoid(heat):F3} :");
                for (int k = 0; k < ch; k++) Console.Write($"  [{k}]={flat[at + k],8:F3}");
                Console.WriteLine();

                // Both readings of the pairs, in FRAME pixels, so they can be held against the
                // rectangle that is actually in the picture.
                Print("ours  [heat,h,w,offY,offX]", box, r, c, strideX, strideY,
                      offY: flat[at + 3], offX: flat[at + 4], sh: flat[at + 1], sw: flat[at + 2]);
                Print("swap  [heat,w,h,offX,offY]", box, r, c, strideX, strideY,
                      offY: flat[at + 4], offX: flat[at + 3], sh: flat[at + 2], sw: flat[at + 1]);
                Print("adr   [heat,offY,offX,h,w]", box, r, c, strideX, strideY,
                      offY: flat[at + 1], offX: flat[at + 2], sh: flat[at + 3], sw: flat[at + 4]);
                Print("exp   [heat,h,w,..] as log", box, r, c, strideX, strideY,
                      offY: flat[at + 3], offX: flat[at + 4],
                      sh: MathF.Exp(flat[at + 1]), sw: MathF.Exp(flat[at + 2]));
                Console.WriteLine();
            }

            // What the whole grid can express, which says whether a big box is even possible.
            Console.WriteLine("channel ranges over the grid:");
            for (int k = 0; k < ch; k++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        float v = flat[(r * cols + c) * ch + k];
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                Console.WriteLine($"  [{k}] {lo,8:F3} .. {hi,8:F3}");
            }
            return 0;
        }

        private static void Print(string name, BgrLetterbox box, int row, int col,
                                  float strideX, float strideY,
                                  float offY, float offX, float sh, float sw)
        {
            float cy = (row + offY) * strideY, cx = (col + offX) * strideX;
            float mh = sh * strideY, mw = sw * strideX;
            box.ToFrame(cx - mw / 2f, cy - mh / 2f, mw, mh,
                        out float x, out float y, out float fw, out float fh);
            Console.WriteLine($"      {name} -> frame {x,7:F1},{y,7:F1}  {fw,6:F1}x{fh,6:F1}");
        }

        private static float Sigmoid(float x) =>
            x >= 0f ? 1f / (1f + MathF.Exp(-x)) : MathF.Exp(x) / (1f + MathF.Exp(x));

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
    }
}
