using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Which family of backlight PWM frequencies the sweep points to.</summary>
    public enum PwmFamily
    {
        /// <summary>Ripple fell away at an exposure that nulls multiples of 120 Hz.</summary>
        Hz120,
        /// <summary>Ripple fell away at an exposure that nulls multiples of 100 Hz.</summary>
        Hz100,
        /// <summary>Ripple stayed at every exposure tried — not a 100/120 Hz family, or DC dimming.</summary>
        None,
        /// <summary>No ripple even at the reference exposure — no PWM, or the frame was saturated.</summary>
        NoRipple
    }

    /// <summary>One exposure held for a while, and the spread of brightness seen during it.</summary>
    public readonly struct PwmPoint
    {
        /// <summary>"panel" or "room" — which half of the procedure this came from.</summary>
        public readonly string Label;
        public readonly int ExposureUs;
        public readonly float MinLuma;
        public readonly float MaxLuma;
        public readonly float MeanLuma;
        /// <summary>Worst clipped% seen. Any clipping makes the ripple figure untrustworthy.</summary>
        public readonly float MaxClippedPct;
        public readonly int Samples;

        /// <summary>
        /// The camera's own <c>ResultingFrameRate</c> at this exposure, or 0 when it does not have
        /// the feature. Two jobs: it confirms the exposure actually took (the rate moves with it),
        /// and it is the only direct answer to what this camera's rate ceiling really is at the
        /// current decimation — the 184 in the model name is the full-resolution figure.
        /// </summary>
        public readonly double ResultingFps;

        public PwmPoint(string label, int exposureUs, float minLuma, float maxLuma,
                        float meanLuma, float maxClippedPct, int samples, double resultingFps = 0)
        {
            Label = label;
            ExposureUs = exposureUs;
            MinLuma = minLuma;
            MaxLuma = maxLuma;
            MeanLuma = meanLuma;
            MaxClippedPct = maxClippedPct;
            Samples = samples;
            ResultingFps = resultingFps;
        }

        /// <summary>Ripple as a percentage of the mean — the figure the whole procedure turns on.</summary>
        public double RipplePercent => PwmSweep.RipplePercent(MinLuma, MaxLuma);

        public string ToCsvLine() => string.Join(",",
            Label,
            ExposureUs.ToString(CultureInfo.InvariantCulture),
            MinLuma.ToString("0.###", CultureInfo.InvariantCulture),
            MaxLuma.ToString("0.###", CultureInfo.InvariantCulture),
            MeanLuma.ToString("0.###", CultureInfo.InvariantCulture),
            MaxClippedPct.ToString("0.###", CultureInfo.InvariantCulture),
            Samples.ToString(CultureInfo.InvariantCulture),
            ResultingFps.ToString("0.###", CultureInfo.InvariantCulture));

        public static bool TryParseCsvLine(string line, out PwmPoint point)
        {
            point = default;
            if (string.IsNullOrWhiteSpace(line)) return false;

            string[] f = line.Split(',');
            if (f.Length < 7) return false;   // the 8th (resulting fps) is optional: files predate it

            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int us)) return false;
            if (!float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float min)) return false;
            if (!float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float max)) return false;
            if (!float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float mean)) return false;
            if (!float.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float clip)) return false;
            if (!int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return false;

            double fps = 0;
            if (f.Length >= 8)
                double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out fps);

            point = new PwmPoint(f[0], us, min, max, mean, clip, n, fps);
            return true;
        }
    }

    /// <summary>What the sweep concluded, and the operating point that follows from it.</summary>
    public readonly struct PwmVerdict
    {
        public readonly PwmFamily Family;
        public readonly int ExposureUs;
        public readonly double TargetFps;
        /// <summary>True when a point was clipped, which can fake a null. The verdict is then suspect.</summary>
        public readonly bool Clipped;

        public PwmVerdict(PwmFamily family, int exposureUs, double targetFps, bool clipped)
        {
            Family = family;
            ExposureUs = exposureUs;
            TargetFps = targetFps;
            Clipped = clipped;
        }
    }

    /// <summary>
    /// The arithmetic behind the backlight PWM exposure sweep.
    ///
    /// Exposure is a box integral, so a frequency f passes through it scaled by |sinc(pi*f*T)| and
    /// is nulled exactly when f*T is a whole number. So the exposure at which brightness ripple
    /// collapses is a whole multiple of the PWM period, and the frequency family follows from it.
    ///
    /// Kept free of MIL and WPF: this is the part worth testing, and the layer that can be.
    /// </summary>
    public static class PwmSweep
    {
        /// <summary>
        /// Readout overhead between exposures, from the camera's own max-rate relation
        /// (max fps = 1/(exposure + 45 us)). It is also the only interval the sensor is blind.
        /// </summary>
        public const double InterFrameOverheadUs = 45.0;

        /// <summary>
        /// 6500 us first as the reference: it is a whole multiple of 153.8 Hz, so it nulls none of
        /// the common PWM frequencies and shows what "ripple present" looks like on this panel.
        /// Then 5000 and 10000 (both null multiples of 100 Hz) and 8333 (nulls multiples of 120 Hz).
        /// Two points for the 100 Hz family so a verdict there rests on two agreeing measurements.
        /// </summary>
        public static readonly int[] PanelExposuresUs = { 6500, 5000, 8333, 10000 };

        /// <summary>
        /// Room lighting with the panel off: the reference, and the exposure that nulls 120 Hz.
        /// Korean mains is 60 Hz, so its light ripples at 120 Hz — if it shows up at 6500 and is
        /// gone at 8333, that identifies it and no blackout curtain is needed.
        /// </summary>
        public static readonly int[] RoomExposuresUs = { 6500, 8333 };

        /// <summary>
        /// The scan: every exposure from 4000 to 11000 us in 250 us steps, 29 points.
        ///
        /// The four-point sweep tests two hypotheses — is this a 100 Hz family or a 120 Hz one —
        /// and has nothing to say about any other answer. That design came from a person reading a
        /// number off the screen for fifteen seconds at a time, where six points was already
        /// tedious. Automated it is 20 s a point, so measuring the curve and reading the nulls off
        /// it costs about ten minutes and answers for any frequency instead of two.
        ///
        /// 250 us resolves nulls down to a period of about 2000 us, which is 500 Hz. Above that the
        /// nulls crowd together faster than the scan can separate them — but they stop mattering
        /// at the same time: |sinc(pi*f*T)| falls as 1/(pi*f*T), so by 2 kHz any exposure in this
        /// range already suppresses the ripple to under 3% of its raw amplitude. The resolution
        /// runs out exactly where the problem does.
        /// </summary>
        public static int[] ScanExposuresUs
        {
            get
            {
                var list = new List<int>();
                for (int us = 4000; us <= 11000; us += 250) list.Add(us);
                return list.ToArray();
            }
        }

        /// <summary>A point is a null when its ripple is under a third of the reference's.</summary>
        public const double NullFraction = 1.0 / 3.0;

        /// <summary>Below this the reference itself is flat, so there is nothing to null.</summary>
        public const double NoRippleThresholdPercent = 1.0;

        /// <summary>Peak-to-peak spread as a percentage of the mean. Zero when there is no light.</summary>
        public static double RipplePercent(double min, double max)
        {
            double mean = (min + max) / 2.0;
            if (mean <= 0) return 0.0;
            return Math.Abs(max - min) / mean * 100.0;
        }

        /// <summary>
        /// The acquisition rate an exposure allows, with no dead time between frames.
        ///
        /// Deriving fps from exposure rather than choosing it is deliberate. Capping the rate below
        /// what the exposure allows opens a blind gap between frames — at 5000 us held to 184 fps
        /// that gap is 435 us, 8% of the time, and an anomaly landing inside it leaves no trace.
        /// Letting the exposure set the rate keeps the gap at the 45 us the sensor cannot avoid.
        /// </summary>
        public static double FpsForExposure(int exposureUs) =>
            exposureUs <= 0 ? 0.0 : 1e6 / (exposureUs + InterFrameOverheadUs);

        /// <summary>
        /// Reads the verdict off the panel points. <paramref name="points"/> may hold room points
        /// too; they are ignored here.
        /// </summary>
        public static PwmVerdict Judge(IReadOnlyList<PwmPoint> points)
        {
            bool clipped = false;
            double refRipple = -1;
            double r5000 = double.MaxValue, r8333 = double.MaxValue, r10000 = double.MaxValue;

            for (int i = 0; i < (points?.Count ?? 0); i++)
            {
                PwmPoint p = points[i];
                if (!string.Equals(p.Label, "panel", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (p.MaxClippedPct > 0) clipped = true;

                switch (p.ExposureUs)
                {
                    case 6500: refRipple = p.RipplePercent; break;
                    case 5000: r5000 = p.RipplePercent; break;
                    case 8333: r8333 = p.RipplePercent; break;
                    case 10000: r10000 = p.RipplePercent; break;
                }
            }

            if (refRipple < 0)
                return new PwmVerdict(PwmFamily.None, 8333, FpsForExposure(8333), clipped);

            // A flat reference means there was nothing to null — no PWM, or a saturated frame whose
            // ripple was cut off at 255. Recommending an exposure from that would be inventing one.
            if (refRipple < NoRippleThresholdPercent)
                return new PwmVerdict(PwmFamily.NoRipple, 8333, FpsForExposure(8333), clipped);

            double limit = refRipple * NullFraction;

            // Lowest wins. The 100 Hz family is tested at two exposures and the 120 Hz family at
            // one, so a tie goes to 100 Hz — the side with the corroborating second point.
            double best100 = Math.Min(r5000, r10000);
            if (r8333 < best100 && r8333 <= limit)
                return new PwmVerdict(PwmFamily.Hz120, 8333, FpsForExposure(8333), clipped);
            if (best100 <= limit)
                // 10000 over 5000 where both null: the longer exposure gathers more light, and
                // light is what makes the reading precise.
                return new PwmVerdict(PwmFamily.Hz100, 10000, FpsForExposure(10000), clipped);

            return new PwmVerdict(PwmFamily.None, 8333, FpsForExposure(8333), clipped);
        }

        /// <summary>
        /// Whether room lighting was seen, and whether 8333 us nulls it — which would identify it
        /// as the 120 Hz ripple of Korean mains.
        /// </summary>
        public static string RoomNote(IReadOnlyList<PwmPoint> points)
        {
            double refRipple = -1, nulled = -1;
            for (int i = 0; i < (points?.Count ?? 0); i++)
            {
                PwmPoint p = points[i];
                if (!string.Equals(p.Label, "room", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p.ExposureUs == 6500) refRipple = p.RipplePercent;
                if (p.ExposureUs == 8333) nulled = p.RipplePercent;
            }

            if (refRipple < 0) return "실내 조명은 측정하지 않았습니다.";
            if (refRipple < NoRippleThresholdPercent)
                return "실내 조명 리플은 검출되지 않았습니다 — 차광이 필요하지 않습니다.";
            if (nulled >= 0 && nulled < refRipple * NullFraction)
                return "실내 조명 리플이 8333 µs에서 소거되었습니다 — 120 Hz로 확인됩니다.";
            return "실내 조명 리플이 있고 8333 µs에서도 남습니다 — 120 Hz가 아닙니다. 차광이 필요합니다.";
        }

        // ----- Reading the scan curve -----

        /// <summary>An exposure where the ripple collapsed — a whole multiple of the PWM period.</summary>
        public readonly struct PwmNull
        {
            /// <summary>The scan point that came out lowest — a multiple of the 250 us step.</summary>
            public readonly int ExposureUs;
            public readonly double RipplePercent;

            /// <summary>
            /// Where the null really sits, interpolated between the grid points around it.
            ///
            /// This matters more than it looks. Taking the grid point alone reads a 240 Hz backlight
            /// as 250 Hz, and the exposure computed from 250 Hz misses the real null by 333 us —
            /// so the ripple would not cancel at the very exposure the procedure recommends.
            ///
            /// |sinc| approaches a zero linearly from both sides, so the curve near a null is a V,
            /// not a parabola. For a symmetric V the offset from the middle sample is
            /// <c>h * (left - right) / (left + right)</c>, which is exact when the two slopes match.
            /// </summary>
            public readonly double RefinedExposureUs;

            public PwmNull(int exposureUs, double ripplePercent, double refinedExposureUs)
            {
                ExposureUs = exposureUs;
                RipplePercent = ripplePercent;
                RefinedExposureUs = refinedExposureUs;
            }
        }

        /// <summary>
        /// The exposures where the ripple fell away, in ascending order. A point qualifies when it
        /// is no higher than both neighbours and under <see cref="NullFraction"/> of the scan's
        /// worst ripple — the worst point stands in for "PWM fully visible", the way the 6500 us
        /// reference does in the four-point sweep.
        /// </summary>
        public static List<PwmNull> FindNulls(IReadOnlyList<PwmPoint> points, string label = "panel")
        {
            var scan = new List<PwmPoint>();
            for (int i = 0; i < (points?.Count ?? 0); i++)
                if (string.Equals(points[i].Label, label, StringComparison.OrdinalIgnoreCase))
                    scan.Add(points[i]);

            var found = new List<PwmNull>();
            scan.Sort((a, b) => a.ExposureUs.CompareTo(b.ExposureUs));

            // Only a real scan. The four-point sweep also has a lowest point, and calling that a
            // null would let the report announce a frequency read off four unevenly spaced samples
            // — which is exactly the over-reach the scan mode exists to replace. Both the
            // interpolation and the "which multiples would the scan have caught" argument assume an
            // even grid, so neither is available here.
            if (!IsEvenGrid(scan)) return found;

            double worst = 0;
            foreach (PwmPoint p in scan)
                if (p.RipplePercent > worst) worst = p.RipplePercent;

            // A flat curve has no nulls to find — there was no ripple to null in the first place.
            if (worst < NoRippleThresholdPercent) return found;

            double limit = worst * NullFraction;

            for (int i = 1; i < scan.Count - 1; i++)
            {
                double r = scan[i].RipplePercent;
                if (r > limit) continue;
                if (r > scan[i - 1].RipplePercent || r > scan[i + 1].RipplePercent) continue;

                // A flat-bottomed valley qualifies once, at its first sample, rather than reporting
                // every point across the bottom as its own null and halving the inferred period.
                if (found.Count > 0 && scan[i].ExposureUs - found[found.Count - 1].ExposureUs <= 250)
                    continue;

                double left = scan[i - 1].RipplePercent;
                double right = scan[i + 1].RipplePercent;
                double h = (scan[i + 1].ExposureUs - scan[i - 1].ExposureUs) / 2.0;
                double sum = left + right;
                double refined = sum > 0
                    ? scan[i].ExposureUs + h * (left - right) / sum
                    : scan[i].ExposureUs;

                found.Add(new PwmNull(scan[i].ExposureUs, r, refined));
            }
            return found;
        }

        /// <summary>
        /// The PWM frequency the nulls imply, or 0 when the scan cannot say.
        ///
        /// Nulls sit at whole multiples of the period, so the spacing between neighbouring nulls is
        /// the period itself and needs no guess about which multiple each one is.
        ///
        /// A single null can often still be resolved. It looks ambiguous — T is consistent with
        /// k/f for every k — but a larger k implies neighbouring nulls at T*(k-1)/k and T*(k+1)/k,
        /// and if either of those would have fallen inside the scanned range, the scan would have
        /// found it. Every k whose neighbours should have been visible is therefore ruled out. For a
        /// lone null at 8333 us in a 4000-11000 scan, k=2 would put one at 4167 — inside the range,
        /// and absent — so k=1 stands and the answer is 120 Hz. Only when no k can be excluded does
        /// this return 0 and leave the caller to report candidates.
        /// </summary>
        public static double InferFrequencyHz(IReadOnlyList<PwmPoint> points, string label = "panel")
        {
            List<PwmNull> nulls = FindNulls(points, label);
            if (nulls.Count == 0) return 0;

            if (nulls.Count == 1)
                return SingleNullFrequency(nulls[0].RefinedExposureUs);

            // Least squares through (index, exposure), not the median gap. The nulls are only known
            // to the scan's 250 us grid, so each one carries up to 125 us of quantisation — and a
            // single gap inherits the error of both its ends. Fitting the whole line averages that
            // down: at 480 Hz the median gap reads 500 Hz, the fit reads 482.
            int n = nulls.Count;
            double meanIndex = (n - 1) / 2.0;
            double meanUs = 0;
            for (int i = 0; i < n; i++) meanUs += nulls[i].RefinedExposureUs;
            meanUs /= n;

            double cov = 0, varIndex = 0;
            for (int i = 0; i < n; i++)
            {
                double di = i - meanIndex;
                cov += di * (nulls[i].RefinedExposureUs - meanUs);
                varIndex += di * di;
            }

            double periodUs = varIndex > 0 ? cov / varIndex : 0;
            return periodUs > 0 ? 1e6 / periodUs : 0;
        }

        /// <summary>
        /// The frequency a lone null implies, or 0 when more than one multiple survives. See
        /// <see cref="InferFrequencyHz"/> for why a single null is usually not ambiguous.
        /// </summary>
        private static double SingleNullFrequency(double nullUs)
        {
            if (nullUs <= 0) return 0;

            int[] scan = ScanExposuresUs;
            int n = scan.Length;
            if (n < 3) return 0;

            double step = scan[1] - scan[0];

            // What the scan can actually catch. FindNulls needs a sample on either side, so the two
            // end points can never be nulls — the usable grid runs from scan[1] to scan[n-2]. A true
            // null lands on the nearest grid point, up to half a step away, so anything within half
            // a step of that usable span would have been found.
            double detectLo = scan[1] - step / 2;
            double detectHi = scan[n - 2] + step / 2;

            double onlyK = 0;
            for (int k = 1; k <= 8; k++)
            {
                double period = nullUs / k;
                bool neighbourWouldHaveShown =
                    Detectable(nullUs - period, detectLo, detectHi) ||
                    Detectable(nullUs + period, detectLo, detectHi);
                if (neighbourWouldHaveShown)
                    continue;           // the scan would have caught it, and did not — rule k out

                if (onlyK > 0) return 0;   // more than one k survives: genuinely ambiguous
                onlyK = k;
            }

            return onlyK > 0 ? 1e6 * onlyK / nullUs : 0;
        }

        private static bool Detectable(double us, double lo, double hi) => us >= lo && us <= hi;

        /// <summary>Fewest points that can carry a curve rather than a handful of probes.</summary>
        private const int MinScanPoints = 10;

        /// <summary>
        /// Whether these points are an evenly spaced scan. Sorted input assumed.
        /// </summary>
        private static bool IsEvenGrid(List<PwmPoint> sorted)
        {
            if (sorted.Count < MinScanPoints) return false;

            int step = sorted[1].ExposureUs - sorted[0].ExposureUs;
            if (step <= 0) return false;

            for (int i = 2; i < sorted.Count; i++)
                if (sorted[i].ExposureUs - sorted[i - 1].ExposureUs != step)
                    return false;

            return true;
        }

        // ----- Choosing the operating point -----

        /// <summary>One usable exposure, and what it costs.</summary>
        public readonly struct OperatingPoint
        {
            /// <summary>Which multiple of the PWM period this is.</summary>
            public readonly int K;
            public readonly int ExposureUs;
            public readonly double Fps;
            /// <summary>Shortest detectable event at this rate, at a depth threshold of 0.10.</summary>
            public readonly double BMinMs;
            /// <summary>Light gathered, relative to the longest candidate offered.</summary>
            public readonly double RelativeLight;
            /// <summary>Fraction of time the sensor is blind, if the rate has to be capped.</summary>
            public readonly double DeadFraction;
            public readonly bool InWindow;

            public OperatingPoint(int k, int exposureUs, double fps, double bMinMs,
                                  double relativeLight, double deadFraction, bool inWindow)
            {
                K = k;
                ExposureUs = exposureUs;
                Fps = fps;
                BMinMs = bMinMs;
                RelativeLight = relativeLight;
                DeadFraction = deadFraction;
                InWindow = inWindow;
            }
        }

        /// <summary>Depth a dip has to reach to be called an anomaly. Sets the shortest event seen.</summary>
        public const double DepthThreshold = 0.10;

        /// <summary>
        /// Every exposure that nulls <paramref name="freqHz"/> and could actually be run, longest
        /// first — the longest gathers the most light, and light is what makes the reading precise.
        ///
        /// The window is bounded at both ends. <paramref name="fpsCap"/> is what the camera can
        /// deliver, so an exposure shorter than that allows cannot be run at full duty: the rate
        /// has to be capped and the gap between exposures becomes dead time, reported here rather
        /// than hidden. <paramref name="fpsFloor"/> is where detection stops being trustworthy —
        /// below about 120 fps a single-refresh blank no longer contains a whole exposure, so its
        /// depth starts depending on where the phase happened to fall.
        ///
        /// Candidates outside the window are still returned, marked, because for some frequencies
        /// there is nothing inside it — 200 Hz is the awkward case, where k=1 overruns the camera
        /// and k=2 falls under the floor. Hiding that would present a forced trade as a free choice.
        ///
        /// The floor defaults to 119 rather than 120 on purpose. What the floor actually protects is
        /// that a 16.67 ms blank contains a whole exposure whatever the phase, and that needs
        /// <c>2T + 45 &lt;= 16667</c>, i.e. T no longer than 8311 us. The exposure that nulls 120 Hz
        /// is 8333 us — over by 22 us, worth 0.3% of depth in the worst phase. A floor of exactly
        /// 120 would throw away the single most useful exposure this whole procedure can find, over
        /// a rounding error.
        /// </summary>
        public static List<OperatingPoint> OperatingPoints(double freqHz, double fpsCap = 184,
                                                           double fpsFloor = 119)
        {
            var candidates = new List<OperatingPoint>();
            if (freqHz <= 0 || fpsCap <= 0 || fpsFloor <= 0) return candidates;

            double periodUs = 1e6 / freqHz;
            double tMin = 1e6 / fpsCap - InterFrameOverheadUs;      // shorter than this overruns the camera
            double tMax = 1e6 / fpsFloor - InterFrameOverheadUs;    // longer than this drops under the floor

            // One step past the window at each end, so the nearest fallback is on the table when
            // nothing lands inside it.
            double searchMax = tMax + periodUs;

            double longest = 0;
            var raw = new List<KeyValuePair<int, int>>();
            for (int k = 1; k <= 64; k++)
            {
                double t = k * periodUs;
                if (t < tMin - periodUs) continue;
                if (t > searchMax) break;

                int us = (int)Math.Round(t);
                raw.Add(new KeyValuePair<int, int>(k, us));
                if (us > longest) longest = us;
            }

            foreach (KeyValuePair<int, int> c in raw)
            {
                int us = c.Value;
                bool inWindow = us >= tMin && us <= tMax;

                // Below tMin the exposure allows more frames than the camera will give, so the rate
                // gets capped and the difference is time the sensor is not looking at anything.
                double naturalFps = FpsForExposure(us);
                double dead = 0;
                double fps = naturalFps;
                if (naturalFps > fpsCap)
                {
                    fps = fpsCap;
                    dead = 1.0 - us / (1e6 / fpsCap);
                }

                candidates.Add(new OperatingPoint(
                    c.Key, us, fps,
                    DepthThreshold * us / 1000.0,
                    longest > 0 ? us / longest : 0,
                    dead, inWindow));
            }

            // Usable ones first, then the fallbacks. Within each group the longest exposure leads,
            // because it gathers the most light. Sorting purely by length would put an unusable
            // candidate at the top of the table, where it reads as the recommendation.
            candidates.Sort((a, b) =>
                a.InWindow != b.InWindow ? (a.InWindow ? -1 : 1)
                                         : b.ExposureUs.CompareTo(a.ExposureUs));
            return candidates;
        }

        public const string CsvHeader = "label,exposure_us,min_luma,max_luma,mean_luma,max_clipped_pct,samples,resulting_fps";

        /// <summary>Parses a whole CSV, skipping the header and anything malformed.</summary>
        public static List<PwmPoint> ParseCsv(IEnumerable<string> lines)
        {
            var points = new List<PwmPoint>();
            if (lines == null) return points;

            foreach (string line in lines)
            {
                if (line != null && line.StartsWith("label,", StringComparison.Ordinal))
                    continue;
                if (PwmPoint.TryParseCsvLine(line, out PwmPoint p))
                    points.Add(p);
            }
            return points;
        }

        /// <summary>The report written next to the CSV. Korean, because its readers are.</summary>
        public static string RenderReport(IReadOnlyList<PwmPoint> points, string channel,
                                          string roi, string date)
        {
            PwmVerdict v = Judge(points);
            var sb = new StringBuilder();

            sb.AppendLine("# 백라이트 PWM 노출 스윕 측정");
            sb.AppendLine();
            sb.AppendLine($"측정일: {date}");
            sb.AppendLine($"채널: {channel}");
            sb.AppendLine($"분석 ROI: {roi}");
            sb.AppendLine("측정 방식: 앱이 노출을 바꿔가며 자동 수집 (`--pwm-sweep`)");
            sb.AppendLine();

            AppendTable(sb, points, "panel", "## 패널 (정지 화면)");
            AppendTable(sb, points, "room", "## 실내 조명 단독 (패널 OFF)");
            AppendScanFindings(sb, points);

            sb.AppendLine("## 판정");
            sb.AppendLine();
            switch (v.Family)
            {
                case PwmFamily.Hz120:
                    sb.AppendLine("- PWM 주파수는 **120 Hz의 배수**입니다 (240 · 480 · 960 Hz 중 하나).");
                    sb.AppendLine("- 실내 조명 리플(120 Hz)도 같은 노출에서 함께 소거되는 유리한 경우입니다.");
                    break;
                case PwmFamily.Hz100:
                    sb.AppendLine("- PWM 주파수는 **100 Hz의 배수**입니다 (200 · 1000 Hz 중 하나).");
                    sb.AppendLine("- 이 노출은 실내 조명 120 Hz를 소거하지 못합니다 — 아래 조명 항목을 볼 것.");
                    break;
                case PwmFamily.None:
                    sb.AppendLine("- 시험한 어느 노출에서도 리플이 충분히 떨어지지 않았습니다.");
                    sb.AppendLine("- 100·120 Hz 계열이 아니거나 이 패널이 DC 조광일 수 있습니다.");
                    sb.AppendLine("- 다음 단계: 4000~11000 µs를 250 µs 간격으로 훑어 최소점을 직접 찾을 것.");
                    sb.AppendLine("  최소점 T에서 주파수는 `f = k/T` (k = 1, 2, 3...) 중 하나입니다.");
                    break;
                case PwmFamily.NoRipple:
                    sb.AppendLine("- 기준점(6500 µs)에서도 리플이 거의 없었습니다.");
                    sb.AppendLine("- PWM 조광이 없거나(DC 조광), 화면이 포화되어 리플이 잘렸을 수 있습니다.");
                    break;
            }
            sb.AppendLine();
            sb.AppendLine($"- **권장 노출: {v.ExposureUs} µs**");
            sb.AppendLine($"- **권장 취득 fps: {v.TargetFps.ToString("0.#", CultureInfo.InvariantCulture)}**");
            sb.AppendLine("  (노출에서 유도한 값입니다. 이보다 낮게 캡을 걸면 프레임 사이에 사각지대가 생깁니다.)");
            if (v.Clipped)
            {
                sb.AppendLine();
                sb.AppendLine("> **이 판정은 신뢰할 수 없습니다.** 어느 지점에서 화소가 포화(clip > 0)했습니다.");
                sb.AppendLine("> 포화된 화소는 리플이 255에서 잘려 사라지므로 가짜 null을 만듭니다.");
                sb.AppendLine("> 조리개나 패널 밝기를 낮추고 다시 재십시오.");
            }
            sb.AppendLine();
            sb.AppendLine($"- 실내 조명: {RoomNote(points)}");
            sb.AppendLine();

            sb.AppendLine("## 근거");
            sb.AppendLine();
            sb.AppendLine("노출 T의 박스 적분은 주파수 f 성분을 `|sinc(pi*f*T)|` 배로 통과시키고, `f*T`가 정수이면");
            sb.AppendLine("정확히 0이다. 따라서 리플이 최소가 되는 노출은 PWM 주기의 정수배이며, 거기서 주파수");
            sb.AppendLine("계열이 역산된다. 리플은 자유 구동 카메라가 매번 무작위 위상으로 표본화하기 때문에");
            sb.AppendLine("관측되는 luma 산포로 나타난다 — 파형이 아니라 진폭만 보는 방법이다.");
            sb.AppendLine();
            sb.AppendLine("## 반영할 곳");
            sb.AppendLine();
            sb.AppendLine("- `research.md` 8절 실측표");
            sb.AppendLine("- `docs/superpowers/specs/2026-08-20-avn-anomaly-detection-design.md` 2절의 노출·fps 표");
            sb.AppendLine($"- 앱 설정: 채널마다 노출 {v.ExposureUs} µs, 세 채널 동일하게");

            return sb.ToString();
        }

        /// <summary>
        /// What the scan curve says, when there is one: where the nulls fell, what frequency their
        /// spacing implies, and which exposures could actually be run at it.
        /// </summary>
        private static void AppendScanFindings(StringBuilder sb, IReadOnlyList<PwmPoint> points)
        {
            List<PwmNull> nulls = FindNulls(points);
            if (nulls.Count == 0)
                return;

            sb.AppendLine("## 곡선에서 읽은 것");
            sb.AppendLine();
            sb.Append("리플이 떨어진 노출: ");
            for (int i = 0; i < nulls.Count; i++)
                sb.Append(i > 0 ? ", " : "").Append(nulls[i].ExposureUs).Append(" µs");
            sb.AppendLine();
            sb.AppendLine();

            double f = InferFrequencyHz(points);
            if (f <= 0)
            {
                sb.AppendLine($"null이 하나뿐이라 주파수가 하나로 정해지지 않는다 — `f = k / {nulls[0].ExposureUs} µs`의");
                sb.AppendLine("어느 k인지 알 수 없다. 후보:");
                sb.AppendLine();
                for (int k = 1; k <= 4; k++)
                    sb.AppendLine($"- k={k} → **{1e6 * k / nulls[0].ExposureUs:0.#} Hz**");
                sb.AppendLine();
                sb.AppendLine("스캔 범위를 넓히거나 더 촘촘히 훑어 두 번째 null을 찾으면 간격이 곧 주기다.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"**PWM 주파수 ≈ {f:0.#} Hz**");
            sb.AppendLine();
            if (nulls.Count >= 2)
                sb.AppendLine("null 사이의 간격이 곧 주기다 — 어느 null이 몇 번째 배수인지는 몰라도 된다.");
            else
                sb.AppendLine($"null이 하나뿐이지만 답은 정해진다. 이것이 k번째 배수라면 {nulls[0].ExposureUs} µs의 " +
                              "k분의 1 간격으로 이웃 null이 있어야 하고, 그 위치가 스캔 범위 안이면 스캔이 찾았을 " +
                              "것이다. 찾지 못했으므로 그런 k는 전부 배제된다 — 하나만 남았다.");
            sb.AppendLine();

            // The camera's own answer beats the model number: 184 is the full-resolution figure and
            // this may be running decimated.
            double cap = 0;
            for (int i = 0; i < points.Count; i++)
                if (points[i].ResultingFps > cap) cap = points[i].ResultingFps;
            if (cap <= 0) cap = 184;

            sb.AppendLine($"### 쓸 수 있는 노출 (상한 {cap:0.#} fps, 하한 120 fps 기준)");
            sb.AppendLine();
            sb.AppendLine("| k | 노출 | fps | 빛 | b_min | 사각 | 판정 |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (OperatingPoint p in OperatingPoints(f, cap))
            {
                string verdict = p.InWindow ? "**창 안**"
                               : p.DeadFraction > 0 ? "카메라 상한 초과 — 캡 필요"
                               : "하한 미만 — 감도 손해";
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} µs | {2:0.#} | {3:0%} | {4:0.00} ms | {5:0.0%} | {6} |",
                    p.K, p.ExposureUs, p.Fps, p.RelativeLight, p.BMinMs, p.DeadFraction, verdict));
            }
            sb.AppendLine();
            sb.AppendLine("**창 안이 여럿이면 가장 긴 노출을 권한다** — 빛이 많을수록 측정이 정밀하다.");
            sb.AppendLine("고장이 프레임 미만(백라이트 경로)으로 밝혀지면 그때는 b_min이 작은 쪽으로 바꾼다.");
            sb.AppendLine("**창 안이 없으면** 사각지대를 사거나 감도를 파는 것 중 하나다 — 위 표가 값을 보여준다.");
            sb.AppendLine();
        }

        private static void AppendTable(StringBuilder sb, IReadOnlyList<PwmPoint> points,
                                        string label, string heading)
        {
            bool any = false;
            for (int i = 0; i < (points?.Count ?? 0); i++)
            {
                if (!string.Equals(points[i].Label, label, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!any)
                {
                    sb.AppendLine(heading);
                    sb.AppendLine();
                    sb.AppendLine("| 노출 | 소거 대상 | 최소 | 최대 | 평균 | 리플 | clip | 표본 | 카메라 fps |");
                    sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
                    any = true;
                }

                PwmPoint p = points[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} µs | {1} | {2:0.##} | {3:0.##} | {4:0.##} | {5:0.0}% | {6:0.##}% | {7} | {8} |",
                    p.ExposureUs, NullTargetOf(p.ExposureUs),
                    p.MinLuma, p.MaxLuma, p.MeanLuma, p.RipplePercent, p.MaxClippedPct, p.Samples,
                    p.ResultingFps > 0 ? p.ResultingFps.ToString("0.#", CultureInfo.InvariantCulture) : "-"));
            }

            if (any) sb.AppendLine();
        }

        private static string NullTargetOf(int exposureUs)
        {
            switch (exposureUs)
            {
                case 6500: return "없음 (기준)";
                case 5000: return "100 Hz 계열";
                case 8333: return "120 Hz 계열";
                case 10000: return "100 Hz 계열";
                default: return "-";
            }
        }
    }
}
