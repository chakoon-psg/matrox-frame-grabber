using System.Collections.Generic;
using System.ComponentModel;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.ViewModels
{
    /// <summary>
    /// One anomaly kind in the app settings: a checkbox, what it looks like, and whether anything
    /// implements it.
    ///
    /// App-wide, which is where it belongs and where it was asked for. The thresholds are per
    /// camera because they had to be measured that way, but *which* faults the rig watches for is
    /// one policy - four cameras disagreeing about it produces a report nobody can read, and there
    /// is no reason to want it.
    ///
    /// Every kind is listed and all but Dropout are un-tickable. A checkbox that can be ticked and
    /// does nothing is worse than one that cannot; leaving the unimplemented kinds out entirely is
    /// worse still, because then a quiet rig reads as "nothing wrong" when one of seven things is
    /// being looked at.
    /// </summary>
    public sealed class AnomalyKindToggle : INotifyPropertyChanged
    {
        private readonly OutputSettings _settings;

        internal AnomalyKindToggle(OutputSettings settings, AnomalyKind kind)
        {
            _settings = settings;
            Kind = kind;
        }

        public AnomalyKind Kind { get; }

        /// <summary>The kind's name, as the enum spells it - the word the log and the clip use.</summary>
        public string Name => Kind.ToString();

        /// <summary>Whether a detector exists. Drives the checkbox's enabled state.</summary>
        public bool Implemented => AnomalyCatalog.Implemented(Kind);

        /// <summary>
        /// What this kind looks like, in CONTEXT.md's words.
        ///
        /// Its own glossary terms, not the shorter ones in circulation: CONTEXT.md rules out
        /// 백화 for Washout and 깜빡임 for Flicker precisely because they collide with other
        /// entries - 깜빡임 in the field almost always means Dropout.
        /// </summary>
        public string Description => AnomalyCatalog.Describe(Kind);

        /// <summary>What the row shows beside the name: what it is, or that it does not exist yet.</summary>
        public string StatusText => Implemented ? Description : "미구현 — " + Description;

        /// <summary>
        /// Whether the rig watches for this kind. Written straight through and saved, so there is
        /// no Apply for it: a checkbox that needs confirming reads as broken.
        ///
        /// It reaches the detector on the next Start, like every other detection setting.
        /// </summary>
        public bool Enabled
        {
            get => _settings != null && _settings.IsKindEnabled(Kind);
            set
            {
                if (_settings == null || Enabled == value) return;
                _settings.SetKindEnabled(Kind, value);
                Raise(nameof(Enabled));
            }
        }

        /// <summary>One row per kind, in the order the catalog lists them.</summary>
        internal static IReadOnlyList<AnomalyKindToggle> BuildFor(OutputSettings settings)
        {
            var rows = new List<AnomalyKindToggle>(AnomalyCatalog.Count);
            foreach (AnomalyKind k in AnomalyCatalog.All)
                rows.Add(new AnomalyKindToggle(settings, k));
            return rows;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
