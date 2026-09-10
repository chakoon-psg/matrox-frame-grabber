using System;
using System.Globalization;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What a recording does to the pixels.
    ///
    /// Three, not two, because "compressed or not" is the wrong axis: lossless compression keeps
    /// the pixels bit-exact *and* is smaller and no slower than writing them raw (measured below),
    /// so the interesting choice is whether the file has to be re-measurable. The raw option only
    /// exists for a reader that has no codec at all.
    /// </summary>
    public enum VideoEncoding
    {
        /// <summary>H.264 (AVC) in MP4. Small, watchable anywhere, and lossy.</summary>
        H264 = 0,

        /// <summary>Bit-exact, compressed. Ut Video in Matroska for colour, FFV1 for mono.</summary>
        Lossless = 1,

        /// <summary>Bit-exact, not compressed at all. rawvideo in QuickTime.</summary>
        Uncompressed = 2,
    }

    /// <summary>
    /// Which container a recording is wrapped in, where the encoding does not decide it on its own.
    ///
    /// It exists for one measured reason. Killing ffmpeg mid-write - a power cut, which is a thing
    /// that happens to a rig running unattended for months - leaves an MP4 with **nothing
    /// readable**: measured 2026-09-11, 7.08 MB on disk and 0 frames recoverable, with and without
    /// faststart, because the index is only written when the file closes. The same kill against
    /// MPEG-TS gave back 1480 frames and a correct 11.93 s duration; Matroska gave the frames but
    /// no duration. TS costs 4% more bytes and remuxes to MP4 with a stream copy in 0.07 s, so
    /// nothing is lost by recording in it and converting on the way out.
    /// </summary>
    public enum VideoContainer
    {
        /// <summary>Whatever the encoding's own container is - MP4 for H.264.</summary>
        Default = 0,

        /// <summary>
        /// MPEG-TS. For a recording that must still be readable if the machine dies mid-file.
        /// </summary>
        MpegTs = 1,
    }

    /// <summary>
    /// Which encoder, which container, and what it costs. Measured on this machine 2026-09-10
    /// (16 threads, C: an NVMe sustaining 2.75-3.46 GB/s), feeding real 1024x772 3-band frames and
    /// again with incompressible noise as the floor:
    ///
    ///                       real frames        noise (floor)      2064x1544 noise
    ///   rawvideo bgr24      1416 fps  1.00x    same               383 fps
    ///   utvideo   gbrp      1089 fps  4.20x     681 fps  1.00x    165 fps
    ///   ffv1 s16  gbrp       357 fps  6.17x     199 fps  0.90x      54 fps
    ///   libx264 crf23        597 fps  n/a       316 fps 10.96x      79 fps
    ///
    /// (h264 ratios from a looped source mean nothing; the real 10 s clips from the same run
    /// measured 188-261 kb/s on a dark scene.)
    ///
    /// Three things reading the ffmpeg docs would not have told us, each verified by comparing
    /// frame hashes through a round trip:
    ///
    /// 1. **gbrp rawvideo into AVI produces an unreadable file** and ffmpeg only warns - "gbrp
    ///    rawvideo cannot be written to avi, output file will be unreadable". It wrote 5.7 GB
    ///    anyway and read them back as bgr24, so the planes come out permuted. Uncompressed
    ///    therefore packs to bgr24, which round-trips bit-exact.
    ///
    ///    AVI then failed a second time and worse, because this one loses frames silently. A real
    ///    12 s recording - 3.54 GB, 1492 frames, all of them in the file - decoded as 1441 frames:
    ///    the default probe reads a large raw AVI's rate as 120 (it sees about two of these
    ///    2.37 MB frames inside the 5 MB probe window) and the conversion to 120 drops 3.4% of
    ///    them. `-probesize 400M` reads 124.32 and all 1492 come back, but a file that needs a flag
    ///    to be read correctly is not evidence. QuickTime stores per-sample durations - tbn 31079,
    ///    the exact rate - and read 1800 of 1800 frames from a 4.27 GB file with no flags at all.
    ///    So Uncompressed writes .mov.
    /// 2. **FFV1 expands incompressible content** (0.90x) and cannot keep up at full resolution
    ///    (54 fps against the 124.3 needed). Ut Video never passed 1.00x and is as fast as raw.
    /// 3. **Ut Video has no gray**, and gray routed through its yuv444p is not bit-exact -
    ///    measured, the hashes differ. Mono lossless is FFV1 instead.
    /// </summary>
    public static class VideoCodecs
    {
        /// <summary>File extension, which the container is chosen by.</summary>
        public static string Extension(VideoEncoding encoding) =>
            Extension(encoding, VideoContainer.Default);

        /// <summary>File extension for an explicit container.</summary>
        public static string Extension(VideoEncoding encoding, VideoContainer container)
        {
            if (container == VideoContainer.MpegTs) return "ts";
            switch (encoding)
            {
                case VideoEncoding.Lossless: return "mkv";
                case VideoEncoding.Uncompressed: return "mov";
                default: return "mp4";
            }
        }

        /// <summary>ffmpeg's name for that container, for -segment_format.</summary>
        public static string SegmentFormat(VideoEncoding encoding) =>
            SegmentFormat(encoding, VideoContainer.Default);

        /// <summary>ffmpeg's muxer name for an explicit container.</summary>
        public static string SegmentFormat(VideoEncoding encoding, VideoContainer container)
        {
            if (container == VideoContainer.MpegTs) return "mpegts";
            switch (encoding)
            {
                case VideoEncoding.Lossless: return "matroska";
                case VideoEncoding.Uncompressed: return "mov";
                default: return "mp4";
            }
        }

        /// <summary>
        /// Whether the encoding can be wrapped in the container at all.
        ///
        /// MPEG-TS carries H.264 and nothing else we write: it has no mapping for Ut Video, and raw
        /// RGB in TS is not a thing. Refused rather than attempted, because ffmpeg's failure for a
        /// bad pairing is a launch error the operator sees as "recording did not start".
        /// </summary>
        public static bool Supports(VideoEncoding encoding, VideoContainer container) =>
            container != VideoContainer.MpegTs || encoding == VideoEncoding.H264;

        /// <summary>
        /// The encoder and its pixel format. Colour arrives planar gbrp - see the MbufGetColor
        /// trap - and every choice here starts from that.
        /// </summary>
        public static string CodecArgs(VideoEncoding encoding, int bands)
        {
            bool color = bands >= 3;
            switch (encoding)
            {
                case VideoEncoding.Lossless:
                    // Ut Video for colour: bit-exact straight from gbrp, and it never came out
                    // bigger than raw. FFV1 for mono, because Ut Video has no gray and routing
                    // gray through yuv444p is not bit-exact.
                    return color
                        ? "-c:v utvideo -pix_fmt gbrp"
                        : "-c:v ffv1 -level 3 -pix_fmt gray -slices 16 -slicecrc 0";

                case VideoEncoding.Uncompressed:
                    // bgr24, not gbrp: see the container traps above. 24BG in QuickTime is the
                    // long-standing uncompressed-RGB pairing, and editors read it.
                    return color ? "-c:v rawvideo -pix_fmt bgr24" : "-c:v rawvideo -pix_fmt gray";

                default:
                    return "-c:v libx264 -preset veryfast -pix_fmt yuv420p";
            }
        }

        /// <summary>Whether the file can be measured again and give the same answer.</summary>
        public static bool IsLossless(VideoEncoding encoding) => encoding != VideoEncoding.H264;

        /// <summary>
        /// Whether the frame has to be cropped to even dimensions. Only H.264 needs it, and a
        /// lossless output must not be cropped at all - dropping a row to please an encoder is
        /// exactly the quiet difference these files exist to rule out.
        /// </summary>
        public static bool NeedsEvenDimensions(VideoEncoding encoding) => encoding == VideoEncoding.H264;

        /// <summary>Whether the container rewrites its index on close. MP4 only.</summary>
        public static bool WantsFastStart(VideoEncoding encoding) =>
            WantsFastStart(encoding, VideoContainer.Default);

        /// <summary>Same, for an explicit container. TS has no index to move.</summary>
        public static bool WantsFastStart(VideoEncoding encoding, VideoContainer container) =>
            container != VideoContainer.MpegTs && encoding == VideoEncoding.H264;

        /// <summary>
        /// Whether a file in this container is still readable if the process writing it dies.
        ///
        /// Measured by killing ffmpeg 12 s into a write: MP4 gave back 0 frames, MPEG-TS gave back
        /// all 1480 with the right duration.
        /// </summary>
        public static bool SurvivesAKill(VideoContainer container) => container == VideoContainer.MpegTs;

        /// <summary>What this is, for a log line and the Rec tooltip.</summary>
        public static string Name(VideoEncoding encoding, int bands)
        {
            switch (encoding)
            {
                case VideoEncoding.Lossless: return bands >= 3 ? "utvideo" : "ffv1";
                case VideoEncoding.Uncompressed: return "rawvideo";
                default: return "libx264";
            }
        }

        /// <summary>
        /// Bytes per frame written, as a planning figure rather than a promise.
        ///
        /// Uncompressed is exact. Lossless uses the *floor* - the incompressible case, measured at
        /// 1.00x - because an estimate that assumed the 4.2x a dark frame gave would be wrong in
        /// the direction that fills a disk. H.264 has no honest per-frame figure and returns 0:
        /// the same encoder measured 188 kb/s on a still dark room and 27 MB/s on noise.
        /// </summary>
        public static double BytesPerFrame(VideoEncoding encoding, int width, int height, int bands)
        {
            if (width <= 0 || height <= 0) return 0.0;
            double raw = (double)width * height * (bands >= 3 ? 3 : 1);
            switch (encoding)
            {
                case VideoEncoding.Uncompressed: return raw;
                case VideoEncoding.Lossless: return raw;   // floor; real content measured 4.2x smaller
                default: return 0.0;
            }
        }

        /// <summary>
        /// One line for the settings window: what a minute of this costs. Empty for H.264, per
        /// <see cref="BytesPerFrame"/> - a made-up number there would be the only wrong one.
        /// </summary>
        public static string SizePerMinute(VideoEncoding encoding, int width, int height,
                                           int bands, double fps)
        {
            double perFrame = BytesPerFrame(encoding, width, height, bands);
            if (perFrame <= 0.0 || fps <= 1.0) return string.Empty;

            double gbPerMinute = perFrame * fps * 60.0 / 1e9;
            string figure = gbPerMinute >= 10.0
                ? gbPerMinute.ToString("0", CultureInfo.InvariantCulture)
                : gbPerMinute.ToString("0.0", CultureInfo.InvariantCulture);
            return encoding == VideoEncoding.Uncompressed
                ? figure + " GB/min"
                : "up to " + figure + " GB/min";
        }

        /// <summary>Parses a stored name, falling back to H.264 rather than to something expensive.</summary>
        public static VideoEncoding Parse(string name) =>
            Enum.TryParse(name, ignoreCase: true, out VideoEncoding e) ? e : VideoEncoding.H264;
    }
}
