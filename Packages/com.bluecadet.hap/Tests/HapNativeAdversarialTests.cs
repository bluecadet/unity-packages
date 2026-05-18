using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Bluecadet.Hap;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class HapNativeAdversarialTests
    {
        private IntPtr _handle = IntPtr.Zero;

        private static string FixturePath => System.IO.Path.GetFullPath(
            System.IO.Path.Combine(
                UnityEngine.Application.dataPath,
                "../../../Packages/com.bluecadet.hap/Tests~/TestFixtures/test_64x64.mov"
            )
        );

        [TearDown]
        public void TearDown()
        {
            if (_handle != IntPtr.Zero)
            {
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Error path and boundary tests
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void HapReadSample_NegativeFrameIndex_ReturnsError()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int result = HapNative.hap_read_sample(_handle, -1);
            Assert.That(result, Is.LessThan(0), "Expected error (negative return) for negative frame index");
        }

        [Test]
        public void HapReadSample_FrameIndexOutOfRange_ReturnsError()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int result = HapNative.hap_read_sample(_handle, 9999);
            Assert.That(result, Is.LessThan(0), "Expected error (negative return) for out-of-range frame index");
        }

        [Test]
        public void HapDecompressFrame_BufferTooSmall_ReturnsError()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);

            int readBytes = HapNative.hap_read_sample(_handle, 0);
            Assert.That(readBytes, Is.GreaterThan(0), "hap_read_sample failed for frame 0");

            IntPtr tinyBuf = Marshal.AllocHGlobal(4);
            try
            {
                int result = HapNative.hap_decompress_frame(_handle, tinyBuf, 4);
                Assert.That(result, Is.Not.EqualTo(HapNative.ErrorNone), "Expected non-zero error when buffer is too small");
            }
            finally
            {
                Marshal.FreeHGlobal(tinyBuf);
            }
        }

        [Test]
        public void HapOpen_MultipleHandles_AllValid()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);

            IntPtr h1 = IntPtr.Zero;
            IntPtr h2 = IntPtr.Zero;
            IntPtr h3 = IntPtr.Zero;

            try
            {
                h1 = HapNative.hap_open(FixturePath, out int err1);
                h2 = HapNative.hap_open(FixturePath, out int err2);
                h3 = HapNative.hap_open(FixturePath, out int err3);

                Assert.That(h1, Is.Not.EqualTo(IntPtr.Zero), "Handle 1 should be valid");
                Assert.That(h2, Is.Not.EqualTo(IntPtr.Zero), "Handle 2 should be valid");
                Assert.That(h3, Is.Not.EqualTo(IntPtr.Zero), "Handle 3 should be valid");

                Assert.That(err1, Is.EqualTo(HapNative.ErrorNone));
                Assert.That(err2, Is.EqualTo(HapNative.ErrorNone));
                Assert.That(err3, Is.EqualTo(HapNative.ErrorNone));

                int w1 = HapNative.hap_get_width(h1);
                int w2 = HapNative.hap_get_width(h2);
                int w3 = HapNative.hap_get_width(h3);
                Assert.That(w2, Is.EqualTo(w1), "All handles should report same width");
                Assert.That(w3, Is.EqualTo(w1), "All handles should report same width");

                int h1Height = HapNative.hap_get_height(h1);
                Assert.That(HapNative.hap_get_height(h2), Is.EqualTo(h1Height));
                Assert.That(HapNative.hap_get_height(h3), Is.EqualTo(h1Height));

                int fc1 = HapNative.hap_get_frame_count(h1);
                Assert.That(HapNative.hap_get_frame_count(h2), Is.EqualTo(fc1));
                Assert.That(HapNative.hap_get_frame_count(h3), Is.EqualTo(fc1));
            }
            finally
            {
                if (h1 != IntPtr.Zero) HapNative.hap_close(h1);
                if (h2 != IntPtr.Zero) HapNative.hap_close(h2);
                if (h3 != IntPtr.Zero) HapNative.hap_close(h3);
            }
        }

        [Test]
        public void HapDecodeFrame_Interleaved_AllSucceed()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);

            int[] sequence = { 0, 15, 5, 29, 1, 14 };

            IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                foreach (int frameIndex in sequence)
                {
                    int readBytes = HapNative.hap_read_sample(_handle, frameIndex);
                    Assert.That(readBytes, Is.GreaterThan(0), $"hap_read_sample failed for frame {frameIndex}");

                    int result = HapNative.hap_decompress_frame(_handle, buf, frameBufferSize);
                    Assert.That(result, Is.EqualTo(HapNative.ErrorNone), $"hap_decompress_frame failed for frame {frameIndex}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        [Test]
        public void HapDecodeFrame_ThenReDecodesSameFrame_IdenticalOutput()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);

            IntPtr buf1 = Marshal.AllocHGlobal(frameBufferSize);
            IntPtr buf2 = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                int readBytes = HapNative.hap_read_sample(_handle, 10);
                Assert.That(readBytes, Is.GreaterThan(0), "hap_read_sample failed for frame 10 (first pass)");
                int result1 = HapNative.hap_decompress_frame(_handle, buf1, frameBufferSize);
                Assert.That(result1, Is.EqualTo(HapNative.ErrorNone), "First decompress of frame 10 failed");

                readBytes = HapNative.hap_read_sample(_handle, 10);
                Assert.That(readBytes, Is.GreaterThan(0), "hap_read_sample failed for frame 10 (second pass)");
                int result2 = HapNative.hap_decompress_frame(_handle, buf2, frameBufferSize);
                Assert.That(result2, Is.EqualTo(HapNative.ErrorNone), "Second decompress of frame 10 failed");

                for (int i = 0; i < frameBufferSize; i++)
                {
                    byte b1 = Marshal.ReadByte(buf1, i);
                    byte b2 = Marshal.ReadByte(buf2, i);
                    Assert.That(b2, Is.EqualTo(b1), $"Byte mismatch at offset {i}: decode is not deterministic");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf1);
                Marshal.FreeHGlobal(buf2);
            }
        }

        [Test]
        public void HapDecodeFrame_FirstAndLastFrame_Succeed()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);

            IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                int readBytes = HapNative.hap_read_sample(_handle, 0);
                Assert.That(readBytes, Is.GreaterThan(0), "hap_read_sample failed for first frame");
                int result = HapNative.hap_decompress_frame(_handle, buf, frameBufferSize);
                Assert.That(result, Is.EqualTo(HapNative.ErrorNone), "hap_decompress_frame failed for first frame");

                readBytes = HapNative.hap_read_sample(_handle, 29);
                Assert.That(readBytes, Is.GreaterThan(0), "hap_read_sample failed for last frame");
                result = HapNative.hap_decompress_frame(_handle, buf, frameBufferSize);
                Assert.That(result, Is.EqualTo(HapNative.ErrorNone), "hap_decompress_frame failed for last frame");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        [Test]
        public void HapReadSample_CalledWithoutDecompress_DoesNotCorruptState()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);

            int readBytes = HapNative.hap_read_sample(_handle, 5);
            Assert.That(readBytes, Is.GreaterThan(0), "hap_read_sample failed for frame 5");

            readBytes = HapNative.hap_read_sample(_handle, 10);
            Assert.That(readBytes, Is.GreaterThan(0), "hap_read_sample failed for frame 10");

            IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                int result = HapNative.hap_decompress_frame(_handle, buf, frameBufferSize);
                Assert.That(result, Is.EqualTo(HapNative.ErrorNone),
                    "hap_decompress_frame should succeed after skipping decompress of frame 5");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Robustness
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void HapOpen_EmptyString_ReturnsError()
        {
            IntPtr handle = HapNative.hap_open("", out int err);
            if (handle != IntPtr.Zero)
                HapNative.hap_close(handle);
            Assert.That(handle == IntPtr.Zero || err != HapNative.ErrorNone,
                "Opening an empty string path should return a null handle or a non-zero error code");
        }
    }
}
