using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;

namespace MatroxFrameGrabber.Views
{
    /// <summary>
    /// A lane per camera showing what the detector decided, with brightness reduced to a sparkline
    /// beside each name.
    ///
    /// This answers the question the brightness curve could not: which item, on which device. The
    /// lane is the device, the mark's colour is the item, and a solid mark against an outlined one
    /// is whether the detector believed it or the onset-spread gate turned it away.
    ///
    /// Two placement rules come from <see cref="TimelineLayout"/> rather than from here, because
    /// both were forced by measured data and both are easy to get wrong in drawing code: a mark is
    /// never narrower than five pixels (the shortest detection measured was 8.0 ms, which is
    /// 0.0013% of a ten-minute axis) and rejected marks are drawn first so detections sit on top
    /// (sixteen rejections fell inside 68 seconds with two detections among them).
    ///
    /// Built once and updated in place. The renderer this replaces cleared and rebuilt its legend
    /// on every tick.
    /// </summary>
    public partial class ChannelTimeline : UserControl
    {
        /// <summary>
        /// Seconds of board time the lanes show. Ten minutes because anomalies are sparse - 74 in
        /// half an hour on the measured run - so the brightness window of two minutes would show
        /// almost nothing.
        /// </summary>
        public const double WindowSeconds = 600.0;

        private const int SparklinePoints = 60;
        private const double SparklineWidth = 58.0;
        private const double SparklineHeight = 14.0;
        private const double LaneHeight = 18.0;

        private static readonly string[] ChannelBrushKeys = { "Ch0Brush", "Ch1Brush", "Ch2Brush", "Ch3Brush" };

        /// <summary>
        /// One colour per kind, so a mark says which item it is. Distinct on this dark ground and
        /// deliberately not the channel colours, which already mean "which camera".
        /// </summary>
        private static readonly Dictionary<AnomalyKind, Color> KindColors = new Dictionary<AnomalyKind, Color>
        {
            { AnomalyKind.Dropout,  Color.FromRgb(0xFF, 0x6E, 0x6E) },
            { AnomalyKind.Blackout, Color.FromRgb(0xC5, 0x86, 0xC0) },
            { AnomalyKind.Washout,  Color.FromRgb(0xFF, 0xD4, 0x79) },
            { AnomalyKind.Flicker,  Color.FromRgb(0x4F, 0xC3, 0xF7) },
            { AnomalyKind.Flip,     Color.FromRgb(0x81, 0xC7, 0x84) },
            // Blue grey for a picture that has stopped moving, pink for one whose colour is wrong -
            // both far enough from the five above to be told apart on a 5 px mark.
            { AnomalyKind.Freeze,     Color.FromRgb(0x90, 0xA4, 0xAE) },
            { AnomalyKind.ColorShift, Color.FromRgb(0xF0, 0x62, 0x92) },
        };

        private static readonly Dictionary<ChannelHealth, Color> HealthColors = new Dictionary<ChannelHealth, Color>
        {
            { ChannelHealth.Absent,          Color.FromRgb(0x3A, 0x3A, 0x3A) },
            { ChannelHealth.Stopped,         Color.FromRgb(0x6F, 0x6F, 0x6F) },
            { ChannelHealth.Healthy,         Color.FromRgb(0x81, 0xC7, 0x84) },
            { ChannelHealth.Uncalibrated,    Color.FromRgb(0xFF, 0xB7, 0x4D) },
            { ChannelHealth.OpticsOutOfBand, Color.FromRgb(0xFF, 0xB7, 0x4D) },
            { ChannelHealth.ClipIncomplete,  Color.FromRgb(0xFF, 0x6E, 0x6E) },
            { ChannelHealth.DetectorBlind,   Color.FromRgb(0xFF, 0x6E, 0x6E) },
            { ChannelHealth.FramesMissed,    Color.FromRgb(0xFF, 0x6E, 0x6E) },
            // Grey like Stopped, because it is the same kind of statement - somebody chose this -
            // and deliberately not the green that would claim the panel had been examined.
            { ChannelHealth.DetectionOff,    Color.FromRgb(0x6F, 0x6F, 0x6F) },
        };

