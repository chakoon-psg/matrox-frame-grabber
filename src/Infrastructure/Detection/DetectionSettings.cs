using System;
using System.Text.Json.Serialization;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What one anomaly kind is set to on one camera, as it is stored.
    ///
    /// Times are milliseconds, not frames. The values in service were authored as frame counts at
    /// 124.316 fps - a debounce of 20 frames is 161 ms - and a frame count means something else on
    /// every other rig: the same 20 would be 667 ms on a 30 fps camera. Storing the intent and
    /// resolving it against the rate in use is the difference between a setting that travels and
    /// one that silently changes meaning. <see cref="DetectionSettings.Resolve"/> does the sum.
    /// </summary>
    public sealed class KindSettings
    {
        /// <summary>
        /// Whether this kind is checked at all. Off for everything but Dropout, and off by default
        /// for a kind that appears in a later version - an uncalibrated detector switching itself
        /// on after an update would look like the panel had started failing.
        ///
        /// Not serialized any more: the flag is one app-wide policy, stored once as
        /// OutputSettings.EnabledKinds and written into every channel from there. What lives here
        /// is the copy the detector and the budget arithmetic read. Persisting it per channel as
        /// well would give the file four answers that can disagree with the one.
        /// </summary>
        [JsonIgnore]
        public bool Enabled { get; set; }

        /// <summary>
        /// The flag as a file written before the move stored it. Read only to migrate from - see
        /// <see cref="AnomalyKindSet.UnionOf"/> - and never written, so the file ends up with one
        /// answer rather than two that can disagree.
        ///
        /// It exists because the migration would otherwise be unable to see what it migrates: the
        /// same change that moved the flag stopped deserializing it, and a union taken after that
        /// reads every channel as off. Same shape as the ChannelThresholds legacy key.
        /// </summary>
        [JsonPropertyName("Enabled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? StoredEnabled { get; set; }

        /// <summary>The flag an older file stored, or the live one when there was no older file.</summary>
        [JsonIgnore]
        public bool WasEnabled => StoredEnabled ?? Enabled;

        /// <summary>
        /// How far brightness must move, as a fraction of the running baseline. For a falling kind
        /// this is a depth; for a rising one the same number the other way. See AnomalyThresholds
        /// for how 0.05 was measured.
        /// </summary>
        public double Deviation { get; set; } = 0.05;

        /// <summary>How much the tiles must agree, 0..1.</summary>
        public double Coherence { get; set; } = 0.80;

        /// <summary>How long the move must persist to be believed. 161 ms was 20 frames at 124.316 fps.</summary>
        public double DebounceMs { get; set; } = 161.0;

        /// <summary>
        /// Longest event reported as one. Past this it is closed as truncated and the baseline is
        /// adopted, because nothing is emitted while an event is open and a sustained fall would
        /// otherwise silence the channel - measured, it once cost 50 s and seven real dips.
        /// 2010 ms was 250 frames.
        /// </summary>
        public double MaxEventMs { get; set; } = 2010.0;

        /// <summary>
        /// How much of a head start the first falling tile may have over the last. A surface that
        /// dims at once is near zero; the periodic horizontal wipe on these panels took 193 ms and
        /// is turned away by this. 48 ms was 6 frames.
        /// </summary>
        public double MaxOnsetSpreadMs { get; set; } = 48.0;

        /// <summary>Tiles that must have an onset at all, out of 64. Spatial, so not a time.</summary>
        public int MinOnsetTiles { get; set; } = 8;

        /// <summary>When the threshold above was last measured, ISO date. Empty means never.</summary>
        public string CalibratedAt { get; set; } = string.Empty;

        /// <summary>Frames the calibration was taken over.</summary>
        public long CalibrationFrames { get; set; }

        /// <summary>The false-positive floor that calibration measured, which the threshold clears.</summary>
        public double CalibrationFloor { get; set; }

        public KindSettings Clone() => (KindSettings)MemberwiseClone();

        /// <summary>
        /// Pulls every value into a range that cannot silence the detector.
        ///
        /// Applied on write as well as on load, because the settings window shows what is stored:
        /// clamping only on the way to the detector would leave a typed 0 sitting in the file and
        /// on screen while something else was actually in force.
        /// </summary>
        public void Clamp()
        {
            Deviation = C(Deviation, 0.001, 0.999);
            Coherence = C(Coherence, 0.0, 1.0);
            DebounceMs = C(DebounceMs, 1.0, 600000.0);
            MaxEventMs = C(MaxEventMs, 1.0, 600000.0);
            MaxOnsetSpreadMs = C(MaxOnsetSpreadMs, 1.0, 600000.0);
            MinOnsetTiles = MinOnsetTiles < 1 ? 1
                          : (MinOnsetTiles > TileGrid.TileCount ? TileGrid.TileCount : MinOnsetTiles);
            CalibratedAt = CalibratedAt ?? string.Empty;
        }

        private static double C(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));

        public void CopyFrom(KindSettings other)
        {
            if (other == null) return;
            Enabled = other.Enabled;
            Deviation = other.Deviation;
            Coherence = other.Coherence;
            DebounceMs = other.DebounceMs;
            MaxEventMs = other.MaxEventMs;
            MaxOnsetSpreadMs = other.MaxOnsetSpreadMs;
            MinOnsetTiles = other.MinOnsetTiles;
            // StoredEnabled is deliberately NOT copied. It is the old file's key, read once by
            // the migration; carrying it onto the live instance would put "Enabled" back into
            // every save, and the file would again hold four answers beside the one.
            //
            // Provenance verbatim: it records where the threshold came from, and clamping a record
            // of the past would make it a different record.
            CalibratedAt = other.CalibratedAt ?? string.Empty;
            CalibrationFrames = other.CalibrationFrames;
            CalibrationFloor = other.CalibrationFloor;
            Clamp();
        }
    }

    /// <summary>
    /// One camera's detection settings: what is shared between kinds, and a bag per kind.
    ///
    /// Shared because they describe the measurement rather than a fault: the running baseline is
    /// computed once from the tile grid and every kind compares against it, and the false-positive
    /// budget is a property of the whole channel. Splitting the budget per kind is the point -
    /// seven detectors each allowed one false positive an hour is seven an hour, so switching on
    /// another kind would quietly multiply the rate unless the total is divided.
    /// </summary>
    public sealed class DetectionSettings
    {
        /// <summary>
        /// Total false positives per hour this channel is allowed, across every enabled kind.
        /// A rate rather than a margin because it is the quantity anyone actually cares about.
        /// </summary>
        public double FalsePositiveBudgetPerHour { get; set; } = 1.0;

        /// <summary>Median window for the running baseline. 732 ms was 91 frames at 124.316 fps.</summary>
        public double BaselineWindowMs { get; set; } = 732.0;

        /// <summary>Frames judged before the baseline is trusted. 241 ms was 30 frames.</summary>
        public double BaselineWarmupMs { get; set; } = 241.0;

        /// <summary>Per-kind settings, indexed by <see cref="AnomalyKind"/>.</summary>
        public KindSettings[] PerKind { get; set; } = Defaults();

        /// <summary>
        /// Defaults: Dropout on because it is measured, every other kind off.
        ///
        /// The flag here is a copy of the app-wide policy, which OutputSettings writes over this on
        /// every load path - so this value only ever reaches a DetectionSettings held on its own,
        /// which is what the budget arithmetic below is tested against. It says Dropout for the
        /// same reason AnomalyKindSet.Default() does: a rig that has never been configured should
        /// watch for the one fault there is a detector for.
        /// </summary>
        public static KindSettings[] Defaults()
        {
            var a = new KindSettings[AnomalyCatalog.Count];
            for (int i = 0; i < a.Length; i++)
                a[i] = new KindSettings { Enabled = false };
            a[AnomalyCatalog.Index(AnomalyKind.Dropout)].Enabled = true;
            return a;
        }

        /// <summary>This kind's settings, creating the array if a hand-edited file lost it.</summary>
        public KindSettings For(AnomalyKind kind)
        {
            Normalize();
            return PerKind[AnomalyCatalog.Index(kind)] ?? (PerKind[AnomalyCatalog.Index(kind)] = new KindSettings());
        }

        /// <summary>
        /// Makes the array exactly as long as the catalog, keeping whatever is already in it.
        ///
        /// Grown rather than replaced, and that is the whole point of the method: this code used to
        /// throw the array away whenever its length disagreed, so appending a sixth kind would have
        /// silently reset every channel to the default depth - discarding a threshold measured over
        /// 8713 frames - while the detector kept running and only the numbers changed. Adding a
        /// kind is exactly when that would have happened, and it has now happened twice.
        ///
        /// A longer array is truncated, which loses nothing this build can name.
        /// </summary>
        private void Normalize()
        {
            if (PerKind != null && PerKind.Length == AnomalyCatalog.Count)
            {
                for (int i = 0; i < PerKind.Length; i++)
                    if (PerKind[i] == null) PerKind[i] = new KindSettings();
                return;
            }

            KindSettings[] grown = Defaults();
            if (PerKind != null)
            {
                for (int i = 0; i < PerKind.Length && i < grown.Length; i++)
                    if (PerKind[i] != null) grown[i] = PerKind[i];
            }
            PerKind = grown;
        }

        /// <summary>How many kinds are switched on and implemented - the detectors that will run.</summary>
        public int EnabledCount
        {
            get
            {
                int n = 0;
                foreach (AnomalyKind k in AnomalyCatalog.All)
                    if (AnomalyCatalog.Implemented(k) && For(k).Enabled) n++;
                return n;
            }
        }

        /// <summary>
        /// The share of the budget each running detector gets.
        ///
        /// Split rather than handed out whole because seven detectors each allowed one false
        /// positive an hour is seven an hour: switching on another kind would quietly multiply the
        /// rate the operator was told to expect.
        /// </summary>
        public double BudgetPerEnabledKind => BudgetShare(FalsePositiveBudgetPerHour, EnabledCount);

        /// <summary>
        /// The arithmetic behind <see cref="BudgetPerEnabledKind"/>, as a function of the count so
        /// it can be checked before a second detector exists. Zero or one enabled gets the whole
        /// budget, which also keeps a proposal asked for before anything is switched on from
        /// dividing by zero.
        /// </summary>
        public static double BudgetShare(double totalPerHour, int enabledCount) =>
            enabledCount <= 1 ? totalPerHour : totalPerHour / enabledCount;

        /// <summary>
        /// Turns stored intent into the frame counts the detector works in, at the rate the camera
        /// is actually running.
        ///
        /// Every count is at least 1: a millisecond value shorter than a frame period must still
        /// mean "one frame", not "zero", because a zero debounce or a zero-length event window
        /// turns the detector off in a way that looks like a quiet rig.
        /// </summary>
        public AnomalyThresholds Resolve(AnomalyKind kind, double fps)
        {
            double rate = fps > 1.0 ? fps : 1.0;
            KindSettings k = For(kind);

            var t = new AnomalyThresholds
            {
                Depth = k.Deviation,
                Coherence = k.Coherence,
                DebounceFrames = Frames(k.DebounceMs, rate),
                MaxEventFrames = Frames(k.MaxEventMs, rate),
                MaxOnsetSpreadFrames = Frames(k.MaxOnsetSpreadMs, rate),
                MinOnsetTiles = k.MinOnsetTiles,
                BaselineWindow = Frames(BaselineWindowMs, rate),
                BaselineWarmupFrames = Frames(BaselineWarmupMs, rate),
                FalsePositiveBudgetPerHour = BudgetPerEnabledKind,
                CalibratedAt = k.CalibratedAt ?? string.Empty,
                CalibrationFrames = k.CalibrationFrames,
                CalibrationFloor = k.CalibrationFloor,
            };
            // Through CopyFrom so the clamps that keep a hand-edited file from silencing the
            // detector are applied here too, in one place.
            var clamped = new AnomalyThresholds();
            clamped.CopyFrom(t);
            return clamped;
        }

        private static int Frames(double ms, double fps)
        {
            double f = ms * fps / 1000.0;
            if (double.IsNaN(f) || f < 1.0) return 1;
            return f > int.MaxValue ? int.MaxValue : (int)Math.Round(f);
        }

        /// <summary>
        /// Writes a measured threshold back into one kind, with where it came from.
        /// </summary>
        public void ApplyCalibration(AnomalyKind kind, double deviation, string atIso,
                                     long frames, double floor)
        {
            KindSettings k = For(kind);
            k.Deviation = deviation;
            k.CalibratedAt = atIso ?? string.Empty;
            k.CalibrationFrames = frames;
            k.CalibrationFloor = floor;
            k.Clamp();
        }

        public void CopyFrom(DetectionSettings other)
        {
            if (other == null) return;
            FalsePositiveBudgetPerHour = other.FalsePositiveBudgetPerHour;
            BaselineWindowMs = other.BaselineWindowMs;
            BaselineWarmupMs = other.BaselineWarmupMs;
            Normalize();
            foreach (AnomalyKind k in AnomalyCatalog.All)
                For(k).CopyFrom(other.For(k));
            Clamp();
        }

        /// <summary>Pulls the shared values into a usable range, and every kind with them.</summary>
        public void Clamp()
        {
            FalsePositiveBudgetPerHour = double.IsNaN(FalsePositiveBudgetPerHour)
                ? 1.0
                : (FalsePositiveBudgetPerHour < 0.001 ? 0.001
                   : (FalsePositiveBudgetPerHour > 100000.0 ? 100000.0 : FalsePositiveBudgetPerHour));
            BaselineWindowMs = BaselineWindowMs < 24.0 ? 24.0
                             : (BaselineWindowMs > 600000.0 ? 600000.0 : BaselineWindowMs);
            BaselineWarmupMs = BaselineWarmupMs < 8.0 ? 8.0
                             : (BaselineWarmupMs > 600000.0 ? 600000.0 : BaselineWarmupMs);
            foreach (AnomalyKind k in AnomalyCatalog.All) For(k).Clamp();
        }

        public DetectionSettings Clone()
        {
            var c = new DetectionSettings();
            c.CopyFrom(this);
            return c;
        }

        /// <summary>
        /// The frame rate the old frame-count settings were authored at.
        ///
        /// The previous schema stored frames and did not record the rate, so a migration has to
        /// assume one. This is the operating point every value in service was measured at - 8000 us
        /// exposure, 124.316 fps - and only the time-based counts are affected: deviation,
        /// coherence, tile count, budget and provenance are rate-independent and move across
        /// exactly. The migration is logged rather than silent.
        /// </summary>
        public const double LegacyFrameRate = 124.316;

        /// <summary>
        /// Builds settings from the old flat, frame-based shape.
        ///
        /// The only value that was ever tuned by hand is the depth - 0.05 on two channels and 0.15
        /// on the dim one - and it carries over unchanged. Everything else in service sat at its
        /// default, so the conversion has nothing to distort.
        ///
        /// Growing the per-kind array is a different migration and lives in Normalize: this one
        /// changes the shape of a kind's settings, that one changes how many kinds there are.
        /// </summary>
        public static DetectionSettings FromLegacy(AnomalyThresholds legacy)
        {
            var s = new DetectionSettings();
            if (legacy == null) return s;

            s.FalsePositiveBudgetPerHour = legacy.FalsePositiveBudgetPerHour;
            s.BaselineWindowMs = Ms(legacy.BaselineWindow);
            s.BaselineWarmupMs = Ms(legacy.BaselineWarmupFrames);

            KindSettings d = s.For(AnomalyKind.Dropout);
            // Set so the kind-set migration can see it: a file this old has no EnabledKinds list
            // either, and AnomalyKindSet.UnionOf reads WasEnabled off these instances.
            d.Enabled = true;
            d.Deviation = legacy.Depth;
            d.Coherence = legacy.Coherence;
            d.DebounceMs = Ms(legacy.DebounceFrames);
            d.MaxEventMs = Ms(legacy.MaxEventFrames);
            d.MaxOnsetSpreadMs = Ms(legacy.MaxOnsetSpreadFrames);
            d.MinOnsetTiles = legacy.MinOnsetTiles;
            d.CalibratedAt = legacy.CalibratedAt ?? string.Empty;
            d.CalibrationFrames = legacy.CalibrationFrames;
            d.CalibrationFloor = legacy.CalibrationFloor;
            s.Clamp();
            return s;
        }

        private static double Ms(int frames) => frames * 1000.0 / LegacyFrameRate;
    }
}
