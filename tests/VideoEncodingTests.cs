using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// What each encoding writes, and what it costs.
    ///
    /// These exist because two of the three choices were only safe to offer after measuring them,
    /// and one of the measurements contradicted the obvious answer: planar gbrp fed to rawvideo in
    /// AVI produces a file ffmpeg itself calls unreadable, with nothing but a warning to say so.
    /// Packing to bgr24 round-trips bit-exact instead, and the container has to follow the codec.
    /// </summary>
    public class VideoCodecsTests
    {
        // The current operating point, and the geometry the estimates were measured at.
        const int W = 1024, H = 772, Bands = 3;
        const double Fps = 124.316;

        [Fact]
        public void Each_encoding_writes_the_container_its_codec_can_live_in()
        {
            Assert.Equal("mp4", VideoCodecs.Extension(VideoEncoding.H264));
            Assert.Equal("mkv", VideoCodecs.Extension(VideoEncoding.Lossless));
            Assert.Equal("mov", VideoCodecs.Extension(VideoEncoding.Uncompressed));

            // Measured: utvideo into mp4 is refused outright, and rawvideo into matroska is too -
            // "Raw RGB is not supported Natively in Matroska, you can use AVI or NUT".
            Assert.Equal("matroska", VideoCodecs.SegmentFormat(VideoEncoding.Lossless));
            Assert.Equal("mov", VideoCodecs.SegmentFormat(VideoEncoding.Uncompressed));
        }

        /// <summary>
        /// Two traps in one line. Planar gbrp into a raw container must be packed - ffmpeg wrote
        /// 5.7 GB of gbrp AVI on a warning alone and read the planes back permuted - and the
        /// container has to be QuickTime rather than AVI, because a 3.54 GB raw AVI probes as
        /// 120 fps by default and 3.4% of its frames are dropped on the way out.
        /// </summary>
        [Fact]
        public void Uncompressed_colour_is_packed_bgr24_in_quicktime()
        {
            string args = VideoCodecs.CodecArgs(VideoEncoding.Uncompressed, 3);
            Assert.Contains("-c:v rawvideo", args);
            Assert.Contains("bgr24", args);
            Assert.DoesNotContain("gbrp", args);
            Assert.Equal("mov", VideoCodecs.Extension(VideoEncoding.Uncompressed));
        }

        /// <summary>
        /// Ut Video for colour, FFV1 for mono, and the reason is measured: Ut Video has no gray at
        /// all, and gray routed through its yuv444p came back with different frame hashes.
        /// </summary>
        [Fact]
        public void Lossless_is_utvideo_for_colour_and_ffv1_for_mono()
        {
            Assert.Contains("utvideo", VideoCodecs.CodecArgs(VideoEncoding.Lossless, 3));
            Assert.Contains("gbrp", VideoCodecs.CodecArgs(VideoEncoding.Lossless, 3));

            string mono = VideoCodecs.CodecArgs(VideoEncoding.Lossless, 1);
            Assert.Contains("ffv1", mono);
            Assert.Contains("gray", mono);
            Assert.DoesNotContain("utvideo", mono);
        }

        [Fact]
        public void Only_h264_is_lossy_and_only_h264_needs_even_dimensions()
        {
            Assert.False(VideoCodecs.IsLossless(VideoEncoding.H264));
            Assert.True(VideoCodecs.IsLossless(VideoEncoding.Lossless));
            Assert.True(VideoCodecs.IsLossless(VideoEncoding.Uncompressed));

            Assert.True(VideoCodecs.NeedsEvenDimensions(VideoEncoding.H264));
            Assert.False(VideoCodecs.NeedsEvenDimensions(VideoEncoding.Lossless));
            Assert.False(VideoCodecs.NeedsEvenDimensions(VideoEncoding.Uncompressed));
        }

        /// <summary>
        /// The figure the operator is shown before filling a disk. 1024x772x3 at 124.316 fps is
        /// 295 MB/s, which is 17.7 GB a minute; the full sensor is four times that.
        /// </summary>
        [Fact]
        public void Uncompressed_states_what_a_minute_costs()
        {
            Assert.Equal("18 GB/min", VideoCodecs.SizePerMinute(VideoEncoding.Uncompressed, W, H, Bands, Fps));
            Assert.Equal("71 GB/min", VideoCodecs.SizePerMinute(VideoEncoding.Uncompressed, 2064, 1544, Bands, Fps));
        }

        /// <summary>
        /// Lossless is quoted as a ceiling, because the ratio is the content's business: 4.20x on a
        /// real frame and 1.00x on noise. An estimate built on the 4.20 would be wrong in the
        /// direction that fills a disk.
        /// </summary>
        [Fact]
        public void Lossless_is_quoted_as_an_upper_bound_not_a_promise()
        {
            string s = VideoCodecs.SizePerMinute(VideoEncoding.Lossless, W, H, Bands, Fps);
            Assert.StartsWith("up to ", s);
            Assert.Contains("18 GB/min", s);
        }

        /// <summary>
        /// H.264 gets no figure at all. The same encoder measured 188 kb/s on a still dark room and
        /// 27 MB/s on noise - five orders of magnitude - so any number here would be the only wrong
        /// one on the window.
        /// </summary>
        [Fact]
        public void H264_offers_no_size_estimate_because_it_has_no_honest_one()
        {
            Assert.Equal(0.0, VideoCodecs.BytesPerFrame(VideoEncoding.H264, W, H, Bands));
            Assert.Equal(string.Empty, VideoCodecs.SizePerMinute(VideoEncoding.H264, W, H, Bands, Fps));
        }

        [Fact]
        public void Bytes_per_frame_is_the_raw_payload_for_both_bit_exact_kinds()
        {
            Assert.Equal(W * H * 3, VideoCodecs.BytesPerFrame(VideoEncoding.Uncompressed, W, H, 3), 3);
            Assert.Equal(W * H * 3, VideoCodecs.BytesPerFrame(VideoEncoding.Lossless, W, H, 3), 3);
            Assert.Equal(W * H, VideoCodecs.BytesPerFrame(VideoEncoding.Uncompressed, W, H, 1), 3);
        }

        [Fact]
        public void No_geometry_and_no_rate_produce_no_estimate_rather_than_a_zero()
        {
            Assert.Equal(string.Empty, VideoCodecs.SizePerMinute(VideoEncoding.Uncompressed, 0, 0, 3, Fps));
            Assert.Equal(string.Empty, VideoCodecs.SizePerMinute(VideoEncoding.Uncompressed, W, H, 3, 0.0));
        }

        /// <summary>
        /// An unrecognised name has to land on the cheap one. A settings file from a later version,
        /// or a typo, must not select the encoding that writes 71 GB a minute.
        /// </summary>
        [Fact]
        public void An_unknown_stored_name_falls_back_to_h264()
        {
            Assert.Equal(VideoEncoding.Lossless, VideoCodecs.Parse("Lossless"));
            Assert.Equal(VideoEncoding.Lossless, VideoCodecs.Parse("lossless"));
            Assert.Equal(VideoEncoding.Uncompressed, VideoCodecs.Parse("Uncompressed"));
            Assert.Equal(VideoEncoding.H264, VideoCodecs.Parse("Ffv1WithBellsOn"));
            Assert.Equal(VideoEncoding.H264, VideoCodecs.Parse(null));
            Assert.Equal(VideoEncoding.H264, VideoCodecs.Parse(""));
        }
    }
}