        /// <summary>Everything one lane needs, so an update touches properties and not the tree.</summary>
        private sealed class Lane
        {
            public Ellipse Dot;
            public TextBlock Name;
            public TextBlock Counts;
            public Canvas Marks;
            public readonly List<Rectangle> Pool = new List<Rectangle>();
        }

        private sealed class LegendEntry
        {
            public FrameworkElement Root;
            public TextBlock Text;
            public Polyline Spark;
        }

        private readonly Lane[] _lanes = new Lane[4];
        private readonly LegendEntry[] _legend = new LegendEntry[4];
        private readonly TextBlock[] _axisLabels = new TextBlock[5];

        private readonly BrightnessSample[] _sampleScratch = new BrightnessSample[BrightnessHistory.Capacity];
        private readonly TimelineEvent[] _eventScratch = new TimelineEvent[AnomalyTimeline.Capacity];

        public ChannelTimeline()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Redraws from the channels. Called on the 500 ms stats tick, on the UI thread - which is
        /// also the only thread that fills the timelines being read here.
        /// </summary>
        public void Update(IReadOnlyList<CameraChannel> channels)
        {
            if (channels == null) return;

            EnsureRows(channels);

            double now = NewestBoardTime(channels);
            var diag = new StringBuilder();

            for (int i = 0; i < channels.Count && i < _lanes.Length; i++)
            {
                CameraChannel ch = channels[i];
                UpdateLegend(i, ch);
                UpdateLane(i, ch, now);

                if (!ch.CameraPresent) continue;
                if (diag.Length > 0) diag.Append("  ");
                diag.Append($"ch{i} ");
                diag.Append(ch.BrightnessFailures > 0
                    ? $"FAIL x{ch.BrightnessFailures}"
                    : $"{ch.LastBrightnessSampleMs:F1} ms");
            }

            UpdateAxis(now);
            ToolTip = diag.Length > 0 ? diag.ToString() : null;
        }

        // ----- construction, once -----

