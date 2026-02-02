using System;
using System.Threading;
using UnityEngine;

namespace Bluecadet.Hap
{
    public class HapPlayer : MonoBehaviour
    {
        [SerializeField] string filePath;
        [SerializeField] bool playOnEnable = true;
        [SerializeField] bool loop = true;
        [SerializeField] Renderer targetRenderer;

        IntPtr _handle;
        HapFrameRingBuffer _ringBuffer;
        HapTextureUploader _uploader;

        int _frameCount;
        float _frameRate;
        float _duration;
        int _frameBufferSize;

        float _clock;
        bool _playing;
        int _lastUploadedFrame = -1;

        Thread _decodeThread;
        volatile bool _decodeRunning;
        volatile int _decodeTargetFrame = -1;
        readonly object _decodeLock = new object();

        public Texture2D Texture => _uploader?.Texture;
        public bool IsPlaying => _playing;
        public bool IsOpen => _handle != IntPtr.Zero;
        public int FrameCount => _frameCount;
        public float Duration => _duration;

        public float Time
        {
            get => _clock;
            set
            {
                _clock = Mathf.Clamp(value, 0f, _duration);
                int frame = ClockToFrame(_clock);
                RequestDecode(frame);
            }
        }

        void OnEnable()
        {
            Open();
            if (playOnEnable && IsOpen)
                Play();
        }

        void OnDisable()
        {
            Close();
        }

        void Update()
        {
            if (!IsOpen) return;

            if (_playing)
            {
                _clock += UnityEngine.Time.deltaTime;

                if (_clock >= _duration)
                {
                    if (loop)
                        _clock %= _duration;
                    else
                    {
                        _clock = _duration;
                        _playing = false;
                    }
                }

                int frame = ClockToFrame(_clock);
                RequestDecode(frame);
            }

            UploadFrame();

            if (targetRenderer != null && _uploader?.Texture != null)
            {
                targetRenderer.material.mainTexture = _uploader.Texture;
            }
        }

        public void Play()
        {
            if (!IsOpen) return;
            _playing = true;
        }

        public void Pause()
        {
            _playing = false;
        }

        public void Stop()
        {
            _playing = false;
            _clock = 0f;
            if (IsOpen)
                RequestDecode(0);
        }

        void Open()
        {
            if (string.IsNullOrEmpty(filePath)) return;

            int err;
            _handle = HapNative.hap_open(filePath, out err);
            if (_handle == IntPtr.Zero)
            {
                Debug.LogError($"[HapPlayer] Failed to open '{filePath}', error: {err}");
                return;
            }

            _frameCount = HapNative.hap_get_frame_count(_handle);
            _frameRate = HapNative.hap_get_frame_rate(_handle);
            _duration = _frameCount / _frameRate;
            _frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);

            int width = HapNative.hap_get_width(_handle);
            int height = HapNative.hap_get_height(_handle);
            int texFormat = HapNative.hap_get_texture_format(_handle);

            _ringBuffer = new HapFrameRingBuffer(_frameBufferSize);
            _uploader = new HapTextureUploader(width, height, texFormat);

            _clock = 0f;
            _lastUploadedFrame = -1;

            _decodeRunning = true;
            _decodeThread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "HapDecode"
            };
            _decodeThread.Start();

            RequestDecode(0);
        }

        void Close()
        {
            _playing = false;

            _decodeRunning = false;
            lock (_decodeLock)
                Monitor.Pulse(_decodeLock);

            if (_decodeThread != null)
            {
                _decodeThread.Join(500);
                _decodeThread = null;
            }

            _uploader?.Dispose();
            _uploader = null;

            _ringBuffer?.Dispose();
            _ringBuffer = null;

            if (_handle != IntPtr.Zero)
            {
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
            }

            _frameCount = 0;
            _frameRate = 0;
            _duration = 0;
            _lastUploadedFrame = -1;
        }

        int ClockToFrame(float clock)
        {
            int frame = Mathf.FloorToInt(clock * _frameRate);
            return Mathf.Clamp(frame, 0, _frameCount - 1);
        }

        void RequestDecode(int frame)
        {
            lock (_decodeLock)
            {
                _decodeTargetFrame = frame;
                Monitor.Pulse(_decodeLock);
            }
        }

        void DecodeLoop()
        {
            int lastDecoded = -1;

            while (_decodeRunning)
            {
                int target;
                lock (_decodeLock)
                {
                    while (_decodeRunning && _decodeTargetFrame == lastDecoded)
                        Monitor.Wait(_decodeLock, 2);

                    if (!_decodeRunning) break;
                    target = _decodeTargetFrame;
                }

                if (target == lastDecoded) continue;

                IntPtr buf = _ringBuffer.WritePtr;
                int result = HapNative.hap_decode_frame(_handle, target, buf, _frameBufferSize);

                if (result == HapNative.ErrorNone)
                {
                    _ringBuffer.CommitWrite(target);
                    lastDecoded = target;
                }
            }
        }

        void UploadFrame()
        {
            int readFrame = _ringBuffer.ReadFrame;
            if (readFrame < 0 || readFrame == _lastUploadedFrame) return;

            IntPtr ptr = _ringBuffer.ReadPtr;
            if (ptr == IntPtr.Zero) return;

            _uploader.Upload(ptr, _frameBufferSize);
            _lastUploadedFrame = readFrame;
        }
    }
}
