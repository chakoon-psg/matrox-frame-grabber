using System;
using System.Collections.Generic;
using System.Text;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Which anomaly kinds are being watched for, as a set that can be stored and read back.
    ///
    /// This is app-wide, not per camera, and the split is deliberate: *which faults we are looking
    /// for* is a policy for the run, while *how sensitive this camera is* is a calibration of one
    /// optical path. The thresholds stay per channel because they had to be measured that way -
    /// the dimmest of three channels needed 0.15 where the other two ran at 0.05 - but a rig where
    /// camera 1 watches for Blackout and camera 2 does not is a rig whose report cannot be read.
    ///
    /// Stored as names rather than a bitmask or an array of flags, so that appending a kind cannot
    /// shift what an older file meant, and an unknown name from a newer build is ignored instead of
    /// switching on a detector nobody here has heard of.
    /// </summary>
    public static class AnomalyKindSet
    {
        /// <summary>The default: Dropout, the only kind with a detector behind it.</summary>
        public static bool[] Default()
        {
            var on = new bool[AnomalyCatalog.Count];
            on[AnomalyCatalog.Index(AnomalyKind.Dropout)] = true;
            return on;
        }

        /// <summary>
        /// Reads a stored list of names.
        ///
        /// A **missing** list takes the default, because a file that lost the key must not silently
        /// stop watching. A list that is **present and empty** means exactly that: nothing. The two
        /// have to be told apart, and treating an empty list as the default was measured doing the
        /// wrong thing - unticking every box wrote [], which read back as Dropout on, so the
        /// setting reverted itself on the next start and the log said it was watching.
        ///
        /// A list of names this build does not know is also taken at its word. It cannot watch for
        /// a kind it has never heard of, and every box then shows unticked with the channel
        /// reporting DetectionOff, which is the truth rather than a guess.
        /// </summary>
        public static bool[] FromNames(IEnumerable<string> names)
        {
            if (names == null) return Default();

            var on = new bool[AnomalyCatalog.Count];
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!Enum.TryParse(name.Trim(), ignoreCase: true, out AnomalyKind kind)) continue;
                if ((int)kind < 0 || (int)kind >= AnomalyCatalog.Count) continue;
                on[AnomalyCatalog.Index(kind)] = true;
            }
            return on;
        }

        /// <summary>The names to store, in catalog order.</summary>
        public static string[] ToNames(bool[] enabled)
        {
            var names = new List<string>(AnomalyCatalog.Count);
            foreach (AnomalyKind k in AnomalyCatalog.All)
                if (Get(enabled, k)) names.Add(k.ToString());
            return names.ToArray();
        }

        /// <summary>Reads one kind out of a set, tolerating a set of the wrong length.</summary>
        public static bool Get(bool[] enabled, AnomalyKind kind) =>
            enabled != null && enabled.Length == AnomalyCatalog.Count &&
            enabled[AnomalyCatalog.Index(kind)];

        /// <summary>
        /// The set implied by settings that stored the flag per channel, which is what every file
        /// written before this looked like.
        ///
        /// The union, not the intersection: the flag was on Dropout on every channel, and if
        /// somebody had switched a kind on for one camera they meant to be watching for it. Taking
        /// the intersection would silently stop watching for it everywhere.
        /// </summary>
        public static bool[] UnionOf(IEnumerable<DetectionSettings> channels)
        {
            if (channels == null) return Default();

            var on = new bool[AnomalyCatalog.Count];
            bool any = false;
            foreach (DetectionSettings d in channels)
            {
                if (d == null) continue;
                foreach (AnomalyKind k in AnomalyCatalog.All)
                {
                    // WasEnabled, not Enabled: the flag this reads was deserialized into a legacy
                    // property, because the change that moved it out of here also stopped
                    // deserializing it - a union over the live flag would read every channel as off.
                    if (!d.For(k).WasEnabled) continue;
                    on[AnomalyCatalog.Index(k)] = true;
                    any = true;
                }
            }
            return any ? on : Default();
        }

        /// <summary>
        /// One line for a status area: what is on, and how many are not.
        ///
        /// Both halves matter. Naming only what is on would let a panel with nothing to report read
        /// as "nothing wrong" when six of seven things are not being looked at.
        /// </summary>
        public static string Describe(bool[] enabled)
        {
            var sb = new StringBuilder();
            int off = 0;
            foreach (AnomalyKind k in AnomalyCatalog.All)
            {
                if (Get(enabled, k))
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(k.ToString());
                    if (!AnomalyCatalog.Implemented(k)) sb.Append(" (미구현)");
                }
                else off++;
            }

            if (sb.Length == 0) return $"none — all {AnomalyCatalog.Count} kinds off";
            return off > 0 ? $"{sb} · {off} off" : sb.ToString();
        }
    }
}