        private void EnsureRows(IReadOnlyList<CameraChannel> channels)
        {
            for (int i = 0; i < channels.Count && i < _lanes.Length; i++)
            {
                bool present = channels[i].CameraPresent;

                if (_legend[i] == null && present)
                    _legend[i] = BuildLegend(i, channels[i]);
                if (_legend[i] != null)
                    _legend[i].Root.Visibility = present ? Visibility.Visible : Visibility.Collapsed;

                if (_lanes[i] == null && present)
                    _lanes[i] = BuildLane(i, channels[i]);
                if (_lanes[i] != null)
                {
                    Visibility v = present ? Visibility.Visible : Visibility.Collapsed;
                    _lanes[i].Dot.Visibility = v;
                    _lanes[i].Name.Visibility = v;
                    _lanes[i].Counts.Visibility = v;
                    _lanes[i].Marks.Visibility = v;
                }
            }

            if (_axisLabels[0] == null)
            {
                for (int t = 0; t < _axisLabels.Length; t++)
                {
                    _axisLabels[t] = new TextBlock
                    {
                        FontSize = 9,
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x71, 0x78)),
                    };
                    Axis.Children.Add(_axisLabels[t]);
                }
            }
        }

        private LegendEntry BuildLegend(int index, CameraChannel channel)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = (Brush)FindResource(ChannelBrushKeys[index]),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            var text = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextBrush"),
            };
            var spark = new Polyline
            {
                Stroke = (Brush)FindResource(ChannelBrushKeys[index]),
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Width = SparklineWidth,
                Height = SparklineHeight,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            row.Children.Add(swatch);
            row.Children.Add(text);
            row.Children.Add(spark);
            Legend.Children.Add(row);

            return new LegendEntry { Root = row, Text = text, Spark = spark };
        }

        private Lane BuildLane(int index, CameraChannel channel)
        {
            Lanes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LaneHeight) });
            int row = Lanes.RowDefinitions.Count - 1;

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            var bar = new Border
            {
                Width = 3,
                Height = 10,
                Background = (Brush)FindResource(ChannelBrushKeys[index]),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            var name = new TextBlock
            {
                Text = $"CAM{index}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextBrush"),
            };
            var counts = new TextBlock
            {
                FontSize = 10,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)FindResource("MutedTextBrush"),
            };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(dot, 0); head.Children.Add(dot);
            Grid.SetColumn(bar, 1); head.Children.Add(bar);
            Grid.SetColumn(name, 2); head.Children.Add(name);
            Grid.SetColumn(counts, 3); head.Children.Add(counts);
            head.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetRow(head, row);
            Grid.SetColumn(head, 0);
            Lanes.Children.Add(head);

            var marks = new Canvas
            {
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                Margin = new Thickness(0, 2, 0, 2),
            };
            Grid.SetRow(marks, row);
            Grid.SetColumn(marks, 1);
            Lanes.Children.Add(marks);

            return new Lane { Dot = dot, Name = name, Counts = counts, Marks = marks };
        }

        // ----- per-tick updates -----

        private void UpdateLegend(int index, CameraChannel channel)
        {
            LegendEntry e = _legend[index];
            if (e == null) return;

            if (!channel.IsGrabbing || !channel.Brightness.HasData)
            {
                e.Text.Text = $"{channel.Name}  —";
                e.Spark.Points = new PointCollection();
                return;
            }

            BrightnessSample latest = channel.Brightness.Latest;
            e.Text.Text = $"{channel.Name}  {latest.Luma:0}  clip {latest.ClippedPct:0.0}%";

            // 60 points, not 240: at a quarter of the samples the shape survives and the cost does
            // not. The old curve assigned about 960 points a second across four channels.
            int n = channel.Brightness.CopyTo(_sampleScratch);
            var pts = new PointCollection(SparklinePoints);
            int step = Math.Max(1, n / SparklinePoints);
            for (int p = (n - 1) % step; p < n; p += step)
            {
                double x = SparklineWidth * p / Math.Max(1, n - 1);
                double y = SparklineHeight - SparklineHeight * _sampleScratch[p].Luma / 255.0;
                pts.Add(new Point(x, y));
            }
            e.Spark.Points = pts;
        }

        private void UpdateLane(int index, CameraChannel channel, double nowSec)
        {
            Lane lane = _lanes[index];
            if (lane == null) return;

            ChannelHealth health = channel.Health;
            // TryGetValue, not the indexer: a state added to the enum without a colour here
            // would throw on the stats tick, inside a UI update, for want of a dictionary entry.
            lane.Dot.Fill = new SolidColorBrush(
                HealthColors.TryGetValue(health, out Color hc) ? hc : Colors.Gray);
            lane.Dot.ToolTip = ChannelHealthRule.Describe(health);

            AnomalyTimeline t = channel.RecentAnomalies;
            lane.Counts.Text = $"{t.DetectedCount} · {t.RejectedCount}";
            lane.Counts.ToolTip = "검출 · 거절";

            double width = lane.Marks.ActualWidth;
            if (width <= 0.0) { HideFrom(lane, 0); return; }

            int count = t.CopyTo(_eventScratch);
            var window = new TimelineEvent[count];
            Array.Copy(_eventScratch, window, count);
            List<TimelineMark> marks = TimelineLayout.Place(window, nowSec, WindowSeconds, width);

            double h = Math.Max(1.0, lane.Marks.ActualHeight);
            for (int m = 0; m < marks.Count; m++)
            {
                Rectangle r = Rented(lane, m);
                TimelineMark mk = marks[m];
                Color c = KindColors.TryGetValue(mk.Kind, out Color kc) ? kc : Colors.Gray;

                Canvas.SetLeft(r, mk.Left);
                r.Width = mk.Width;
                r.Height = h;

                if (mk.Rejected)
                {
                    // Outlined rather than filled: it was turned away, and a solid mark would read
                    // as something the detector believed.
                    r.Fill = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
                    r.Stroke = new SolidColorBrush(Color.FromArgb(0x99, c.R, c.G, c.B));
                    r.StrokeThickness = 1;
                    r.StrokeDashArray = null;
                }
                else
                {
                    r.Fill = new SolidColorBrush(c);
                    r.Stroke = mk.Truncated ? new SolidColorBrush(Colors.White) : null;
                    r.StrokeThickness = mk.Truncated ? 1 : 0;
                    // Dashed for a truncated event: its duration is a floor, not a measurement.
                    r.StrokeDashArray = mk.Truncated ? new DoubleCollection { 2, 2 } : null;
                }

                r.ToolTip = Tip(mk, nowSec);
                r.Visibility = Visibility.Visible;
            }
            HideFrom(lane, marks.Count);
        }

        private static string Tip(TimelineMark m, double nowSec)
        {
            string word = AnomalyCatalog.DeviationWord(m.Kind);
            double ago = nowSec - m.StartSec;
            return $"{m.Kind}{(m.Rejected ? " · 거절(쓸림)" : string.Empty)}\n"
                 + $"{ago:F1} s 전 · {m.DurationMs:F1} ms · {word} {m.MaxDeviation:F2}\n"
                 + $"frame {m.StartFrame}-{m.EndFrame}"
                 + (m.Truncated ? "\n상한에서 끊김 — 길이는 최솟값입니다" : string.Empty);
        }

        private Rectangle Rented(Lane lane, int i)
        {
            while (lane.Pool.Count <= i)
            {
                var r = new Rectangle { RadiusX = 1, RadiusY = 1, VerticalAlignment = VerticalAlignment.Stretch };
                Canvas.SetTop(r, 0);
                lane.Marks.Children.Add(r);
                lane.Pool.Add(r);
            }
            return lane.Pool[i];
        }

        private static void HideFrom(Lane lane, int i)
        {
            for (; i < lane.Pool.Count; i++)
                lane.Pool[i].Visibility = Visibility.Collapsed;
        }

        private void UpdateAxis(double nowSec)
        {
            double w = 0.0;
            foreach (Lane l in _lanes)
                if (l != null && l.Marks.ActualWidth > w) w = l.Marks.ActualWidth;
            if (w <= 0.0) return;

            double[] f = TimelineLayout.AxisFractions(_axisLabels.Length);
            for (int i = 0; i < _axisLabels.Length; i++)
            {
                TextBlock label = _axisLabels[i];

                // Elapsed, not a clock. Board time is seconds since the board started, so a
                // h:mm:ss of it reads as "19:25:13" - nineteen hours of uptime, which looks like
                // half past seven in the evening. The wall-clock mapping needs a board-to-wall
                // anchor and belongs with the clip extractor, which needs one anyway.
                double back = WindowSeconds * (1.0 - f[i]);
                label.Text = i == _axisLabels.Length - 1
                    ? "now"
                    : "-" + TimeSpan.FromSeconds(back).ToString(@"m\:ss");
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double x = w * f[i] - (i == 0 ? 0.0 : label.DesiredSize.Width * (i == _axisLabels.Length - 1 ? 1.0 : 0.5));
                Canvas.SetLeft(label, Math.Max(0.0, x));
                Canvas.SetTop(label, 1);
            }
        }

        /// <summary>
        /// The newest board stamp across the running channels - the clock the events carry.
        ///
        /// Board time, not the wall clock the tick runs on. The cameras free-run and the board is
        /// the only clock all of them share, so a lane drawn against DateTime.Now would place the
        /// same event at different places on different lanes, which is the one thing this layout
        /// exists to make readable.
        /// </summary>
        private static double NewestBoardTime(IReadOnlyList<CameraChannel> channels)
        {
            double now = 0.0;
            foreach (CameraChannel c in channels)
                if (c.IsGrabbing && c.LastBoardTimeSec > now) now = c.LastBoardTimeSec;
            return now;
        }
    }
}
