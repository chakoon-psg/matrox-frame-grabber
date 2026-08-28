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

        public PwmPoint(string label, int exposureUs, float minLuma, float maxLuma,
                        float meanLuma, float maxClippedPct, int samples)
        {
            Label = label;
            ExposureUs = exposureUs;
            MinLuma = minLuma;
            MaxLuma = maxLuma;
            MeanLuma = meanLuma;
            MaxClippedPct = maxClippedPct;
            Samples = samples;
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
            Samples.ToString(CultureInfo.InvariantCulture));

        public static bool TryParseCsvLine(string line, out PwmPoint point)
        {
            point = default;
            if (string.IsNullOrWhiteSpace(line)) return false;

            string[] f = line.Split(',');
            if (f.Length < 7) return false;

            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int us)) return false;
            if (!float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float min)) return false;
            if (!float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float max)) return false;
            if (!float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float mean)) return false;
            if (!float.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float clip)) return false;
            if (!int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return false;

            point = new PwmPoint(f[0], us, min, max, mean, clip, n);
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

        public const string CsvHeader = "label,exposure_us,min_luma,max_luma,mean_luma,max_clipped_pct,samples";

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
                    sb.AppendLine("| 노출 | 소거 대상 | 최소 | 최대 | 평균 | 리플 | clip | 표본 |");
                    sb.AppendLine("|---|---|---|---|---|---|---|---|");
                    any = true;
                }

                PwmPoint p = points[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} µs | {1} | {2:0.##} | {3:0.##} | {4:0.##} | {5:0.0}% | {6:0.##}% | {7} |",
                    p.ExposureUs, NullTargetOf(p.ExposureUs),
                    p.MinLuma, p.MaxLuma, p.MeanLuma, p.RipplePercent, p.MaxClippedPct, p.Samples));
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
