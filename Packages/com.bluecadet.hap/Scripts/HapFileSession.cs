using System;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace Bluecadet.Hap
{
    internal enum SessionOpenStatus { NotReady, Opened, Failed }

    /// <summary>
    /// One open file: the native handle, the background thread that opens it, and the decode
    /// thread that fills the caller's texture ring. A session covers a single file from open
    /// to teardown and is never reused — the player creates a new one per open.
    ///
    /// Teardown does not block the caller: <see cref="BeginTeardown"/> returns immediately and
    /// a background thread joins the open and decode threads and closes the handle. The owner
    /// polls <see cref="IsTornDown"/>, and only then destroys the textures the decode thread
    /// writes into.
    /// </summary>
    internal sealed class HapFileSession
    {
        /// <summary>Give up on a file after this many consecutive decode failures.</summary>
        const int MaxConsecutiveDecodeErrors = 10;

        // ── Metadata (valid once opened) ─────────────────────────────────────

        public string FilePath { get; }

        public int FrameCount => _open?.FrameCount ?? 0;
        public float FrameRate => _open?.FrameRate ?? 0f;
        public int Width => _open?.Width ?? 0;
        public int Height => _open?.Height ?? 0;

        /// <summary>Format and size of each texture a frame carries (1, or 2 for Hap Q Alpha).</summary>
        public HapTextureInfo[] Textures => _open?.Textures;

        /// <summary>Why the open failed, or <see cref="HapError.Ok"/>.</summary>
        public HapError Error => _open?.Error ?? HapError.Ok;

        public float Duration => FrameCount > 0 && FrameRate > 0f ? FrameCount / FrameRate : 0f;

        // ── Native handle ────────────────────────────────────────────────────

        /// <summary>
        /// The open thread's result once it has been consumed, which every metadata property
        /// reads through. Kept whole rather than copied out field by field.
        /// </summary>
        NativeOpenResult _open;

        /// <summary>
        /// The live native handle. Separate from <see cref="_open"/> because teardown zeroes it
        /// while the metadata stays readable.
        /// </summary>
        IntPtr _handle;

        // ── Open thread state ────────────────────────────────────────────────

        Thread _openThread;
        volatile NativeOpenResult _pendingOpenResult;

        // ── Decode thread state ──────────────────────────────────────────────

        Thread _decodeThread;
        volatile bool _decodeRunning;
        volatile bool _handleValid;

        /// <summary>The frame and direction the main thread last asked for. Guarded by <see cref="_decodeLock"/>.</summary>
        int _decodeTargetFrame = -1;
        int _decodeDirection = 1;

        HapTextureRing _ring;
        readonly object _decodeLock = new();

        // ── Teardown state ───────────────────────────────────────────────────

        bool _teardownStarted;

        /// <summary>
        /// Set by the teardown thread as its last act. A plain flag rather than an event:
        /// nothing here is disposable, so the owner can drop the session the moment it reads
        /// true without ever racing the signal.
        /// </summary>
        volatile bool _tornDown;

        /// <summary>True once the decode thread has parked and the native handle is closed.</summary>
        public bool IsTornDown => _tornDown;

        // ── Profiler markers ─────────────────────────────────────────────────

        static readonly ProfilerMarker s_DecodeMarker = new("HapPlayer.DecodeFrame");

        public HapFileSession(string filePath) => FilePath = filePath;

        // ── Open ─────────────────────────────────────────────────────────────

        /// <summary>Start opening the file on a background thread. Call once, from the main thread.</summary>
        public void Open()
        {
            _openThread = new Thread(OpenBackground)
            {
                IsBackground = true,
                Name = "HapOpen"
            };
            _openThread.Start();
        }

        void OpenBackground()
        {
            HapError error = HapNative.Open(FilePath, out IntPtr handle);

            // The plugin pairs Ok with a live handle; anything else is a bug in it, not a state
            // to paper over with an invented error code.
            Debug.Assert(error != HapError.Ok || handle != IntPtr.Zero,
                         "[HapPlayer] hap_open reported success without a handle");

            if (error != HapError.Ok || handle == IntPtr.Zero)
            {
                _pendingOpenResult = NativeOpenResult.Failed(error);
                return;
            }

            int frameCount = HapNative.hap_get_frame_count(handle);
            float frameRate = HapNative.hap_get_frame_rate(handle);
            int textureCount = HapNative.hap_get_texture_count(handle);

            if (frameRate <= 0f || frameCount <= 0 || textureCount <= 0)
            {
                HapNative.hap_close(handle);
                _pendingOpenResult = NativeOpenResult.Failed(HapError.CorruptTrack);
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
                    _pendingOpenResult = NativeOpenResult.Failed(HapError.UnsupportedVariant);
                    return;
                }

                textures[t] = new HapTextureInfo(
                    format, HapNative.hap_get_texture_buffer_size(handle, t));
            }

            _pendingOpenResult = new NativeOpenResult(
                handle,
                frameCount,
                frameRate,
                HapNative.hap_get_width(handle),
                HapNative.hap_get_height(handle),
                textures
            );
        }

        /// <summary>
        /// Poll the open from the main thread. Returns Opened once the metadata properties are
        /// populated and the caller can build a texture ring, Failed with <see cref="Error"/>
        /// set, or NotReady while the open thread is still working.
        /// </summary>
        public SessionOpenStatus TryConsumeOpenResult()
        {
            if (_teardownStarted) return SessionOpenStatus.Failed;

            var result = _pendingOpenResult;
            if (result == null) return SessionOpenStatus.NotReady;

            _pendingOpenResult = null;

            _open = result;

            if (!result.Success)
            {
                if (result.Handle != IntPtr.Zero)
                    HapNative.hap_close(result.Handle);
                return SessionOpenStatus.Failed;
            }

            _handle = result.Handle;
            return SessionOpenStatus.Opened;
        }

        // ── Decode thread lifecycle ──────────────────────────────────────────

        /// <summary>
        /// Start the decode thread and queue the first frame. Call from the main thread once
        /// <see cref="TryConsumeOpenResult"/> has returned Opened and the texture ring exists.
        /// </summary>
        public void StartDecoding(HapTextureRing ring, int firstFrame)
        {
            _ring          = ring;
            _handleValid   = true;
            _decodeRunning = true;
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

            var scheduler = new DecodeScheduler(frameCount);
            int consecutiveErrors = 0;

            while (_decodeRunning)
            {
                DecodeRequest request;
                int dir;

                lock (_decodeLock)
                    request = WaitForRequest(scheduler, out dir);

                // Only reached with nothing left to decode because the session is stopping.
                if (request.Kind == DecodeRequestKind.Wait) break;
                if (request.Kind == DecodeRequestKind.Skip) continue;

                if (ring == null) break;
                if (!_handleValid) break;

                int target = request.Frame;
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
                                       $"consecutive errors on '{FilePath}'");
                        break;
                    }
                }
                else
                {
                    consecutiveErrors = 0;
                    ring.CommitWrite(slot, target);
                    scheduler.OnDecoded(target, request.IsPrefetch, dir);
                }
            }
        }

        /// <summary>
        /// The scheduler's next instruction, parking the thread while it has nothing to decode.
        /// Call with <see cref="_decodeLock"/> held. Only returns
        /// <see cref="DecodeRequestKind.Wait"/> when the session is shutting down, so the caller
        /// treats that as "stop" rather than "block".
        /// </summary>
        DecodeRequest WaitForRequest(DecodeScheduler scheduler, out int dir)
        {
            while (true)
            {
                dir = _decodeDirection;
                var request = scheduler.Next(_decodeTargetFrame, dir);
                if (request.Kind != DecodeRequestKind.Wait) return request;
                if (!_decodeRunning) return DecodeRequest.Wait;

                // Block until the main thread requests a new frame or signals exit. The 100ms
                // safety timeout guards against a Pulse missed during a teardown race.
                while (_decodeRunning && _decodeTargetFrame == scheduler.LastExplicit)
                    Monitor.Wait(_decodeLock, 100);

                if (!_decodeRunning) return DecodeRequest.Wait;
            }
        }

        // ── Teardown ─────────────────────────────────────────────────────────

        /// <summary>
        /// Stop decoding and release the file without blocking. A background thread joins the
        /// open and decode threads and closes the native handle; watch <see cref="IsTornDown"/>
        /// (or <see cref="WaitForTeardown"/>) before destroying the textures being decoded
        /// into. Repeat calls are ignored. Main thread only.
        /// </summary>
        public void BeginTeardown()
        {
            if (_teardownStarted) return;
            _teardownStarted = true;

            _decodeRunning = false;
            lock (_decodeLock)
                Monitor.Pulse(_decodeLock);

            var openThread   = _openThread;
            var decodeThread = _decodeThread;
            var handle       = _handle;

            _openThread   = null;
            _decodeThread = null;
            _handle       = IntPtr.Zero;
            _handleValid  = false;
            _ring         = null;

            var teardownThread = new Thread(() =>
            {
                // The open thread may still be memory-mapping the file, and it hands back a
                // handle nobody consumed — that one is ours to close too.
                openThread?.Join();
                var unconsumed = _pendingOpenResult;
                _pendingOpenResult = null;
                if (unconsumed != null && unconsumed.Handle != IntPtr.Zero && unconsumed.Handle != handle)
                    HapNative.hap_close(unconsumed.Handle);

                // The decode thread only has to finish the frame in flight. Joining it is what
                // makes closing the handle safe: it can no longer be inside a decode call, and
                // it can no longer be writing into the ring's textures.
                decodeThread?.Join();

                if (handle != IntPtr.Zero)
                    HapNative.hap_close(handle);

                _tornDown = true;
            })
            {
                IsBackground = true,
                Name = "HapTeardown"
            };
            teardownThread.Start();
        }

        /// <summary>
        /// Block for up to <paramref name="timeoutMs"/> waiting for the teardown to finish.
        /// Only for the destroy path, where nothing can await it; everywhere else, poll
        /// <see cref="IsTornDown"/>.
        /// </summary>
        public bool WaitForTeardown(int timeoutMs)
        {
            if (_tornDown) return true;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (_tornDown) return true;
                Thread.Sleep(1);
            }
            return _tornDown;
        }
    }
}
