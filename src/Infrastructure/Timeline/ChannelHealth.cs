using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What a lane's status dot says about a channel, worst first.
    ///
    /// Events alone miss the two most serious problems. A channel losing frames and a channel whose
    /// detector never sees the tiles both report nothing at all, and nothing is exactly what a
    /// healthy panel reports - so an empty lane is ambiguous until something else distinguishes
    /// "nothing happened" from "nothing is being measured".
    /// </summary>
    public enum ChannelHealth
    {
        /// <summary>No camera on this port. Nothing to say.</summary>
        Absent = 0,

        /// <summary>A camera, not running. Not a fault.</summary>
        Stopped = 1,

        /// <summary>Running, measuring, and calibrated.</summary>
        Healthy = 2,

        /// <summary>
        /// Running, but the threshold in force was never measured on this rig. The detector works;
        /// what it is comparing against is somebody's guess.
        /// </summary>
        Uncalibrated = 3,

        /// <summary>
        /// Clipping or crushing to black. The brightness figures are describing the sensor's limits
        /// rather than the panel, and a fall measured against a clipped baseline is not a fall.
        /// </summary>
        OpticsOutOfBand = 4,

        /// <summary>
        /// The event tier could not keep up, so the segments an anomaly clip is cut from are
        /// missing frames.
        ///
        /// Ranked above the optics because it is silent: luma and clipping are on screen, while a
        /// clip with holes is a file that exists, plays, and says nothing about what is not in it.
        /// </summary>
        ClipIncomplete = 5,

        /// <summary>
        /// The reducer produced grids the detector did not judge. Whatever the cause, frames are
        /// going past unexamined - the failure that looks most like a quiet panel.
        /// </summary>
        DetectorBlind = 6,

        /// <summary>
        /// Frames were lost in acquisition. Anything that happened in them is unrecoverable, and
        /// the strip must not imply the record is complete.
        /// </summary>
        FramesMissed = 7,
    }

    /// <summary>
    /// Turns a channel's counters into one status. MIL-free so the bands are assertions rather than
    /// something to discover by staring at a dot.
    /// </summary>
    public static class ChannelHealthRule
    {
        /// <summary>
        /// Above this share of sampled pixels clipping to white, the reading describes the sensor
        /// rather than the panel. One percent is the figure the legend already called out.
        /// </summary>
        public const double MaxClipPercent = 1.0;

        /// <summary>Same for pixels crushed to black.</summary>
        public const double MaxBlackPercent = 1.0;

        /// <summary>
        /// Luma outside this band is not a panel being watched. Deliberately wide: the dimmest of
        /// these three cameras runs at 53 against 65 and 66, which wants more light but is not a
        /// fault, and inventing a threshold between them would put an amber dot on a working rig.
        /// </summary>
        public const double MinLuma = 10.0;

        /// <summary>Upper end of the same band.</summary>
        public const double MaxLuma = 245.0;

        /// <summary>
        /// The worst thing true of this channel.
        ///
        /// Ordered by how much it invalidates the record: losing frames and not judging them both
        /// mean the measurement did not happen, which outranks anything about the picture.
        /// </summary>
        public static ChannelHealth Evaluate(bool present, bool grabbing,
                                             long framesMissed,
                                             long reductions, long gridsAccepted,
                                             long clipFramesSkipped,
                                             bool calibrated,
                                             double luma, double clipPercent, double blackPercent)
        {
            if (!present) return ChannelHealth.Absent;
            if (!grabbing) return ChannelHealth.Stopped;

            if (framesMissed > 0) return ChannelHealth.FramesMissed;
            if (reductions > 0 && gridsAccepted < reductions) return ChannelHealth.DetectorBlind;
            if (clipFramesSkipped > 0) return ChannelHealth.ClipIncomplete;

            if (clipPercent > MaxClipPercent || blackPercent > MaxBlackPercent ||
                luma < MinLuma || luma > MaxLuma)
                return ChannelHealth.OpticsOutOfBand;

            return calibrated ? ChannelHealth.Healthy : ChannelHealth.Uncalibrated;
        }

        /// <summary>Whether this state needs someone's attention now.</summary>
        public static bool IsFault(ChannelHealth h) =>
            h == ChannelHealth.DetectorBlind ||
            h == ChannelHealth.FramesMissed ||
            h == ChannelHealth.ClipIncomplete;

        /// <summary>One line for the lane's tooltip, in the operator's terms.</summary>
        public static string Describe(ChannelHealth h)
        {
            switch (h)
            {
                case ChannelHealth.Absent: return "카메라 없음";
                case ChannelHealth.Stopped: return "정지";
                case ChannelHealth.Healthy: return "정상";
                case ChannelHealth.Uncalibrated: return "임계값이 실측된 적 없음";
                case ChannelHealth.OpticsOutOfBand: return "광학 범위 밖 — 밝기가 센서 한계를 재고 있습니다";
                case ChannelHealth.ClipIncomplete: return "사건 클립에 구멍 — 인코더가 프레임을 놓쳤습니다";
                case ChannelHealth.DetectorBlind: return "검지기 실명 — 판정되지 않은 프레임이 있습니다";
                case ChannelHealth.FramesMissed: return "프레임 유실 — 그 사이 일은 복구할 수 없습니다";
                default: return string.Empty;
            }
        }
    }
}
