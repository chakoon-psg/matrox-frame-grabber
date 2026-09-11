using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The ffmpeg command line. These exist because a recording lost 3.5% of its frames to a
    /// missing output rate and there was no way to see it: -framerate sets the input rate, and
    /// without -r the output rate came from rawvideo's probed tbr, which snapped 124.316 to 120.
    /// Measured 2026-09-10: 7462 frames fed, 7204 in the file.
    /// </summary>
    public class FfmpegArgsTests
    {
        const int W = 1024, H = 772;
        const double SourceFps = 124.316;

        static string One(double fps = SourceFps) =>
            FfmpegArgs.Build(W, H, 3, SourceFps, new[] { new FfmpegOutput("out.mp4", fps) });

        // ----- the defect -----

        /// <summary>
        /// Both rates, and both of them the same value for a full-rate output. The input rate alone
        /// is what the broken command line had.
        /// </summary>
        [Fact]
        public void Every_output_names_its_own_rate_as_well_as_the_input_rate()
        {
            string args = One();
            Assert.Contains("-framerate 124.316", args);
            Assert.Contains("-r 124.316", args);
        }

        /// <summary>
        /// The output rate is the file's, not the source's. A session tier taking every fourth
        /// frame declares 31.079 while the pipe still carries 124.316.
        /// </summary>
        [Fact]
        public void An_output_that_skips_frames_declares_its_own_rate_not_the_sources()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps,
                new[] { new FfmpegOutput("session.mp4", VideoRatePolicy.FileFps(SourceFps, 4)) });

            Assert.Contains("-framerate 124.316", args);
            Assert.Contains("-r 31.079", args);
            Assert.DoesNotContain("-r 124.316", args);
        }

        [Fact]
        public void An_output_without_a_usable_rate_is_refused()
        {
            Assert.Throws<ArgumentException>(() =>
                FfmpegArgs.Build(W, H, 3, SourceFps, new[] { new FfmpegOutput("out.mp4", 0.0) }));
        }

        // ----- one process, two files -----

        [Fact]
        public void Two_outputs_are_each_mapped_and_each_carry_their_own_rate()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps, new[]
            {
                new FfmpegOutput("seg_%05d.mp4", SourceFps, keyframeInterval: 62,
                                 segmentSeconds: 2.0, segmentListPath: "seg.csv"),
                new FfmpegOutput("session.mp4", VideoRatePolicy.FileFps(SourceFps, 4)),
            });

            Assert.Equal(2, CountOf(args, "-map 0:v"));
            Assert.Contains("-r 124.316", args);
            Assert.Contains("-r 31.079", args);
            Assert.Contains("seg_%05d.mp4", args);
            Assert.Contains("session.mp4", args);
        }

        /// <summary>
        /// -map is unnecessary for a lone output, and leaving it out keeps the single-output line
        /// identical to the one already in service.
        /// </summary>
        [Fact]
        public void A_lone_output_is_not_mapped()
        {
            Assert.DoesNotContain("-map", One());
        }

        // ----- segments -----

        [Fact]
        public void A_segmented_output_asks_for_the_segment_muxer_and_a_list()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps, new[]
            {
                new FfmpegOutput("seg_%05d.mp4", SourceFps, keyframeInterval: 62,
                                 segmentSeconds: 2.0, segmentListPath: "seg.csv"),
            });

            Assert.Contains("-f segment -segment_time 2", args);
            Assert.Contains("-reset_timestamps 1", args);
            Assert.Contains("-segment_list \"seg.csv\" -segment_list_type csv", args);
            Assert.Contains("-g 62", args);
        }

        /// <summary>
        /// faststart rewrites the index once a file closes, which is the muxer's job for a
        /// segmented output rather than the caller's.
        /// </summary>
        [Fact]
        public void A_segmented_output_does_not_ask_for_faststart()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps,
                new[] { new FfmpegOutput("seg_%05d.mp4", SourceFps, segmentSeconds: 2.0) });

            Assert.DoesNotContain("+faststart", args);
        }

        [Fact]
        public void A_single_file_output_asks_for_faststart()
        {
            Assert.Contains("-movflags +faststart", One());
        }

        /// <summary>
        /// Without a pattern the segment muxer overwrites one file, and the failure looks like a
        /// recording that only ever holds the last two seconds.
        /// </summary>
        [Fact]
        public void A_segmented_output_without_a_pattern_is_refused()
        {
            Assert.Throws<ArgumentException>(() =>
                FfmpegArgs.Build(W, H, 3, SourceFps,
                    new[] { new FfmpegOutput("seg.mp4", SourceFps, segmentSeconds: 2.0) }));
        }

        [Fact]
        public void A_keyframe_interval_of_zero_leaves_the_encoder_default()
        {
            Assert.DoesNotContain(" -g ", One());
        }

        // ----- pixel format -----

        /// <summary>
        /// Three bands are fed planar because MbufGetColor's packing paths do not work on these
        /// buffers - the format here has to match how the bytes are actually written.
        /// </summary>
        [Fact]
        public void Three_bands_are_planar_and_one_band_is_gray()
        {
            Assert.Contains("-pixel_format gbrp", One());
            Assert.Contains("-pixel_format gray",
                FfmpegArgs.Build(W, H, 1, SourceFps, new[] { new FfmpegOutput("m.mp4", SourceFps) }));
        }

        [Fact]
        public void Geometry_and_inputs_are_validated()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FfmpegArgs.Build(0, H, 3, SourceFps, new[] { new FfmpegOutput("o.mp4", SourceFps) }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FfmpegArgs.Build(W, H, 3, 0.0, new[] { new FfmpegOutput("o.mp4", SourceFps) }));
            Assert.Throws<ArgumentException>(() =>
                FfmpegArgs.Build(W, H, 3, SourceFps, new FfmpegOutput[0]));
        }

        // ----- encoding -----

        /// <summary>
        /// The lossless line, end to end. Verified by round trip 2026-09-10: the frame hashes out
        /// of this file matched the gbrp fed in.
        /// </summary>
        [Fact]
        public void The_lossless_output_is_utvideo_uncropped_and_unindexed()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps,
                new[] { new FfmpegOutput("Camera_0.mkv", SourceFps,
                                         encoding: VideoEncoding.Lossless) });

            Assert.Contains("-c:v utvideo -pix_fmt gbrp", args);
            Assert.DoesNotContain("libx264", args);
            // No crop: the file exists so the deviation can be measured again, and dropping a row
            // to please an encoder is the sort of quiet difference it is there to rule out.
            Assert.DoesNotContain("crop=", args);
            // faststart is an MP4 idea.
            Assert.DoesNotContain("+faststart", args);
            Assert.Contains("-r 124.316", args);
        }

        [Fact]
        public void The_uncompressed_output_is_packed_bgr24_even_though_the_pipe_is_planar()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps,
                new[] { new FfmpegOutput("Camera_0.mov", SourceFps,
                                         encoding: VideoEncoding.Uncompressed) });

            // The input side stays planar - that is how the bytes are written - and only the
            // output packs. gbrp rawvideo in AVI is written and then read back permuted.
            Assert.Contains("-pixel_format gbrp", args);
            Assert.Contains("-c:v rawvideo -pix_fmt bgr24", args);
        }

        /// <summary>
        /// ffmpeg picks the muxer from the extension, so a mismatch does not write the file that
        /// was asked for - utvideo into .mp4 is refused outright. Caught here rather than as a
        /// failed launch with the recording button already lit.
        /// </summary>
        [Fact]
        public void An_output_whose_extension_does_not_match_its_encoding_is_refused()
        {
            Assert.Throws<ArgumentException>(() =>
                FfmpegArgs.Build(W, H, 3, SourceFps,
                    new[] { new FfmpegOutput("out.mp4", SourceFps, encoding: VideoEncoding.Lossless) }));
            Assert.Throws<ArgumentException>(() =>
                FfmpegArgs.Build(W, H, 3, SourceFps,
                    new[] { new FfmpegOutput("out.mkv", SourceFps, encoding: VideoEncoding.Uncompressed) }));
            Assert.Throws<ArgumentException>(() =>
                FfmpegArgs.Build(W, H, 3, SourceFps,
                    new[] { new FfmpegOutput("out.mov", SourceFps) }));
        }

        /// <summary>
        /// A keyframe interval is an inter-frame idea. Both lossless encoders here are intra-only,
        /// so every frame is already a keyframe and -g would be ignored or refused.
        /// </summary>
        [Fact]
        public void A_keyframe_interval_is_not_asked_of_an_intra_only_encoder()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps,
                new[] { new FfmpegOutput("seg_%05d.mkv", SourceFps, keyframeInterval: 62,
                                         segmentSeconds: 2.0, encoding: VideoEncoding.Lossless) });

            Assert.DoesNotContain(" -g ", args);
            Assert.Contains("-segment_format matroska", args);
        }

        /// <summary>
        /// Mono lossless is FFV1, because Ut Video has no gray and gray through its yuv444p is not
        /// bit-exact - measured, the hashes differ.
        /// </summary>
        [Fact]
        public void A_mono_lossless_output_is_ffv1_and_stays_gray()
        {
            string args = FfmpegArgs.Build(W, H, 1, SourceFps,
                new[] { new FfmpegOutput("m.mkv", SourceFps, encoding: VideoEncoding.Lossless) });

            Assert.Contains("-pixel_format gray", args);
            Assert.Contains("-c:v ffv1", args);
            Assert.Contains("-pix_fmt gray", args);
        }

        /// <summary>
        /// The two tiers in one process, each with its own encoding: this is the shape the design
        /// rests on - a lossless session file beside an H.264 ring, because the ring is written
        /// continuously and lossless would be 884 MB/s across three channels.
        /// </summary>
        [Fact]
        public void Two_outputs_can_be_encoded_differently_in_one_process()
        {
            string args = FfmpegArgs.Build(W, H, 3, SourceFps, new[]
            {
                new FfmpegOutput("seg_%05d.mp4", SourceFps, keyframeInterval: 62,
                                 segmentSeconds: 2.0, segmentListPath: "seg.csv"),
                new FfmpegOutput("session.mkv", SourceFps, encoding: VideoEncoding.Lossless),
            });

            Assert.Contains("libx264", args);
            Assert.Contains("utvideo", args);
            Assert.Equal(2, CountOf(args, "-map 0:v"));
        }

        // ----- the line that is already in service -----

        /// <summary>
        /// Locks the single-output command line to the one that recorded 7462 of 7462 frames at
        /// 31079/250 on 2026-09-10, so this extraction is provably a refactor.
        /// </summary>
        [Fact]
        public void The_single_output_line_is_the_one_that_was_measured()
        {
            Assert.Equal(
                "-hide_banner -loglevel warning -f rawvideo -pixel_format gbrp " +
                "-video_size 1024x772 -framerate 124.316 -i pipe:0 -an " +
                "-vf \"crop=trunc(iw/2)*2:trunc(ih/2)*2\" -r 124.316 " +
                "-c:v libx264 -preset veryfast -pix_fmt yuv420p -movflags +faststart " +
                "-y \"C:\\out\\Camera_0.mp4\"",
                FfmpegArgs.Build(1024, 772, 3, 124.316,
                    new[] { new FfmpegOutput("C:\\out\\Camera_0.mp4", 124.316) }));
        }

        static int CountOf(string haystack, string needle)
        {
            int n = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }
    }
}
