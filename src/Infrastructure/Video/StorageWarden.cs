using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Watches the local staging folder and applies <see cref="StoragePolicy"/> to it.
    ///
    /// Thin on purpose. Every judgement - what the reserve must be, whether to act, how far to
    /// free - is in StoragePolicy where it is tested; what is here is the disk query, the file
    /// listing and the delete, none of which can be tested against a real disk in this suite.
    ///
    /// Two structural guarantees, chosen over a filename rule because a filename rule is one typo
    /// away from deleting evidence:
    ///   - it only ever looks in the continuous folder, and the evidence is written elsewhere;
    ///   - inside it, only media files are candidates. The sidecar and the segment list are the
    ///     record of what was recorded and they are kilobytes, so they stay even when the video
    ///     they describe is gone.
    /// </summary>
    public sealed class StorageWarden
    {
        /// <summary>
        /// A file younger than this is left alone: it may be the segment ffmpeg is still writing.
        /// Two minutes clears any segment length anybody would set below that.
        /// </summary>
        public static readonly TimeSpan TooYoungToTouch = TimeSpan.FromMinutes(2);

        /// <summary>What the continuous tier writes. Everything else in the folder is left alone.</summary>
        private static readonly string[] MediaExtensions = { ".ts", ".mp4", ".mkv", ".mov", ".avi" };

        private readonly string _folder;

        public StorageWarden(string continuousFolder)
        {
            _folder = continuousFolder;
        }

        /// <summary>Free space on the volume the folder lives on, in GB. Zero when it cannot be read.</summary>
        public double FreeGb
        {
            get
            {
                try
                {
                    string root = Path.GetPathRoot(Path.GetFullPath(_folder));
                    return string.IsNullOrEmpty(root)
                        ? 0.0
                        : new DriveInfo(root).AvailableFreeSpace / 1e9;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        /// <summary>Media files old enough to be moved or deleted, oldest first.</summary>
        public IReadOnlyList<FileInfo> Candidates()
        {
            try
            {
                var dir = new DirectoryInfo(_folder);
                if (!dir.Exists) return Array.Empty<FileInfo>();

                DateTime cutoff = DateTime.Now - TooYoungToTouch;
                return dir.GetFiles()
                          .Where(f => MediaExtensions.Contains(f.Extension.ToLowerInvariant()))
                          .Where(f => f.LastWriteTime < cutoff)
                          .OrderBy(f => f.LastWriteTime)
                          .ToList();
            }
            catch
            {
                return Array.Empty<FileInfo>();
            }
        }

        /// <summary>
        /// Applies the policy once.
        ///
        /// Returns what the caller still has to do: <see cref="StorageAction.StopRecording"/> means
        /// the recording has to be stopped by whoever owns it, because this class deliberately has
        /// no handle on a camera. Deleting it does itself, because that is the part that must not
        /// wander outside this folder.
        /// </summary>
        public StorageAction Apply(double reserveGb, LowSpacePolicy policy, out string message)
        {
            message = null;
            double free = FreeGb;
            if (free <= 0.0) return StorageAction.Nothing;   // could not read the volume

            IReadOnlyList<FileInfo> candidates = Candidates();
            StorageAction action = StoragePolicy.Decide(free, reserveGb, policy, candidates.Count > 0);
            if (action != StorageAction.DeleteOldest)
            {
                if (action == StorageAction.StopRecording)
                    message = $"local storage low - {free:F1} GB free against a {reserveGb:F1} GB "
                            + "reserve, and "
                            + (candidates.Count > 0
                                ? "the policy is to stop rather than delete"
                                : "there is no context left to delete, so what filled the disk is "
                                + "evidence - it is not deleted");
                return action;
            }

            double target = StoragePolicy.TargetGb(reserveGb);
            long freedBytes = 0;
            int freedFiles = 0;
            foreach (FileInfo f in candidates)
            {
                if (FreeGb >= target) break;
                try
                {
                    long size = f.Length;
                    f.Delete();
                    freedBytes += size;
                    freedFiles++;
                }
                catch
                {
                    // In use, or gone already - the mover may have taken it. Try the next one.
                }
            }

            message = freedFiles > 0
                ? $"local storage low - {free:F1} GB free against a {reserveGb:F1} GB reserve; "
                + $"deleted {freedFiles} context file(s), {freedBytes / 1e9:F2} GB, oldest first. "
                + "Anomaly evidence is not touched."
                : $"local storage low - {free:F1} GB free and nothing could be deleted";

            return freedFiles > 0 ? StorageAction.DeleteOldest : StorageAction.StopRecording;
        }

        /// <summary>
        /// The biggest media file in the folder, as a stand-in for one segment.
        ///
        /// Measured rather than assumed because a segment is 4 MB of a static dark scene and about
        /// 300 MB of lit moving content - two orders apart, and the reserve has to hold one of
        /// them, since the segment being written is too young to delete.
        /// </summary>
        public long LargestCandidateBytes()
        {
            try
            {
                var dir = new DirectoryInfo(_folder);
                if (!dir.Exists) return 0;
                return dir.GetFiles()
                          .Where(f => MediaExtensions.Contains(f.Extension.ToLowerInvariant()))
                          .Select(f => f.Length)
                          .DefaultIfEmpty(0L)
                          .Max();
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Files waiting for the mover, and how old the oldest is. For the status line.</summary>
        public string PendingText()
        {
            IReadOnlyList<FileInfo> pending = Candidates();
            if (pending.Count == 0) return "nothing pending transfer";

            TimeSpan age = DateTime.Now - pending[0].LastWriteTime;
            double gb = pending.Sum(f => (double)f.Length) / 1e9;
            return $"{pending.Count} file(s) pending transfer, {gb:F1} GB, oldest {Age(age)}";
        }

        private static string Age(TimeSpan t) =>
            t.TotalHours >= 1.0 ? $"{t.TotalHours:F0} h old"
          : t.TotalMinutes >= 1.0 ? $"{t.TotalMinutes:F0} min old"
          : "just now";
    }
}
