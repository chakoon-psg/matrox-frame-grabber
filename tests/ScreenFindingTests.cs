using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The finding and the inspector that finds nothing. Small, but the second one is the whole
    /// removal story: every caller has something to call when there is no model.
    /// </summary>
    public class ScreenFindingTests
    {
        [Fact]
        public void A_finding_knows_its_far_edges()
        {
            var f = new ScreenFinding(AnomalyKind.Washout, 0.75f, 100f, 50f, 40f, 20f);

            Assert.Equal(140f, f.Right);
            Assert.Equal(70f, f.Bottom);
            Assert.Equal(800f, f.Area);
        }

        [Fact]
        public void An_empty_finding_has_no_area()
        {
            Assert.Equal(0f, new ScreenFinding(AnomalyKind.Flip, 1f, 0f, 0f, 0f, 10f).Area);
            Assert.Equal(0f, new ScreenFinding(AnomalyKind.Flip, 1f, 0f, 0f, -5f, 10f).Area);
        }

        /// <summary>It speaks the app's vocabulary, which is what lets the rest of the app filter it.</summary>
        [Fact]
        public void A_finding_names_a_kind_the_settings_window_lists()
        {
            var f = new ScreenFinding(AnomalyKind.ColorShift, 0.6f, 0f, 0f, 1f, 1f);

            Assert.Equal(AnomalyKind.ColorShift, f.Kind);
            Assert.True(AnomalyKindSet.Default().Length >= (int)f.Kind + 1);
        }

        [Fact]
        public void A_finding_says_what_it_is()
        {
            string s = new ScreenFinding(AnomalyKind.Dropout, 0.5f, 10f, 20f, 30f, 40f).ToString();

            Assert.Contains("Dropout", s);
            Assert.Contains("30x40", s);
        }

        // ----- the one that finds nothing -----

        [Fact]
        public void The_null_inspector_finds_nothing_and_says_why()
        {
            using var none = new NullScreenInspector("no weights beside the exe");

            Assert.Equal(0, none.Inspect(new byte[3], stackalloc ScreenFinding[4]));
            Assert.Empty(none.Reports);
            Assert.Contains("no weights beside the exe", none.Describe);
        }

        [Fact]
        public void It_has_a_reason_even_when_nobody_gave_one()
        {
            using var none = new NullScreenInspector();

            Assert.False(string.IsNullOrWhiteSpace(none.Reason));
            Assert.Contains("no screen inspector", none.Describe);
        }

        /// <summary>
        /// Not zero. A caller sizing a letterbox from these would otherwise ask for a picture of
        /// no pixels and get an exception from somewhere unrelated.
        /// </summary>
        [Fact]
        public void Its_input_size_is_usable_arithmetic()
        {
            using var none = new NullScreenInspector();

            Assert.True(none.InputWidth > 0);
            Assert.True(none.InputHeight > 0);
        }
    }
}
