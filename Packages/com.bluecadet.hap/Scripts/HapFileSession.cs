using System;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace Bluecadet.Hap
{
    internal enum SessionOpenStatus { NotReady, Opened, Failed }

    /// <summary>
    /// Owns the native handle for one open Hap file and the background threads around it:
    /// an open thread, a decode thread that fills the caller's texture ring, and a close
    /// thread that releases the handle once decoding has stopped.
    /// </summary>
    internal sealed class HapFileSession : IDisposable
    {
        /// <summary>
        /// How long <see cref="Close"/> waits on the main thread for the decode thread to
        /// stop. It only ever has to finish the frame in flight; the timeout exists so a
        /// wedged native call cannot hang the editor.
        /// </summary>
        const int DecodeExitTimeoutMs = 2000;

        /// <summary>Give up on a file after this many consecutive decode failures.</summary>
        const int MaxConsecutiveDecodeErrors = 10;

        // ── Metadata (valid while IsOpen) ────────────────────────────────────

        public int FrameCount => _open?.FrameCount ?? 0;
        public float FrameRate => _open?.FrameRate ?? 0f;
        public int Width => _open?.Width ?? 0;
        public int Height => _open?.Height ?? 0;

        /// <summary>Format and size of each texture a frame carries (1, or 2 for Hap Q Alpha).</summary>
        public HapTextureInfo[] Textures => _open?.Textures;

        public float Duration => FrameCount > 0 && FrameRate > 0f ? FrameCount / FrameRate : 0f;

        public bool IsOpen    => _handle != IntPtr.Zero;
        public bool IsOpening => _openThread != null;

        // ── Native handle ────────────────────────────────────────────────────

        /// <summary>
        /// The open thread's result once it has been consumed, which every metadata property
        /// reads through. Kept whole rather than copied out field by field.
        /// </summary>
        OpenResult _open;

        /// <summary>
        /// The live native handle. Separate from <see cref="_open"/> because teardown zeroes it
        /// while the metadata stays readable.
        /// </summary>
        IntPtr _handle;
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
        HapTextureRing _ring;
        readonly object _decodeLock = new();
        readonly ManualResetEventSlim _decodeExited = new(true);

        // ── Profiler markers ─────────────────────────────────────────────────

        static readonly ProfilerMarker s_DecodeMarker = new("HapPlayer.DecodeFrame");

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
            HapError error = HapNative.Open(resolved, out IntPtr handle);

            if (error != HapError.Ok || handle == IntPtr.Zero)
            {
                if (!_openCancelled)
                    Debug.LogError($"[HapPlayer] Failed to open '{resolved}': {error}");
                _pendingOpenResult = OpenResult.Failed(error == HapError.Ok ? HapError.FileRead : error);
                return;
            }

            int frameCount = HapNative.hap_get_frame_count(handle);
            float frameRate = HapNative.hap_get_frame_rate(handle);
            int textureCount = HapNative.hap_get_texture_count(handle);

            if (frameRate <= 0f || frameCount <= 0 || textureCount <= 0)
            {
                if (!_openCancelled)
                    Debug.LogError($"[HapPlayer] Invalid video ({frameCount} frames, {frameRate} fps, " +
                                   $"{textureCount} textures) in '{resolved}'");
                HapNative.hap_close(handle);
                _pendingOpenResult = OpenResult.Failed(HapError.CorruptTrack);
                return;
            }

            var textures = new HapTextureInfo[textureCount];
            for (int t = 0; t < textureCount; t++)
            {
                int nativeFormat = HapNative.hap_get_texture_format(handle, t);
                if (!HapFormatExtensions.TryToHapFormat(nativeFormat, out HapFormat format))
                {
                    // A texture layout this build has no format for: it would be decoded as
                    // something else entirely, so refuse the file instead.
                    Debug.LogWarning($"[HapPlayer] Texture {t} of '{FilePath}' uses unknown format {nativeFormat}");
                    HapNative.hap_close(handle);
                    _pendingOpenResult = OpenResult.Failed(HapError.UnsupportedVariant);
                    return;
                }

                textures[t] = new HapTextureInfo(
                    format, HapNative.hap_get_texture_buffer_size(handle, t));
            }

            _pendingOpenResult = new OpenResult(
                handle,
                frameCount,
                frameRate,
                HapNative.hap_get_width(handle),
                HapNative.hap_get_height(handle),
                textures
            );
        }

        // ── Poll for result (main thread) ────────────────────────────────────

        /// <summary>
        /// Call from the main thread in Update(). Returns Opened if a successful open result
        /// is ready (metadata properties are populated); Failed if the open failed or was
        /// cancelled; NotReady if the open thread is still running or no open was started.
        /// On Opened, the caller builds the texture ring and calls
        /// <see cref="StartDecoding"/>.
        /// </summary>
        public SessionOpenStatus TryConsumeOpenResult()
        {
            var result = _pendingOpenResult;
            if (result == null) return SessionOpenStatus.NotReady;

            _pendingOpenResult = null;
            _openThread = null;

            if (_openCancelled || !result.Success)
            {
                if (result.Handle != IntPtr.Zero)
                    HapNative.hap_close(result.Handle);
                return SessionOpenStatus.Failed;
            }

            _open   = result;
            _handle = result.Handle;

            return SessionOpenStatus.Opened;
        }

        // ── Decode thread lifecycle ──────────────────────────────────────────

        /// <summary>
        /// Start the decode thread and queue the first frame. Call from the main thread
        /// immediately after TryConsumeOpenResult() returns Opened and the texture ring exists.
        /// </summary>
        public void StartDecoding(HapTextureRing ring, int firstFrame)
        {
            _ring          = ring;
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

        void DecodeLoop()
        {
            // Capture fields that must remain valid for the lifetime of this thread.
            IntPtr handle = _handle;
            int frameCount = FrameCount;
            HapTextureRing ring = _ring;
            int textureCount = ring != null ? ring.TextureCount : 0;

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
                    if (ring == null) break;
                    if (!_handleValid) break;

                    int slot = ring.BeginWrite();
                    HapError result = HapError.Ok;

                    // Decode a frame's textures back to back: the second call reuses the
                    // sample the first one demuxed.
                    using (s_DecodeMarker.Auto())
                    {
                        for (int t = 0; t < textureCount; t++)
                        {
                            result = HapNative.DecodeTexture(handle, target, t,
                                                             ring.GetWritePtr(slot, t),
                                                             ring.GetBufferSize(t));
                            if (result != HapError.Ok) break;
                        }
                    }

                    if (result != HapError.Ok)
                    {
                        consecutiveErrors++;
                        Debug.LogWarning($"[HapPlayer] Failed to decode frame {target}: {result} " +
                                         $"({consecutiveErrors} consecutive)");
                        if (consecutiveErrors >= MaxConsecutiveDecodeErrors)
                        {
                            Debug.LogError($"[HapPlayer] Decode loop aborting after {consecutiveErrors} " +
                                           $"consecutive errors on '{_filePath}'");
                            break;
                        }
                    }
                    else
                    {
                        consecutiveErrors = 0;
                        ring.CommitWrite(slot, target);
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

        /// <summary>
        /// Stop decoding and release the native handle. The handle is closed on a background
        /// thread, but the decode thread is waited on here: the caller destroys the texture
        /// ring the decode thread writes into as soon as this returns.
        ///
        /// Returns false if the decode thread did not stop within
        /// <see cref="DecodeExitTimeoutMs"/>, meaning the ring must NOT be destroyed.
        /// </summary>
        public bool Close()
        {
            if (!IsOpen && _decodeThread == null) return true;

            _decodeRunning = false;
            lock (_decodeLock)
                Monitor.Pulse(_decodeLock);

            bool decodeStopped = _decodeThread == null || _decodeExited.Wait(DecodeExitTimeoutMs);
            if (!decodeStopped)
                Debug.LogError($"[HapPlayer] Decode thread did not stop within {DecodeExitTimeoutMs}ms " +
                               $"for '{_filePath}'");

            var handle       = _handle;
            var decodeThread = _decodeThread;

            _ring         = null;
            _handle       = IntPtr.Zero;
            _handleValid  = false;
            _decodeThread = null;
            FrameCount    = 0;
            FrameRate     = 0f;
            Width         = 0;
            Height        = 0;
            Textures      = null;

            _closeThread = new Thread(() =>
            {
                if (decodeThread != null)
                    _decodeExited.Wait();

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

            return decodeStopped;
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
