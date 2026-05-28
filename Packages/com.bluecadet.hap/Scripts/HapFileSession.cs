using System;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace Bluecadet.Hap
{
    internal enum SessionOpenStatus { NotReady, Opened, Failed }

    internal sealed class HapFileSession : IDisposable
    {
        // ── Metadata (valid while IsOpen) ────────────────────────────────────

        public int FrameCount { get; private set; }
        public float FrameRate { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public HapFormat Format { get; private set; }
        public float Duration => FrameCount > 0 && FrameRate > 0f ? FrameCount / FrameRate : 0f;

        public bool IsOpen    => _handle != IntPtr.Zero;
        public bool IsOpening => _openThread != null;

        // ── Native handle + ring buffer ──────────────────────────────────────

        IntPtr _handle;
        HapFrameRingBuffer _ringBuffer;
        int _frameBufferSize;
        string _filePath;

        // ── Open thread state ────────────────────────────────────────────────

        Thread _openThread;
        volatile bool _openCancelled;
        volatile OpenResult _pendingOpenResult;

        // ── Close thread ─────────────────────────────────────────────────────

        Thread _closeThread;

        // ── Decode thread state ──────────────────────────────────────────────

        Thread _decodeThread;
        volatile bool _decodeRunning;
        volatile bool _handleValid;
        volatile int _decodeTargetFrame = -1;
        volatile int _decodeDirection = 1;
        readonly object _decodeLock = new();
        readonly ManualResetEventSlim _decodeExited = new(true);

        // ── Profiler markers ─────────────────────────────────────────────────

        static readonly ProfilerMarker s_ReadSampleMarker = new("HapPlayer.ReadSample");
        static readonly ProfilerMarker s_DecompressMarker = new("HapPlayer.Decompress");

        // ── Open ─────────────────────────────────────────────────────────────

        public void Open(string resolvedPath)
        {
            _filePath = resolvedPath;

            // If an open is already in progress (rapid enable/disable/enable, or Open(path) called
            // mid-open), wait for it to finish and discard any result before starting a fresh open.
            if (_openThread != null)
            {
                _openThread.Join();
                _openThread = null;
                var stale = _pendingOpenResult;
                _pendingOpenResult = null;
                if (stale != null && stale.Handle != IntPtr.Zero)
                    HapNative.hap_close(stale.Handle);
            }

            if (IsOpen) return;

            // Ensure the previous deferred close has fully finished before reusing _decodeExited
            // and starting a new decode thread.
            _closeThread?.Join();
            _closeThread = null;

            _openCancelled = false;
            _pendingOpenResult = null;

            _openThread = new Thread(() => OpenBackground(resolvedPath))
            {
                IsBackground = true,
                Name = "HapOpen"
            };
            _openThread.Start();
        }

        public void CancelOpen() => _openCancelled = true;

        void OpenBackground(string resolved)
        {
            int err;
            IntPtr handle = HapNative.hap_open(resolved, out err);

            if (handle == IntPtr.Zero)
            {
                if (!_openCancelled)
                    Debug.LogError($"[HapPlayer] Failed to open '{resolved}', error: {err}");
                _pendingOpenResult = OpenResult.Failed;
                return;
            }

            int frameCount = HapNative.hap_get_frame_count(handle);
            float frameRate = HapNative.hap_get_frame_rate(handle);

            if (frameRate <= 0f || frameCount <= 0)
            {
                if (!_openCancelled)
                    Debug.LogError($"[HapPlayer] Invalid video ({frameCount} frames, {frameRate} fps) in '{resolved}'");
                HapNative.hap_close(handle);
                _pendingOpenResult = OpenResult.Failed;
                return;
            }

            _pendingOpenResult = new OpenResult(
                handle,
                frameCount,
                frameRate,
                HapNative.hap_get_frame_buffer_size(handle),
                HapNative.hap_get_width(handle),
                HapNative.hap_get_height(handle),
                HapFormatExtensions.ToHapFormat(HapNative.hap_get_texture_format(handle))
            );
        }

        // ── Poll for result (main thread) ────────────────────────────────────

        /// <summary>
        /// Call from the main thread in Update(). Returns Opened if a successful open result
        /// is ready (metadata properties are populated); Failed if the open failed or was
        /// cancelled; NotReady if the open thread is still running or no open was started.
        /// On Opened, the ring buffer is allocated and the session is ready for StartDecoding().
        /// </summary>
        public SessionOpenStatus TryConsumeOpenResult()
        {
            var result = _pendingOpenResult;
            if (result == null) return SessionOpenStatus.NotReady;

            _pendingOpenResult = null;
            _openThread = null;

            if (_openCancelled || result.Handle == IntPtr.Zero)
            {
                if (result.Handle != IntPtr.Zero)
                    HapNative.hap_close(result.Handle);
                return SessionOpenStatus.Failed;
            }

            _handle          = result.Handle;
            FrameCount       = result.FrameCount;
            FrameRate        = result.FrameRate;
            Width            = result.Width;
            Height           = result.Height;
            Format           = result.Format;
            _frameBufferSize = result.FrameBufferSize;
            _ringBuffer      = new HapFrameRingBuffer(_frameBufferSize);

            return SessionOpenStatus.Opened;
        }

        // ── Decode thread lifecycle ──────────────────────────────────────────

        /// <summary>
        /// Start the decode thread and queue the first frame. Call from the main thread
        /// immediately after TryConsumeOpenResult() returns Opened and GPU resources are ready.
        /// </summary>
        public void StartDecoding(int firstFrame)
        {
            _handleValid   = true;
            _decodeRunning = true;
            _decodeExited.Reset();
            _decodeThread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name         = "HapDecode",
                // AboveNormal reduces wake-up scheduling latency, which is
                // measurably worse on Windows than macOS at default priority.
                Priority     = System.Threading.ThreadPriority.AboveNormal,
            };
            try
            {
                _decodeThread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HapPlayer] Failed to start decode thread: {ex.Message}");
                _decodeThread  = null;
                _decodeRunning = false;
                _handleValid   = false;
                _decodeExited.Set();
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
                return;
            }

            RequestDecode(firstFrame, 1);
        }

        public void RequestDecode(int frame, int direction)
        {
            lock (_decodeLock)
            {
                _decodeDirection = direction;
                if (_decodeTargetFrame == frame) return;
                _decodeTargetFrame = frame;
                Monitor.Pulse(_decodeLock);
            }
        }

        public bool TryAcquireFrame(out HapFrameLease lease)
        {
            var rb = _ringBuffer;
            if (rb == null) { lease = default; return false; }
            return rb.TryAcquire(out lease);
        }

        void DecodeLoop()
        {
            // Capture fields that must remain valid for the lifetime of this thread.
            IntPtr handle = _handle;
            int frameBufferSize = _frameBufferSize;
            int frameCount = FrameCount;
            HapFrameRingBuffer ringBuffer = _ringBuffer;

            try
            {
                var scheduler = new DecodeScheduler(frameCount);
                int consecutiveErrors = 0;

                while (_decodeRunning)
                {
                    int target;
                    bool isPrefetch;
                    int dir;

                    lock (_decodeLock)
                    {
                        dir = _decodeDirection;
                        var (t, pf, block) = scheduler.Next(_decodeTargetFrame, dir);

                        if (block)
                        {
                            // Block until the main thread requests a new frame or signals exit.
                            // 100ms safety timeout guards against Pulse being missed during a Close() race.
                            while (_decodeRunning && _decodeTargetFrame == scheduler.LastExplicit)
                                Monitor.Wait(_decodeLock, 100);

                            if (!_decodeRunning) break;
                            dir = _decodeDirection;
                            (t, pf, _) = scheduler.Next(_decodeTargetFrame, dir);
                        }

                        target     = t;
                        isPrefetch = pf;
                    }

                    if (target == -1) continue;
                    if (ringBuffer == null) break;
                    if (!_handleValid) break;

                    IntPtr buf = ringBuffer.GetWritePtr();
                    int readBytes;
                    using (s_ReadSampleMarker.Auto())
                        readBytes = HapNative.hap_read_sample(handle, target);

                    int result;
                    if (readBytes <= 0)
                    {
                        result = HapNative.ErrorFile;
                    }
                    else
                    {
                        using (s_DecompressMarker.Auto())
                            result = HapNative.hap_decompress_frame(handle, buf, frameBufferSize);
                    }

                    if (result != HapNative.ErrorNone)
                    {
                        consecutiveErrors++;
                        Debug.LogWarning($"[HapPlayer] Failed to decode frame {target}, error: {result} ({consecutiveErrors} consecutive)");
                        if (consecutiveErrors >= 10)
                        {
                            Debug.LogError($"[HapPlayer] Decode loop aborting after {consecutiveErrors} consecutive errors on '{_filePath}'");
                            break;
                        }
                    }
                    else
                    {
                        consecutiveErrors = 0;
                        ringBuffer.CommitWrite(target);

                        if (frameCount > 1)
                        {
                            int nextFrame = (target + dir + frameCount) % frameCount;
                            HapNative.hap_prefetch_frame(handle, nextFrame);
                        }

                        scheduler.OnDecoded(target, isPrefetch, dir);
                    }
                }
            }
            finally
            {
                _decodeExited.Set();
            }
        }

        // ── Close ────────────────────────────────────────────────────────────

        public void Close()
        {
            if (!IsOpen && _decodeThread == null) return;

            _decodeRunning = false;
            lock (_decodeLock)
                Monitor.Pulse(_decodeLock);

            var ringBuffer   = _ringBuffer;
            var handle       = _handle;
            var decodeThread = _decodeThread;

            _ringBuffer   = null;
            _handle       = IntPtr.Zero;
            _handleValid  = false;
            _decodeThread = null;
            FrameCount    = 0;
            FrameRate     = 0f;
            Width         = 0;
            Height        = 0;

            _closeThread = new Thread(() =>
            {
                if (decodeThread != null)
                    _decodeExited.Wait();

                ringBuffer?.Dispose();

                // Mark invalid before hap_close so a lingering decode thread (extremely
                // unlikely given the Wait above) can detect the handle is gone.
                _handleValid = false;
                if (handle != IntPtr.Zero)
                    HapNative.hap_close(handle);
            })
            {
                IsBackground = true,
                Name = "HapClose"
            };
            _closeThread.Start();
        }

        // ── Teardown (called from OnDestroy) ─────────────────────────────────

        /// <summary>
        /// Block until the open thread and close thread have both exited. Drains any
        /// unconsumed open result to prevent native handle leaks. Call before Dispose().
        /// </summary>
        public void Join()
        {
            if (_openThread != null)
            {
                _openThread.Join();
                _openThread = null;
                var leftover = _pendingOpenResult;
                _pendingOpenResult = null;
                if (leftover != null && leftover.Handle != IntPtr.Zero)
                    HapNative.hap_close(leftover.Handle);
            }
            _closeThread?.Join();
        }

        public void Dispose() => _decodeExited.Dispose();
    }
}
