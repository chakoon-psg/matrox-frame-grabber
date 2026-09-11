using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;
using MatroxFrameGrabber.Mil.Video;

namespace MilVideoSinkHarness
{
    /// <summary>
    /// Grabs from one Rapixo CXP channel and feeds an <see cref="IVideoSink"/>, so a sink can be
    /// written and measured without the application around it.
    ///
    /// This exists so that whoever implements <see cref="MilSeqVideoSink"/> gets the acquisition
    /// set up correctly and nothing else. There is no detector, no thresholds, no calibration and
    /// no window here: none of that is needed to make MIL write a file, and all of it is what makes
    /// the application's own channel class 2400 lines long.
    ///
    /// The setup below is not arbitrary. Every step marked TRAP cost this project a measurement to
    /// find; ACCEPTANCE.md lists them all with the symptoms.
    /// </summary>
    internal static class Program
    {
        private const int GrabBufferCount = 4;

        private sealed class HookData
        {
            public IVideoSink Sink;
            public long FrameCount;
            public double FirstTimeStampSec;
            public double LastTimeStampSec;
        }

        private static int Main(string[] rawArgs)
        {
            var args = new Args(rawArgs);
            MilErrorLog.FileSuffix = "-milvideosink";

            Console.WriteLine($"MIL video sink harness - channel {args.Channel}, {args.Seconds} s, "
                            + $"sink {args.Sink}, exposure {args.ExposureUs} us, decim {args.Decimation}");

            MIL_ID app = MIL.M_NULL, sys = MIL.M_NULL, dig = MIL.M_NULL;
            var grabBuffers = new List<MIL_ID>();
            IVideoSink sink = null;
            GCHandle handle = default;
            MIL_DIG_HOOK_FUNCTION_PTR hookDelegate = null;
            HookData data = null;

            try
            {
                MIL.MappAlloc(MIL.M_NULL, MIL.M_DEFAULT, ref app);
                MIL.MappControl(app, MIL.M_ERROR, MIL.M_THROW_EXCEPTION);

                // TRAP: MIL reports errors in a modal dialog on whichever thread failed, which on
                // an acquisition thread stalls the channel behind a box nobody can see. Printing is
                // disabled process-wide and MilErrorLog takes its place.
                MIL.MappControl(app, MIL.M_ERROR, MIL.M_PRINT_DISABLE);

                MIL.MsysAlloc(app, MIL.M_SYSTEM_RAPIXOCXP, MIL.M_DEV0, MIL.M_DEFAULT, ref sys);

                // TRAP: the board reports four digitizers whether or not cameras are attached, and
                // MdigAlloc on an empty port raises that modal dialog even under M_THROW_EXCEPTION.
                // Printing is already disabled above, which is what makes this survivable.
                try
                {
                    MIL.MdigAlloc(sys, MIL.M_DEV0 + args.Channel, "M_DEFAULT", MIL.M_DEFAULT, ref dig);
                }
                catch (MILException)
                {
                    Console.Error.WriteLine($"no camera on channel {args.Channel}.");
                    return 2;
                }

                // TRAP: M_BAYER_CONVERSION is a board setting that survives a reboot. Left off by
                // something else, the colour pipeline reads raw data and the image comes out in
                // tiles. Re-asserted before M_SIZE_BAND is inquired, because that answer depends
                // on it.
                try { MIL.MdigControl(dig, MIL.M_BAYER_CONVERSION, MIL.M_ENABLE); }
                catch (MILException e) { MilErrorLog.Write("re-assert M_BAYER_CONVERSION", e); }

                double fps = Configure(dig, args);

                MIL_INT bands = MIL.MdigInquire(dig, MIL.M_SIZE_BAND, MIL.M_NULL);
                MIL_INT type = MIL.MdigInquire(dig, MIL.M_TYPE, MIL.M_NULL);
                MIL_INT w = MIL.MdigInquire(dig, MIL.M_SIZE_X, MIL.M_NULL);
                MIL_INT h = MIL.MdigInquire(dig, MIL.M_SIZE_Y, MIL.M_NULL);
                Console.WriteLine($"geometry {(long)w}x{(long)h}x{(long)bands}, "
                                + $"{(long)w * (long)h * (long)bands / 1048576.0:F2} MiB/frame, "
                                + $"ResultingFrameRate {fps:F3} fps "
                                + $"(frame period {1e6 / fps:F0} us)");

                // Grab buffers use scarce non-paged DMA memory, so the ring stays small.
                for (int i = 0; i < GrabBufferCount; i++)
                {
                    MIL_ID buf = MIL.M_NULL;
                    MIL.MbufAllocColor(sys, bands, w, h, type,
                        MIL.M_IMAGE + MIL.M_GRAB + MIL.M_PROC, ref buf);
                    MIL.MbufClear(buf, 0);
                    grabBuffers.Add(buf);
                }

                sink = args.Sink == "mil"
                    ? (IVideoSink)new MilSeqVideoSink(sys)
                    : new FfmpegVideoSink(sys, FfmpegRecorder.ResolveFfmpegPath(null));

                Directory.CreateDirectory(args.OutputFolder);
                var spec = new VideoStreamSpec(
                    grabBuffers[0], fps, args.OutputFolder, $"harness_ch{args.Channel}", 1.0,
                    args.Segments
                        ? new[] { new VideoOutputSpec("seg", 1, segmentSeconds: 2.0, keyframeSeconds: 0.5) }
                        : new[] { VideoOutputSpec.SingleFile() });

                if (!sink.Start(spec, out string sinkError))
                {
                    Console.Error.WriteLine($"{sink.Name} did not start: {sinkError}");
                    return 3;
                }
                Console.WriteLine($"{sink.Name} writing {string.Join(", ", sink.FilePaths)}");

                MIL_INT missedAtStart = Inquire(dig, MIL.M_PROCESS_FRAME_MISSED);

                data = new HookData { Sink = sink };
                handle = GCHandle.Alloc(data);
                hookDelegate = new MIL_DIG_HOOK_FUNCTION_PTR(OnFrame);

                MIL.MdigProcess(dig, grabBuffers.ToArray(), grabBuffers.Count,
                    MIL.M_START, MIL.M_DEFAULT, hookDelegate, GCHandle.ToIntPtr(handle));

                Thread.Sleep(args.Seconds * 1000);

                MIL.MdigProcess(dig, grabBuffers.ToArray(), grabBuffers.Count,
                    MIL.M_STOP, MIL.M_DEFAULT, hookDelegate, GCHandle.ToIntPtr(handle));

                MIL_INT missed = Inquire(dig, MIL.M_PROCESS_FRAME_MISSED);
                double rate = 0.0;
                MIL.MdigInquire(dig, MIL.M_PROCESS_FRAME_RATE, ref rate);

                sink.Stop();
                sink.WaitFinalize(15000);

                Report(sink, data, (long)missed - (long)missedAtStart, rate, fps);
                return 0;
            }
            catch (MILException e)
            {
                Console.Error.WriteLine("MIL error: " + e.Message.Trim());
                MilErrorLog.Write("harness", e);
                return 1;
            }
            finally
            {
                try { sink?.Dispose(); } catch { }
                if (handle.IsAllocated) handle.Free();
                foreach (MIL_ID b in grabBuffers) { try { MIL.MbufFree(b); } catch { } }
                if (dig != MIL.M_NULL) { try { MIL.MdigFree(dig); } catch { } }
                if (sys != MIL.M_NULL) { try { MIL.MsysFree(sys); } catch { } }
                if (app != MIL.M_NULL) { try { MIL.MappFree(app); } catch { } }
            }
        }

