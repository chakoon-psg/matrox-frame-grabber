using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// Every settable value survives a copy.
    ///
    /// This test exists because five did not. The Blackout knobs were added to KindSettings and
    /// not to CopyFrom, so a value written in settings.json was read, dropped on the way to the
    /// channel, and the log reported the default - with nothing failing anywhere. It is the same
    /// shape as the JsonStringEnumConverter that stopped the whole settings file loading: the
    /// symptom is "the setting does not apply", and nothing points at the cause.
    ///
    /// Written as a sweep rather than a list, so the next field added is covered without anyone
    /// remembering to add it here.
    /// </summary>
    public class KindSettingsCopyTests
    {
        /// <summary>Values that are all different from the defaults, so a dropped copy shows.</summary>
        private static KindSettings Distinctive() => new KindSettings
        {
            Enabled = true,
            Deviation = 0.123,
            Coherence = 0.456,
            DebounceMs = 77.0,
            MaxEventMs = 888.0,
            MaxOnsetSpreadMs = 33.0,
            MinOnsetTiles = 5,
            CalibratedAt = "2026-09-11T00:00:00Z",
            CalibrationFrames = 12345,
            CalibrationFloor = 0.0091,
            BlackoutEnterLuma = 7.5,
            BlackoutExitLuma = 19.5,
            BlackoutMaxSpread = 6.25,
            BlackoutEnterMs = 750.0,
            BlackoutRecoverMs = 250.0,
        };

        /// <summary>
        /// Reflection over every public settable property, so adding a field to KindSettings
        /// without adding it to CopyFrom fails here rather than on the bench.
        /// </summary>
        [Fact]
        public void Every_settable_property_survives_CopyFrom()
        {
            KindSettings from = Distinctive();
            var to = new KindSettings();

            to.CopyFrom(from);

            foreach (var p in typeof(KindSettings).GetProperties())
            {
                if (!p.CanRead || !p.CanWrite) continue;
                // StoredEnabled is deliberately not copied - it is the old file's key, read once
                // by the migration, and carrying it would put Enabled back into every save.
                if (p.Name == nameof(KindSettings.StoredEnabled)) continue;

                object a = p.GetValue(from);
                object b = p.GetValue(to);
                Assert.Equal(a, b);
            }
        }

        [Fact]
        public void The_blackout_knobs_in_particular()
        {
            var to = new KindSettings();
            to.CopyFrom(Distinctive());

            Assert.Equal(7.5, to.BlackoutEnterLuma, 3);
            Assert.Equal(19.5, to.BlackoutExitLuma, 3);
            Assert.Equal(6.25, to.BlackoutMaxSpread, 3);
            Assert.Equal(750.0, to.BlackoutEnterMs, 3);
            Assert.Equal(250.0, to.BlackoutRecoverMs, 3);
        }
    }
}
