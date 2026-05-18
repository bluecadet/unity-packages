using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Bluecadet.Hap;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class HapNativeTests
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

        [Test]
        public void HapOpen_NonExistentPath_ReturnsNullHandle()
        {
            IntPtr handle = HapNative.hap_open("/nonexistent/path/fake.mov", out int err);
            Assert.That(handle, Is.EqualTo(IntPtr.Zero));
            Assert.That(err, Is.EqualTo(HapNative.ErrorFile));
        }

        [Test]
        public void HapOpen_InvalidFileFormat_ReturnsFormatError()
        {
            string tempFile = System.IO.Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "not a hap file");
                IntPtr handle = HapNative.hap_open(tempFile, out int err);
                if (handle != IntPtr.Zero)
                    HapNative.hap_close(handle);
                Assert.That(handle, Is.EqualTo(IntPtr.Zero));
                Assert.That(err, Is.Not.EqualTo(HapNative.ErrorNone));
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Test]
        public void HapOpen_ValidFile_ReturnsValidHandle()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int err);
            Assert.That(_handle, Is.Not.EqualTo(IntPtr.Zero));
            Assert.That(err, Is.EqualTo(HapNative.ErrorNone));
        }

        [Test]
        public void HapGetWidth_Returns64()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            Assert.That(HapNative.hap_get_width(_handle), Is.EqualTo(64));
        }

        [Test]
        public void HapGetHeight_Returns64()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            Assert.That(HapNative.hap_get_height(_handle), Is.EqualTo(64));
        }

        [Test]
        public void HapGetFrameCount_Returns30()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            Assert.That(HapNative.hap_get_frame_count(_handle), Is.EqualTo(30));
        }

        [Test]
        public void HapGetFrameRate_Returns30()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            Assert.That(HapNative.hap_get_frame_rate(_handle), Is.EqualTo(30.0f).Within(0.001f));
        }

        [Test]
        public void HapGetTextureFormat_ReturnsDXT1()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            Assert.That(HapNative.hap_get_texture_format(_handle), Is.EqualTo(HapNative.TexFormatDXT1));
        }

        [Test]
        public void HapGetFrameBufferSize_Returns2048()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            Assert.That(HapNative.hap_get_frame_buffer_size(_handle), Is.EqualTo(2048));
        }

        [Test]
        public void HapDecodeFrame_Sequential_AllSucceed()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);
            int frameCount = HapNative.hap_get_frame_count(_handle);

            IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    int readBytes = HapNative.hap_read_sample(_handle, i);
                    Assert.That(readBytes, Is.GreaterThan(0), $"hap_read_sample failed for frame {i}");

                    int result = HapNative.hap_decompress_frame(_handle, buf, frameBufferSize);
                    Assert.That(result, Is.EqualTo(HapNative.ErrorNone), $"hap_decompress_frame failed for frame {i}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        [Test]
        public void HapDecodeFrame_Reverse_AllSucceed()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);
            int frameCount = HapNative.hap_get_frame_count(_handle);

            IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                for (int i = frameCount - 1; i >= 0; i--)
                {
                    int readBytes = HapNative.hap_read_sample(_handle, i);
                    Assert.That(readBytes, Is.GreaterThan(0), $"hap_read_sample failed for frame {i}");

                    int result = HapNative.hap_decompress_frame(_handle, buf, frameBufferSize);
                    Assert.That(result, Is.EqualTo(HapNative.ErrorNone), $"hap_decompress_frame failed for frame {i}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        [Test]
        public void HapDecodeFrame_Random_AllSucceed()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            _handle = HapNative.hap_open(FixturePath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);
            int frameCount = HapNative.hap_get_frame_count(_handle);

            var rng = new Random(42);
            IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                for (int n = 0; n < frameCount; n++)
                {
                    int i = rng.Next(0, frameCount);
                    int readBytes = HapNative.hap_read_sample(_handle, i);
                    Assert.That(readBytes, Is.GreaterThan(0), $"hap_read_sample failed for frame {i}");

                    int result = HapNative.hap_decompress_frame(_handle, buf, frameBufferSize);
                    Assert.That(result, Is.EqualTo(HapNative.ErrorNone), $"hap_decompress_frame failed for frame {i}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        [Test]
        public void HapClose_AfterOpen_DoesNotThrow()
        {
            Assume.That(File.Exists(FixturePath), "Test fixture not found: " + FixturePath);
            IntPtr handle = HapNative.hap_open(FixturePath, out int _);
            Assert.DoesNotThrow(() => HapNative.hap_close(handle));
        }
    }
}
