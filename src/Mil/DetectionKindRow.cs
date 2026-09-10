using System.Collections.Generic;
using System.ComponentModel;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// One anomaly kind as the settings window shows it: a checkbox, what it is set to, and whether
    /// anything implements it.
    ///
    /// Every kind is listed and all but Dropout are switched off and un-tickable. A checkbox that
    /// can be ticked and does nothing is worse than one that cannot, and leaving the unimplemented
    /// ones out entirely is worse still: a quiet panel would read as "nothing wrong" when one of
    /// seven things is being looked at.
    /// </summary>
    public sealed class DetectionKindRow : INotifyPropertyChanged
    {
        private readonly CameraChannel _channel;

        internal DetectionKindRow(CameraChannel channel, AnomalyKind kind)
        {
            _channel = channel;
            Kind = kind;
        }

        public AnomalyKind Kind { get; }

        /// <summary>The kind's name, as the enum spells it - the same word the log and the clip use.</summary>
        public string Name => Kind.ToString();

        /// <summary>Whether a detector exists. Drives the checkbox's enabled state.</summary>
        public bool Implemented => AnomalyCatalog.Implemented(Kind);

        /// <summary>What this kind looks like, for the tooltip.</summary>
        public string Description => AnomalyCatalog.Describe(Kind);

        /// <summary>
        /// Whether the kind is checked. Written straight through to the settings and saved, so
        /// there is no Apply for it: a checkbox that needs confirming reads as broken.
        /// </summary>
        public bool Enabled
        {
            get => _channel?.Detection.For(Kind).Enabled ?? false;
            set
            {
                if (_channel == null || Enabled == value) return;
                _channel.Detection.For(Kind).Enabled = value;
                _channel.SaveDetection();
                Raise(nameof(Enabled));
                Raise(nameof(StatusText));
            }
        }

        /// <summary>
        /// What the kind is set to, or why it is not running. The threshold shown is the stored
        /// one; the frame counts it resolves to are on the DETECTION line, since those depend on
        /// the rate rather than on the kind.
        /// </summary>
        public string StatusText
        {
            get
            {
                if (!Implemented) return "미구현";
                if (_channel == null) return string.Empty;

                KindSettings k = _channel.Detection.For(Kind);
                string word = AnomalyCatalog.DeviationWord(Kind);
                return $"{word} {k.Deviation:0.###} · coh {k.Coherence:0.##} · "
                     + $"debounce {k.DebounceMs:0} ms";
            }
        }

        /// <summary>Re-reads everything, for after a calibration or a load.</summary>
        public void Refresh()
        {
            Raise(nameof(Enabled));
            Raise(nameof(StatusText));
        }

        /// <summary>One row per kind for one channel, in the order the catalog lists them.</summary>
        internal static IReadOnlyList<DetectionKindRow> BuildFor(CameraChannel channel)
        {
            var rows = new List<DetectionKindRow>(AnomalyCatalog.Count);
            foreach (AnomalyKind k in AnomalyCatalog.All)
                rows.Add(new DetectionKindRow(channel, k));
            return rows;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
