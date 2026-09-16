using System;
using System.Collections.Generic;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Finds the DUT's green calibration screen by arithmetic, and hands back its rectangle.
    ///
    /// **Why this is not a model.** The question "where is the green rectangle" has an answer that
    /// can be computed and checked: a pixel is on the screen if green dominates its other two
    /// bands and it is bright enough to be emitted rather than reflected, and the screen is the
    /// largest connected run of those pixels. A learned detector was tried first and the weights
    /// that arrived find green *objects* - their size head stops growing at about 130x95 px, so a
    /// green screen filling 40% of the frame comes back as a box over one corner of it
    /// (`docs/cs-screen-inspector-handover.md` has the measurements). This class has no such
    /// ceiling: a component is as big as it is.
    ///
    /// It implements the same <see cref="IScreenInspector"/> contract, takes the same letterboxed
    /// interleaved BGR block, and returns boxes in the same space - so the locator, the overlay
    /// and the letterbox's inverse all work unchanged, and the two can be swapped by a setting.
    ///
    /// **Share, not level.** The test is `g / (r+g+b)`, which does not move when the panel is
    /// brighter or dimmer, so one threshold covers three cameras whose panels measured 116.9,
    /// 82.6 and 34.9 luma. A level test would need a number per camera and would drift with the
    /// backlight - the same reasoning `ColorShiftDetector` gives for judging band shares.
    ///
    /// **And a floor, because a lit screen lights the room.** The panel throws green onto the
    /// bezel, the keyboard and the desk, and those pixels pass the share test while being nothing
    /// anyone wants in a measurement region. <see cref="MinLuma"/> is what separates the emitter
    /// from what it illuminates; it is the one number here that had to be measured rather than
    /// reasoned (see the table in the handover document).
    ///
    /// Cost is one pass over the block plus a union-find over the pixels that passed: measured at
    /// 320x256 it is a fraction of a millisecond against the 8.5 ms an inference takes, and it
    /// allocates nothing per call.
    /// </summary>
    public sealed class GreenScreenInspector : IScreenInspector
    {
        /// <summary>Rec.601 weights as integers, the same ones <c>TileReducer</c> uses.</summary>
        private const int LumaR = 299, LumaG = 587, LumaB = 114, LumaScale = 1000;

        private readonly int _w, _h;
        private readonly int[] _label;          // per pixel, 0 = not on the mask
        private readonly List<Blob> _blobs = new List<Blob>(64);
        private int[] _parent;                  // union-find; grows, so not readonly
        private int _labels;

        private struct Blob
        {
            public int MinX, MinY, MaxX, MaxY, Area;
        }

        /// <param name="inputWidth">Width of the block this is handed. Fixed for the run.</param>
        /// <param name="inputHeight">Height of the block.</param>
        /// <param name="greenShare">
        /// Least `g / (r+g+b)` a pixel needs. A neutral pixel sits at 0.333 and the rig's green
        /// screens measured 0.48; the gap is wide, which is why one number works for three
        /// cameras.
        /// </param>
        /// <param name="minLuma">
        /// Least brightness a pixel needs. This is the emitter-versus-illuminated line: below it
        /// are the bezel, the keyboard and the desk, which are green because the screen is lit.
        /// </param>
        /// <param name="minAreaFraction">
        /// Smallest blob worth reporting, as a fraction of the block. Below this it is a reflection
        /// or a stray highlight, not a screen.
        /// </param>
        /// <param name="minFill">
        /// How much of its own bounding box a blob must fill to be called a screen.
        ///
        /// **This is the guard against content that merely happens to be green.** A map, a tree,
        /// a progress bar - green, and large enough to pass the area floor, but ragged, so they
        /// fill a fraction of the rectangle they span. A screen is a rectangle and fills nearly
        /// all of it: measured on the three cameras of this rig, 0.90, 0.94 and 0.90, against a
        /// floor of 0.7. Without this the region would follow the largest green thing rather than
        /// the largest green SCREEN, and the day the panel shows a map is the day it does that.
        /// </param>
        public GreenScreenInspector(int inputWidth = 320, int inputHeight = 256,
                                    double greenShare = 0.42, int minLuma = 60,
                                    double minAreaFraction = 0.004, double minFill = 0.70)
        {
            if (inputWidth <= 0 || inputHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(inputWidth));

            _w = inputWidth;
            _h = inputHeight;
            GreenShare = greenShare;
            MinLuma = minLuma < 0 ? 0 : minLuma;
            MinAreaFraction = minAreaFraction;
            MinFill = minFill;

            _label = new int[_w * _h];
            _parent = new int[(_w * _h / 4) + 16];
        }

        public double GreenShare { get; }
        public int MinLuma { get; }
        public double MinAreaFraction { get; }
        public double MinFill { get; }

        /// <summary>Pixels that passed the mask on the last call. For tuning and for the log.</summary>
        public int LastMaskPixels { get; private set; }

        /// <summary>Blobs found before the area filter. A jump here means the mask is too loose.</summary>
        public int LastBlobs { get; private set; }

        public string Describe =>
            $"green screen finder (no model), in {_w}x{_h}, share >= {GreenShare:0.##}, "
          + $"luma >= {MinLuma}, area >= {MinAreaFraction:P2}, fill >= {MinFill:0.##}";

        public int InputWidth => _w;
        public int InputHeight => _h;

        /// <summary>
        /// <see cref="AnomalyKind.Unknown"/>, the same as the learned inspector reports.
        ///
        /// It is not claiming a fault. What it finds is the target, and the app acts on that by
        /// moving the analysis region rather than by recording an anomaly.
        /// </summary>
        public AnomalyKind[] Reports { get; } = { AnomalyKind.Unknown };

        public int Inspect(byte[] bgr, Span<ScreenFinding> into)
        {
            if (bgr == null) throw new ArgumentNullException(nameof(bgr));
            int need = _w * _h * 3;
            if (bgr.Length < need)
                throw new ArgumentException($"need {need} bytes, got {bgr.Length}", nameof(bgr));

            LastMaskPixels = 0;
            _labels = 0;
            _blobs.Clear();

            LabelRuns(bgr);
            Merge();

            LastBlobs = _blobs.Count;
            if (_blobs.Count == 0 || into.Length == 0) return 0;

            int minArea = (int)Math.Max(1.0, _w * _h * MinAreaFraction);

            // Largest first: the screen is the biggest green thing in the picture, and a caller
            // that takes only the first finding should get it.
            _blobs.Sort((a, b) => b.Area.CompareTo(a.Area));

            int written = 0;
            for (int i = 0; i < _blobs.Count && written < into.Length; i++)
            {
                Blob b = _blobs[i];
                if (b.Area < minArea) break;      // sorted, so the rest are smaller too

                int w = b.MaxX - b.MinX + 1;
                int h = b.MaxY - b.MinY + 1;
                if (w <= 0 || h <= 0) continue;

                // How much of its own bounding box the blob fills. A screen is a rectangle, so
                // this is near 1; an L of light spilled across a desk is not, and this is what
                // tells them apart without another threshold.
                float fill = b.Area / (float)(w * h);
                if (fill > 1f) fill = 1f;
                if (fill < MinFill) continue;     // green, but not a rectangle - not a screen

                into[written++] = new ScreenFinding(AnomalyKind.Unknown, "green-screen", fill,
                                                   b.MinX, b.MinY, w, h);
            }
            return written;
        }

        /// <summary>
        /// One pass: mask each pixel, give it a provisional label from its left and upper
        /// neighbours, and record that the two labels are the same thing when they differ.
        ///
        /// Four-connectivity on purpose. Eight would bridge a screen to a highlight that only
        /// touches it at a corner, and a corner is exactly how a reflection touches.
        /// </summary>
        private void LabelRuns(byte[] bgr)
        {
            Array.Clear(_label, 0, _label.Length);

            for (int y = 0; y < _h; y++)
            {
                int row = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    int i = row + x;
                    int o = i * 3;
                    int b = bgr[o], g = bgr[o + 1], r = bgr[o + 2];

                    int luma = (LumaR * r + LumaG * g + LumaB * b) / LumaScale;
                    if (luma < MinLuma) continue;

                    int sum = r + g + b;
                    if (sum <= 0 || g < sum * GreenShare) continue;

                    LastMaskPixels++;

                    int left = x > 0 ? _label[i - 1] : 0;
                    int up = y > 0 ? _label[i - _w] : 0;

                    if (left == 0 && up == 0)
                    {
                        _label[i] = NewLabel();
                    }
                    else if (left != 0 && up != 0 && left != up)
                    {
                        _label[i] = left;
                        Union(left, up);
                    }
                    else
                    {
                        _label[i] = left != 0 ? left : up;
                    }
                }
            }
        }

        /// <summary>
        /// Second pass: resolve every provisional label to its root and accumulate one box per
        /// root. Done here rather than during labelling because a merge can happen after pixels
        /// have already been counted against the label that lost.
        /// </summary>
        private void Merge()
        {
            if (_labels == 0) return;

            var index = new int[_labels + 1];
            for (int i = 0; i < index.Length; i++) index[i] = -1;

            for (int y = 0; y < _h; y++)
            {
                int row = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    int l = _label[row + x];
                    if (l == 0) continue;

                    int root = Find(l);
                    int at = index[root];
                    if (at < 0)
                    {
                        at = _blobs.Count;
                        index[root] = at;
                        _blobs.Add(new Blob { MinX = x, MinY = y, MaxX = x, MaxY = y, Area = 1 });
                        continue;
                    }

                    Blob blob = _blobs[at];
                    if (x < blob.MinX) blob.MinX = x;
                    if (x > blob.MaxX) blob.MaxX = x;
                    if (y < blob.MinY) blob.MinY = y;
                    if (y > blob.MaxY) blob.MaxY = y;
                    blob.Area++;
                    _blobs[at] = blob;
                }
            }
        }

        private int NewLabel()
        {
            _labels++;
            if (_labels >= _parent.Length)
                Array.Resize(ref _parent, _parent.Length * 2);
            _parent[_labels] = _labels;
            return _labels;
        }

        private int Find(int a)
        {
            while (_parent[a] != a)
            {
                _parent[a] = _parent[_parent[a]];   // halve the path as we go
                a = _parent[a];
            }
            return a;
        }

        private void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (ra < rb) _parent[rb] = ra; else _parent[ra] = rb;
        }

        public void Dispose() { }

        public override string ToString() => Describe;
    }
}