        /// <summary>
        /// Applies exposure and decimation, then reads them back, and returns the rate the camera
        /// says it can deliver.
        ///
        /// TRAP: a GenICam write is not verified until it is read back. MdigControlFeature does not
        /// throw, printing is disabled, and M_FEATURE_ACCESS_MODE answers RW for features this
        /// camera then ignores. TRAP: the integer features must be written as M_TYPE_MIL_INT or
        /// they are accepted and discarded - GenICamFeatures.SetInt does that.
        /// </summary>
        private static double Configure(MIL_ID dig, Args args)
        {
            var features = new GenICamFeatures { Digitizer = dig };

            if (args.Decimation > 0)
            {
                features.SetInt("DecimationHorizontal", args.Decimation);
                features.SetInt("DecimationVertical", args.Decimation);
                features.TryGetInt(MIL.M_FEATURE_VALUE, "DecimationHorizontal", out long dh);
                features.TryGetInt(MIL.M_FEATURE_VALUE, "DecimationVertical", out long dv);
                Console.WriteLine($"decimation read back {dh}/{dv}");
            }

            if (args.ExposureUs > 0)
            {
                features.SetDouble("ExposureTime", args.ExposureUs);
                features.TryGetDouble(MIL.M_FEATURE_VALUE, "ExposureTime", out double us);
                Console.WriteLine($"exposure read back {us:F0} us");
            }

            // The rate the file must declare. TRAP: AcquisitionFrameRate is what the camera was
            // asked for (184 here) and ResultingFrameRate is what the exposure allows (124.316).
            // Declaring the first made a 120.0 s recording read as 81.07 s and play 1.48x fast.
            features.TryGetDouble(MIL.M_FEATURE_VALUE, "ResultingFrameRate", out double resulting);
            features.TryGetDouble(MIL.M_FEATURE_VALUE, "AcquisitionFrameRate", out double asked);
            Console.WriteLine($"AcquisitionFrameRate {asked:F3} (asked), "
                            + $"ResultingFrameRate {resulting:F3} (deliverable)");

            return VideoRatePolicy.Declared(resulting, 0.0, asked);
        }

