using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>What to do when the local disk is running out.</summary>
    public enum LowSpacePolicy
    {
        /// <summary>
        /// Keep running by discarding the oldest context recording. The right answer for a
        /// durability test, where the interesting moment is in the future rather than the past.
        /// </summary>
        DeleteOldestContext = 0,

        /// <summary>
        /// Stop the continuous recording and say so. For a lab whose rule is that nothing is lost
        /// without a person deciding it. There is no automatic resume - that is the point.
        /// </summary>
        StopRecording = 1,
    }

    /// <summary>What the policy says to do right now.</summary>
    public enum StorageAction
    {
        /// <summary>There is room. Carry on.</summary>
        Nothing = 0,

        /// <summary>Delete the oldest context recording and look again.</summary>
        DeleteOldest = 1,

        /// <summary>Stop the continuous recording and raise it.</summary>
        StopRecording = 2,
    }

    /// <summary>
    /// When the local disk runs low, what gives.
    ///
    /// The local folder is a staging area: the continuous recording is written here and a mover
    /// ships it to the NAS. If the NAS is unreachable the folder grows, and something has to give
    /// before the disk is full - a full disk does not just stop the recording, it takes the
    /// detection and the evidence with it.
    ///
    /// **What gives is never the evidence.** The anomaly clips and stills are the product of a
    /// reliability test and a controlled record; deleting them to make room for context video
    /// would be the wrong trade in every case, and offering it as an option would let somebody
    /// configure a controlled record away. So the deletion is confined to the continuous folder by
    /// construction rather than by a filename rule, and if evidence alone fills the disk the
    /// answer is to stop and say so.
    ///
    /// The ordering that makes the dangerous branch rare is not here: a mover that verifies a file
    /// on the NAS and then deletes the local copy keeps the folder small on its own, so in normal
    /// operation nothing below ever runs.
    /// </summary>
    public static class StoragePolicy
    {
        /// <summary>
        /// The floor under any reserve, whatever the arithmetic below says.
        ///
        /// A disk with less than this free is a machine about to misbehave in ways that have
        /// nothing to do with recording - Windows itself wants room.
        /// </summary>
        public const double FloorGb = 2.0;

        /// <summary>
        /// Events the reserve should be able to absorb before anybody intervenes. Four is a
        /// judgement, not a measurement: enough that a burst does not immediately breach it.
        /// </summary>
        public const int EventsOfHeadroom = 4;

        /// <summary>
        /// The smallest reserve that still lets the rig do its job, at the operating point it is
        /// actually running.
        ///
        /// A reserve below this is worse than none: the disk fills at the moment an anomaly needs
        /// writing, so the one file the whole rig exists to produce is the one that fails. It is
        /// computed rather than fixed because the geometry moves - the evidence for one event is
        /// four stills at 2.26 MiB each at decim 2, and four times that at decim 1.
        /// </summary>
        public static double MinimumReserveGb(int channels, long evidenceBytesPerEvent,
                                              long segmentBytes)
        {
            if (channels < 1) channels = 1;
            if (evidenceBytesPerEvent < 0) evidenceBytesPerEvent = 0;
            if (segmentBytes < 0) segmentBytes = 0;

            double perChannel = evidenceBytesPerEvent * (double)EventsOfHeadroom + segmentBytes;
            return FloorGb + channels * perChannel / 1e9;
        }

        /// <summary>
        /// The reserve to use: what was asked for, but never below what one operating point needs.
        ///
        /// Clamped rather than refused, because the number in the settings window was typed by
        /// somebody who could not have known the minimum - it depends on the decimation in force.
        /// </summary>
        public static double ClampReserveGb(double wantedGb, double minimumGb)
        {
            if (double.IsNaN(wantedGb) || wantedGb < minimumGb) return minimumGb;
            return wantedGb > 100000.0 ? 100000.0 : wantedGb;
        }

        /// <summary>
        /// What to do, given the room left and what there is to delete.
        ///
        /// <paramref name="hasContextToDelete"/> is what makes the two policies converge in the
        /// worst case: with nothing left to delete, "delete the oldest" cannot help and the honest
        /// answer is the same as the other policy's - stop, and say so. That case means the
        /// evidence alone has filled the disk, which is a fault rather than a policy.
        /// </summary>
        public static StorageAction Decide(double freeGb, double reserveGb,
                                           LowSpacePolicy policy, bool hasContextToDelete)
        {
            if (freeGb >= reserveGb) return StorageAction.Nothing;

            if (policy == LowSpacePolicy.DeleteOldestContext && hasContextToDelete)
                return StorageAction.DeleteOldest;

            return StorageAction.StopRecording;
        }

        /// <summary>
        /// How much to free before stopping: the reserve plus a tenth of it.
        ///
        /// Deleting to exactly the reserve puts the next tick back over the line and turns the
        /// policy into a treadmill, one file per tick forever. A margin means it deletes a batch
        /// and then leaves it alone.
        /// </summary>
        public static double TargetGb(double reserveGb) => reserveGb * 1.1;

        /// <summary>One line for the status area.</summary>
        public static string Describe(double freeGb, double reserveGb, LowSpacePolicy policy)
        {
            string room = freeGb >= reserveGb
                ? $"{freeGb:F0} GB free"
                : $"{freeGb:F0} GB free — below the {reserveGb:F0} GB reserve";
            string what = policy == LowSpacePolicy.DeleteOldestContext
                ? "oldest context is deleted"
                : "the recording stops";
            return $"{room} · when low, {what}";
        }
    }
}
