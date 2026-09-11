using System;
using System.Threading.Tasks;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The hold count that lets two sinks write one extraction.
    ///
    /// This is the risky half of removing the second extract: get the counting wrong and either a
    /// buffer is reused while a writer is still reading it - a corrupted recording, which is worse
    /// than the lost frames it was meant to fix - or it is never freed and the pool runs dry, which
    /// shows up as skipped frames somewhere else entirely. Hence a test per rule.
    /// </summary>
    public class SharedFramePoolTests
    {
        static SharedFramePool Pool(int frames = 3, int bytes = 16) => new SharedFramePool(frames, bytes);

        [Fact]
        public void A_taken_frame_is_held_once_by_its_taker()
        {
            var p = Pool();
            byte[] f = p.Take();

            Assert.NotNull(f);
            Assert.Equal(16, f.Length);
            Assert.Equal(1, p.HoldsOn(f));
            Assert.Equal(1, p.InUse);
        }

        /// <summary>
        /// The whole point: two sinks hold it, and it comes back only when both are done. A frame
        /// freed after the first release would be overwritten while the other writer was still
        /// reading it.
        /// </summary>
        [Fact]
        public void A_frame_two_sinks_hold_comes_back_only_when_both_release()
        {
            var p = Pool(frames: 1);
            byte[] f = p.Take();
            Assert.True(p.AddHold(f));      // sink A
            Assert.True(p.AddHold(f));      // sink B
            p.Release(f);                   // the caller lets go

            Assert.Equal(2, p.HoldsOn(f));
            Assert.Null(p.Take());          // still out - nothing else can have it

            p.Release(f);                   // sink A's writer
            Assert.Equal(1, p.HoldsOn(f));
            Assert.Null(p.Take());

            p.Release(f);                   // sink B's writer
            Assert.Equal(0, p.HoldsOn(f));
            Assert.Same(f, p.Take());       // and now it is reusable
        }

        [Fact]
        public void Frames_are_handed_out_until_there_are_none_and_the_shortage_is_counted()
        {
            var p = Pool(frames: 2);
            byte[] a = p.Take();
            byte[] b = p.Take();

            Assert.NotSame(a, b);
            Assert.Equal(2, p.InUse);
            Assert.Null(p.Take());
            Assert.Equal(1, p.Exhausted);

            p.Release(a);
            Assert.Same(a, p.Take());
            Assert.Equal(1, p.Exhausted);   // not counted again
        }

        /// <summary>
        /// A double release is the failure that would put one buffer in two hands. Ignored, not
        /// obeyed.
        /// </summary>
        [Fact]
        public void Releasing_a_free_frame_does_not_hand_it_out_twice()
        {
            var p = Pool(frames: 1);
            byte[] f = p.Take();
            p.Release(f);
            p.Release(f);                   // the bug being guarded against
            p.Release(f);

            byte[] first = p.Take();
            Assert.Same(f, first);
            Assert.Null(p.Take());          // one frame, one holder - not two
        }

        [Fact]
        public void A_buffer_from_somewhere_else_is_not_adopted()
        {
            var p = Pool(frames: 1);
            var stranger = new byte[16];

            Assert.False(p.AddHold(stranger));
            Assert.Equal(0, p.HoldsOn(stranger));
            p.Release(stranger);            // must not free one of ours
            Assert.Equal(0, p.InUse);

            byte[] f = p.Take();
            Assert.NotSame(stranger, f);
        }

        /// <summary>
        /// AddHold on a frame nobody holds has to fail. It means the caller is about to write into a
        /// buffer that is back in the pool, and returning true would hide that.
        /// </summary>
        [Fact]
        public void A_hold_cannot_be_added_to_a_frame_that_is_already_free()
        {
            var p = Pool(frames: 1);
            byte[] f = p.Take();
            p.Release(f);

            Assert.False(p.AddHold(f));
            Assert.Equal(0, p.InUse);
        }

        [Fact]
        public void Null_is_tolerated_the_way_a_failed_extraction_would_produce_it()
        {
            var p = Pool();
            Assert.False(p.AddHold(null));
            p.Release(null);
            Assert.Equal(0, p.HoldsOn(null));
            Assert.Equal(0, p.InUse);
        }

        [Fact]
        public void The_geometry_is_validated_rather_than_producing_an_empty_pool()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SharedFramePool(0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SharedFramePool(2, 0));
        }

        /// <summary>
        /// Releases arrive from two ffmpeg writer threads while the acquisition thread is taking
        /// the next frame, so the counting has to survive that. A frame handed out twice would show
        /// up here as a duplicate.
        /// </summary>
        [Fact]
        public void Takes_and_releases_from_several_threads_never_hand_one_frame_to_two_takers()
        {
            var p = new SharedFramePool(4, 8);
            // byte[] does not override Equals, so the default comparer is reference identity -
            // which is exactly the question here: was one array handed to two takers at once.
            var held = new System.Collections.Concurrent.ConcurrentDictionary<byte[], byte>();

            // Not named _ : that would shadow the discard in TryRemove below.
            Parallel.For(0, 2000, iteration =>
            {
                byte[] f = p.Take();
                if (f == null) return;
                Assert.True(held.TryAdd(f, 0), "the same frame was handed to two takers");
                p.AddHold(f);
                p.Release(f);
                held.TryRemove(f, out _);
                p.Release(f);
            });

            Assert.Equal(0, p.InUse);
        }
    }
}
