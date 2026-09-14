using System;
using System.IO;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Picks an inspector at start-up and says why, the way <c>VideoSinkFactory</c> does.
    ///
    /// **The reason is the point.** A feature that silently does nothing is indistinguishable from
    /// one that is working and finding nothing, and this one does nothing by default - there is no
    /// model file on most machines. So every path through here produces a sentence that ends up in
    /// the log and in the tooltip.
    ///
    /// **Nothing here names ONNX.** The implementation registers itself, which is what keeps the
    /// removal cost at one deleted project reference: with `src/Onnx/` gone, nothing registers,
    /// and this returns <see cref="NullScreenInspector"/> with "no inspector implementation was
    /// registered" as the reason.
    /// </summary>
    public static class ScreenInspectorFactory
    {
        /// <summary>
        /// How to build the real thing, if the real thing is in this build.
        ///
        /// Set once at start-up by whoever owns composition. A delegate rather than a reference
        /// because `Infrastructure/` must not know the ONNX project exists.
        /// </summary>
        public static Func<string, ScreenInspectorOptions, IScreenInspector> Implementation { get; set; }

        /// <summary>
        /// Where the model is looked for, in order. The same shape as the ffmpeg search: a
        /// configured path first, then beside the app.
        /// </summary>
        public static string FindModel(string configured, string appFolder)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            if (!string.IsNullOrWhiteSpace(appFolder))
            {
                try
                {
                    foreach (string candidate in Directory.GetFiles(appFolder, "*.onnx"))
                        return candidate;

                    string models = Path.Combine(appFolder, "models");
                    if (Directory.Exists(models))
                        foreach (string candidate in Directory.GetFiles(models, "*.onnx"))
                            return candidate;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return null;
        }

        /// <summary>
        /// Returns an inspector, always. When anything is missing or broken the result is a
        /// <see cref="NullScreenInspector"/> carrying the reason - never null, never an exception
        /// out of start-up.
        /// </summary>
        public static IScreenInspector Create(bool enabled, string configuredPath, string appFolder,
                                              ScreenInspectorOptions options)
        {
            if (!enabled)
                return new NullScreenInspector("switched off in settings");

            if (Implementation == null)
                return new NullScreenInspector("no inspector implementation was registered in this build");

            string model = FindModel(configuredPath, appFolder);
            if (model == null)
                return new NullScreenInspector("no .onnx model was found (settings path, app folder, app folder\\models)");

            try
            {
                return Implementation(model, options ?? new ScreenInspectorOptions());
            }
            catch (Exception e)
            {
                // A model that will not load must not stop the app from acquiring. It is an
                // optional judge; the tile detectors are the ones the rig depends on.
                return new NullScreenInspector($"{Path.GetFileName(model)} would not load - {e.Message}");
            }
        }
    }

    /// <summary>What an inspector is built with. Per camera, like every other threshold here.</summary>
    public sealed class ScreenInspectorOptions
    {
        /// <summary>Confidence a finding has to reach, 0 to 1.</summary>
        public float ScoreThreshold { get; set; } = 0.3f;

        /// <summary>How much two findings may overlap before the weaker is dropped.</summary>
        public float IouThreshold { get; set; } = 0.5f;

        /// <summary>Most findings one frame may produce.</summary>
        public int MaxFindings { get; set; } = 16;

        /// <summary>
        /// Threads the model may use, or 0 for the runtime's own choice.
        ///
        /// The published optimum of 8 was measured on an idle machine. Here three acquisition
        /// threads and an encoder already want those cores, so this is a value to measure rather
        /// than copy - and the runtime's default turned out to equal 8 on this box anyway.
        /// </summary>
        public int IntraOpThreads { get; set; }
    }
}
