using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Streams fixed-size raw frames (band-1 Bayer) to a file on a background thread. The acquisition
    /// hook rents a buffer, fills it (MbufGet), and enqueues it; a writer thread drains the queue to
    /// disk and recycles the buffer. A bounded queue provides back-pressure so nothing is silently
    /// dropped: if the disk momentarily lags, Enqueue blocks the caller instead of discarding a frame.
    /// </summary>
    public sealed class RawFrameWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly int _frameBytes;
        private readonly BlockingCollection<byte[]> _queue;
        private readonly ConcurrentQueue<byte[]> _pool = new ConcurrentQueue<byte[]>();
        private readonly Thread _writer;

        private long _framesWritten;
        public long FramesWritten => Interlocked.Read(ref _framesWritten);
        public volatile bool Failed;
        public string LastError;

        public RawFrameWriter(string path, int frameBytes, int queueCapacity = 64)
        {
            _frameBytes = frameBytes;
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                1 << 20, FileOptions.SequentialScan);
            _queue = new BlockingCollection<byte[]>(queueCapacity);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "RawFrameWriter" };
            _writer.Start();
        }

        /// <summary>Gets a reusable frame buffer (from the pool, or a fresh one).</summary>
        public byte[] Rent()
        {
            if (_pool.TryDequeue(out byte[] b) && b.Length == _frameBytes)
                return b;
            return new byte[_frameBytes];
        }

        /// <summary>Queues a filled buffer for writing (blocks briefly if the disk is behind).</summary>
        public void Enqueue(byte[] buf)
        {
            try { _queue.Add(buf); }
            catch (InvalidOperationException) { /* queue completed */ }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (byte[] b in _queue.GetConsumingEnumerable())
                {
                    _fs.Write(b, 0, b.Length);
                    Interlocked.Increment(ref _framesWritten);
                    _pool.Enqueue(b);
                }
            }
            catch (Exception e)
            {
                Failed = true;
                LastError = e.Message;
            }
        }

        /// <summary>Signals no more frames, waits for the writer to drain, and flushes to disk.</summary>
        public void CompleteAndWait(int timeoutMs)
        {
            _queue.CompleteAdding();
            _writer.Join(timeoutMs);
            try { _fs.Flush(true); } catch { /* ignore */ }
        }

        public void Dispose()
        {
            try { _fs.Dispose(); } catch { /* ignore */ }
            _queue.Dispose();
        }
    }
}
