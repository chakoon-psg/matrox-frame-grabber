using System;
using System.Collections.Generic;
using System.Text;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>One file ffmpeg should write from the piped stream.</summary>
    public readonly struct FfmpegOutput
    {
        /// <summary>Destination. A segmented output needs a printf pattern, e.g. seg_%05d.mp4.</summary>
        public readonly string PathOrPattern;

        /// <summary>The rate this file declares. Not the source rate when frames are skipped.</summary>
        public readonly double Fps;

        /// <summary>Frames between keyframes; 0 leaves x264's default (250).</summary>
        public readonly int KeyframeInterval;

        /// <summary>Seconds per file; 0 writes one file.</summary>
        public readonly double SegmentSeconds;

        /// <summary>CSV of filename,start,end written as each segment closes. Null for none.</summary>
        public readonly string SegmentListPath;

        /// <summary>
        /// What this file does to the pixels. Per output rather than per process because the two
        /// tiers want different answers: the session file is whatever the operator chose, while the
        /// segment ring is H.264 whatever that is - it is context, it is written continuously, and
        /// a lossless ring would be 100 to 500 times the bytes for the life of the drive.
        /// </summary>
        public readonly VideoEncoding Encoding;

        public FfmpegOutput(string pathOrPattern, double fps, int keyframeInterval = 0,
                            double segmentSeconds = 0.0, string segmentListPath = null,
                            VideoEncoding encoding = VideoEncoding.H264)
        {
            PathOrPattern = pathOrPattern;
            Fps = fps;
            KeyframeInterval = keyframeInterval;
            SegmentSeconds = segmentSeconds;
            SegmentListPath = segmentListPath;
            Encoding = encoding;
        }

        public bool IsSegmented => SegmentSeconds > 0.0;
    }

    /// <summary>
    /// Builds the ffmpeg command line for a piped rawvideo stream.
    ///
    /// Separated from the process plumbing because the argument string is where a recording defect
    /// hid and could not be seen: -framerate sets the *input* rate, and with no -r the output rate
    /// comes from the demuxer's estimated tbr, which rawvideo's probe snaps to a standard value. It
    /// read 120 for a 124.316 fps stream and dropped 3.5% of the frames converting to it. Every
    /// output therefore names its own rate, and a test asserts it rather than a person reading the
    /// line. Measured 2026-09-10: 7462 frames fed, 7204 in the file, before -r was added.
    /// </summary>
    public static class FfmpegArgs
    {
        /// <summary>Even dimensions for H.264. A no-op at 1024x772; a guard for anything else.</summary>
        private const string EvenCrop = "crop=trunc(iw/2)*2:trunc(ih/2)*2";

        /// <summary>
        /// Builds the whole command line. <paramref name="bands"/> selects the pixel format: 3
        /// bands are fed planar as gbrp because MbufGetColor's packing paths do not work on these
        /// buffers, 1 band as gray.
        /// </summary>
        public static string Build(int width, int height, int bands, double inputFps,
                                   IReadOnlyList<FfmpegOutput> outputs)
        {
            if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
            if (!VideoRatePolicy.Usable(inputFps)) throw new ArgumentOutOfRangeException(nameof(inputFps));
            if (outputs == null || outputs.Count == 0) throw new ArgumentException("no outputs", nameof(outputs));

            string pixFmt = bands >= 3 ? "gbrp" : "gray";
            var sb = new StringBuilder();

            sb.Append("-hide_banner -loglevel warning ")
              .Append("-f rawvideo -pixel_format ").Append(pixFmt)
              .Append(" -video_size ").Append(width).Append('x').Append(height)
              .Append(" -framerate ").Append(VideoRatePolicy.Format(inputFps))
              .Append(" -i pipe:0 -an");

            // -map is only needed once there is more than one output, and leaving it out of the
            // single-output case keeps that command line identical to the one already in service.
            bool map = outputs.Count > 1;

            foreach (FfmpegOutput o in outputs)
            {
                if (string.IsNullOrWhiteSpace(o.PathOrPattern))
                    throw new ArgumentException("output without a path", nameof(outputs));
                if (!VideoRatePolicy.Usable(o.Fps))
                    throw new ArgumentException("output without a usable rate", nameof(outputs));
                if (o.IsSegmented && !o.PathOrPattern.Contains("%"))
                    throw new ArgumentException("a segmented output needs a printf pattern", nameof(outputs));

                // ffmpeg picks the muxer from the extension, so a mismatch here does not produce
                // the file that was asked for: utvideo into .mp4 is refused outright, and rawvideo
                // into .mkv too. Caught as an argument error rather than as a failed launch.
                string wanted = "." + VideoCodecs.Extension(o.Encoding);
                if (!o.PathOrPattern.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"{o.Encoding} writes {wanted}, not {o.PathOrPattern}", nameof(outputs));

                if (map) sb.Append(" -map 0:v");

                // Only H.264 needs even dimensions. A lossless output is not cropped at all: it
                // exists so the numbers can be measured again, and dropping a row to please an
                // encoder is the kind of quiet difference it is there to rule out.
                if (VideoCodecs.NeedsEvenDimensions(o.Encoding))
                    sb.Append(" -vf \"").Append(EvenCrop).Append('"');

                sb.Append(" -r ").Append(VideoRatePolicy.Format(o.Fps));
                sb.Append(' ').Append(VideoCodecs.CodecArgs(o.Encoding, bands));

                // Keyframes are an inter-frame idea. The lossless encoders here are intra-only, so
                // every frame is already a keyframe and -g would be refused or ignored.
                if (o.KeyframeInterval > 0 && o.Encoding == VideoEncoding.H264)
                    sb.Append(" -g ").Append(o.KeyframeInterval);

                if (o.IsSegmented)
                {
                    sb.Append(" -f segment -segment_time ")
                      .Append(VideoRatePolicy.Format(o.SegmentSeconds))
                      .Append(" -segment_format ").Append(VideoCodecs.SegmentFormat(o.Encoding))
                      .Append(" -reset_timestamps 1");
                    if (!string.IsNullOrWhiteSpace(o.SegmentListPath))
                        sb.Append(" -segment_list \"").Append(o.SegmentListPath)
                          .Append("\" -segment_list_type csv");
                }
                else if (VideoCodecs.WantsFastStart(o.Encoding))
                {
                    // faststart rewrites the index to the front once the file is closed, which a
                    // segmented output cannot use - each segment is closed by the muxer itself -
                    // and which only MP4 has.
                    sb.Append(" -movflags +faststart");
                }

                sb.Append(" -y \"").Append(o.PathOrPattern).Append('"');
            }

            return sb.ToString();
        }
    }
}