        private static MIL_INT OnFrame(MIL_INT hookType, MIL_ID hookId, IntPtr userDataPtr)
        {
            if (userDataPtr == IntPtr.Zero) return 0;
            var data = GCHandle.FromIntPtr(userDataPtr).Target as HookData;
            if (data == null) return 0;

            MIL_ID grabbed = MIL.M_NULL;
            MIL.MdigGetHookInfo(hookId, MIL.M_MODIFIED_BUFFER + MIL.M_BUFFER_ID, ref grabbed);

            // The board's own stamp. M_GRAB_TIME_STAMP_NS is a different constant for a different
            // call and reads back zero here.
            double stamp = 0.0;
            MIL.MdigGetHookInfo(hookId, MIL.M_MODIFIED_BUFFER + MIL.M_GRAB_TIME_STAMP, ref stamp);
            if (data.FrameCount == 0) data.FirstTimeStampSec = stamp;
            data.LastTimeStampSec = stamp;
            data.FrameCount++;

            // TRAP: everything below runs inside the acquisition budget. At 124.316 fps that is
            // 8043 us per frame, and going over does not slow the preview - it loses frames.
            data.Sink.Feed(grabbed, data.FrameCount);
            return 0;
        }

        private static void Report(IVideoSink sink, HookData data, long missed, double measuredFps,
                                   double declaredFps)
        {
            VideoSinkStats s = sink.Stats;
            double period = measuredFps > 0.0 ? 1e6 / measuredFps : 0.0;

            Console.WriteLine();
            Console.WriteLine($"grabbed   {data.FrameCount} frames, {measuredFps:F2} fps, "
                            + $"{missed} missed");
            Console.WriteLine($"board     first {data.FirstTimeStampSec:F6} s, "
                            + $"last {data.LastTimeStampSec:F6} s, "
                            + $"span {(data.LastTimeStampSec - data.FirstTimeStampSec) * 1000:F1} ms");
            Console.WriteLine($"sink      {sink.Name}: {s.FramesFed} fed, {s.FramesSkipped} skipped, "
                            + $"{s.FramesDropped} dropped");
            Console.WriteLine($"rate      {s.WrittenFps:F2} fps written, {s.DeclaredFps:F3} declared, "
                            + $"{declaredFps:F3} deliverable");
            Console.WriteLine($"feed      mean {s.MeanFeedUs:F0} us, max {s.MaxFeedUs:F0} us "
                            + $"of the {period:F0} us frame period");
            Console.WriteLine();
            Console.WriteLine(missed == 0 && s.FramesSkipped == 0 && s.FramesDropped == 0
                ? "PASS on losses. Now check the file: its duration must match the run's wall clock."
                : "FAIL on losses - see ACCEPTANCE.md.");
        }

        private static MIL_INT Inquire(MIL_ID dig, long what)
        {
            MIL_INT v = 0;
            try { MIL.MdigInquire(dig, what, ref v); } catch (MILException) { }
            return v;
        }

        /// <summary>Command line, so the harness works with whatever camera is on the bench.</summary>
        private sealed class Args
        {
            public int Channel = 0;
            public int Seconds = 30;
            public double ExposureUs = 8000;
            public int Decimation = 2;
            public string Sink = "mil";
            public bool Segments;
            public string OutputFolder = Path.Combine(Path.GetTempPath(), "MilVideoSink");

            public Args(string[] a)
            {
                for (int i = 0; a != null && i < a.Length; i++)
                {
                    string v = i + 1 < a.Length ? a[i + 1] : null;
                    switch (a[i])
                    {
                        case "--channel": Channel = Int(v, Channel); i++; break;
                        case "--seconds": Seconds = Int(v, Seconds); i++; break;
                        case "--exposure": ExposureUs = Dbl(v, ExposureUs); i++; break;
                        case "--decim": Decimation = Int(v, Decimation); i++; break;
                        case "--sink": Sink = v ?? Sink; i++; break;
                        case "--out": OutputFolder = v ?? OutputFolder; i++; break;
                        case "--segments": Segments = true; break;
                    }
                }
            }

            private static int Int(string s, int fallback) =>
                int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

            private static double Dbl(string s, double fallback) =>
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }
    }
}
