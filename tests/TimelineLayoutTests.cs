using System.Collections.Generic;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// Where an anomaly mark lands on a lane. Both rules here came out of the 30-minute static run,
    /// where sixteen rejections fell inside 68 seconds with two detections among them.
    /// </summary>
    public class TimelineLayoutTests
    {
        const double Span = 600.0;    // ten minutes
        const double Width = 900.0;   // pixels

        static TimelineEvent Ev(double startSec, double durMs, bool rejected = false,
                                AnomalyKind kind = AnomalyKind.Dropout, bool truncated = false)
            => new TimelineEvent(
                new AnomalyEvent(1000, 1007, 7, 0.07, 0.93, startSec, durMs,
                                 truncated: truncated, kind: kind),
                rejected);

        static List<TimelineMark> Place(params TimelineEvent[] events) =>
            TimelineLayout.Place(events, windowEndSec: 600.0, spanSec: Span, canvasPx: Width);

        // ----- the two rules the data forced -----

        /// <summary>
        /// The shortest detection measured was 8.0 ms. On a 600 s axis that is 0.0013% of the
        /// width - 0.012 px - and the shortest events are the ones this strip exists to catch.
        /// </summary>
        [Fact]
        public void An_eight_millisecond_event_is_still_wide_enough_to_see()
        {
            TimelineMark m = Assert.Single(Place(Ev(300.0, 8.0)));
            Assert.Equal(TimelineLayout.MinMarkPx, m.Width, 6);
        }

        /// <summary>
        /// Rejected marks come first so a caller adding them in order draws them underneath. With
        /// the order reversed the two detections in that 68-second burst would sit under sixteen
        /// hatched rectangles.
        /// </summary>
        [Fact]
        public void Rejected_marks_are_placed_before_detected_ones()
        {
            List<TimelineMark> marks = Place(
                Ev(300.0, 100.0),                    // detected
                Ev(300.1, 100.0, rejected: true),
                Ev(300.2, 100.0, rejected: true));

            Assert.Equal(3, marks.Count);
            Assert.True(marks[0].Rejected);
            Assert.True(marks[1].Rejected);
            Assert.False(marks[2].Rejected);
        }

        // ----- placement -----

        [Fact]
        public void A_mark_lands_where_its_time_says()
        {
            // Window is 0..600 s over 900 px, so 1 s is 1.5 px.
            TimelineMark m = Assert.Single(Place(Ev(300.0, 10000.0)));
            Assert.Equal(450.0, m.Left, 3);
            Assert.Equal(15.0, m.Width, 3);
        }

        [Fact]
        public void A_longer_event_is_a_wider_mark()
        {
            double narrow = Assert.Single(Place(Ev(100.0, 1000.0))).Width;
            double wide = Assert.Single(Place(Ev(100.0, 20000.0))).Width;
            Assert.True(wide > narrow, $"{wide} should exceed {narrow}");
        }

        // ----- the window edges -----

        [Fact]
        public void An_event_older_than_the_window_is_dropped()
        {
            Assert.Empty(Place(Ev(-10.0, 100.0)));
        }

        [Fact]
        public void An_event_after_the_window_is_dropped()
        {
            Assert.Empty(Place(Ev(700.0, 100.0)));
        }

        /// <summary>An event straddling the old edge keeps the part still on screen.</summary>
        [Fact]
        public void An_event_running_into_the_window_is_clipped_at_the_left()
        {
            TimelineMark m = Assert.Single(Place(Ev(-5.0, 20000.0)));   // -5 s .. +15 s
            Assert.Equal(0.0, m.Left, 6);
            Assert.Equal(15.0 * 1.5, m.Width, 3);
        }

        /// <summary>
        /// And one at the newest edge keeps its width by moving its left edge in, rather than
        /// becoming a sliver that reads as nothing.
        /// </summary>
        [Fact]
        public void An_event_at_the_newest_edge_keeps_its_minimum_width()
        {
            TimelineMark m = Assert.Single(Place(Ev(599.999, 8.0)));
            Assert.Equal(TimelineLayout.MinMarkPx, m.Width, 6);
            Assert.Equal(Width - TimelineLayout.MinMarkPx, m.Left, 6);
        }

        // ----- what a mark carries -----

        /// <summary>
        /// The kind travels with the mark, because colouring by it is what makes the strip answer
        /// "which item" as well as "which device" - the original ask.
        /// </summary>
        [Fact]
        public void A_mark_carries_the_kind_so_it_can_be_coloured_by_it()
        {
            List<TimelineMark> marks = Place(
                Ev(100.0, 100.0, kind: AnomalyKind.Dropout),
                Ev(200.0, 100.0, kind: AnomalyKind.Washout));

            Assert.Contains(marks, m => m.Kind == AnomalyKind.Dropout);
            Assert.Contains(marks, m => m.Kind == AnomalyKind.Washout);
        }

        [Fact]
        public void A_mark_carries_what_a_tooltip_needs()
        {
            TimelineMark m = Assert.Single(Place(Ev(123.456, 56.3, truncated: true)));
            Assert.Equal(123.456, m.StartSec, 6);
            Assert.Equal(56.3, m.DurationMs, 6);
            Assert.Equal(0.07, m.MaxDeviation, 6);
            Assert.True(m.Truncated);
            Assert.Equal(1000, m.StartFrame);
            Assert.Equal(1007, m.EndFrame);
        }

        // ----- degenerate input -----

        [Fact]
        public void Nothing_is_placed_on_a_lane_with_no_width_or_no_span()
        {
            var events = new[] { Ev(300.0, 100.0) };
            Assert.Empty(TimelineLayout.Place(events, 600.0, Span, 0.0));
            Assert.Empty(TimelineLayout.Place(events, 600.0, 0.0, Width));
            Assert.Empty(TimelineLayout.Place(null, 600.0, Span, Width));
        }

        [Fact]
        public void The_axis_runs_from_the_oldest_edge_to_the_newest()
        {
            double[] f = TimelineLayout.AxisFractions(5);
            Assert.Equal(5, f.Length);
            Assert.Equal(0.0, f[0], 6);
            Assert.Equal(0.5, f[2], 6);
            Assert.Equal(1.0, f[4], 6);
        }
    }
}
