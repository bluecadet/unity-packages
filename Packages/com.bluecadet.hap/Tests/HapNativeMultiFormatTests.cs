using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Bluecadet.Hap;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class HapNativeMultiFormatTests
    {
        private IntPtr _handle = IntPtr.Zero;

        private static string FixturesDir => Path.GetFullPath(
            Path.Combine(
                UnityEngine.Application.dataPath,
                "../../../Packages/com.bluecadet.hap/Tests~/TestFixtures"
            )
        );

        private static string DXT1Path  => Path.Combine(FixturesDir, "test_64x64.mov");
        private static string DXT5Path  => Path.Combine(FixturesDir, "test_64x64_hap_alpha.mov");
        private static string HapQPath  => Path.Combine(FixturesDir, "test_64x64_hap_q.mov");

        [TearDown]
        public void TearDown()
        {
            if (_handle != IntPtr.Zero)
            {
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        // ── HAP Alpha (DXT5) ────────────────────────────────────────────────

        [Test]
        public void HapAlpha_Open_ReturnsValidHandle()
        {
            Assume.That(File.Exists(DXT5Path), "HAP Alpha fixture not found: " + DXT5Path);
            _handle = HapNative.hap_open(DXT5Path, out int err);
            Assert.That(_handle, Is.Not.EqualTo(System.IntPtr.Zero));
            Assert.That(err, Is.EqualTo(HapNative.ErrorNone));
        }

        [Test]
        public void HapAlpha_TextureFormat_ReturnsDXT5()
        {
            Assume.That(File.Exists(DXT5Path), "HAP Alpha fixture not found: " + DXT5Path);
            _handle = HapNative.hap_open(DXT5Path, out int _);
            Assert.That(HapNative.hap_get_texture_format(_handle), Is.EqualTo(HapNative.TexFormatDXT5));
        }

        [Test]
        public void HapAlpha_FrameBufferSize_Returns4096()
        {
            Assume.That(File.Exists(DXT5Path), "HAP Alpha fixture not found: " + DXT5Path);
            _handle = HapNative.hap_open(DXT5Path, out int _);
            // DXT5 = 1 byte/pixel, 64×64 = 4096 bytes
            Assert.That(HapNative.hap_get_frame_buffer_size(_handle), Is.EqualTo(4096));
        }

        [Test]
        public void HapAlpha_Metadata_MatchesExpected()
        {
            Assume.That(File.Exists(DXT5Path), "HAP Alpha fixture not found: " + DXT5Path);
            _handle = HapNative.hap_open(DXT5Path, out int _);
            Assert.That(HapNative.hap_get_width(_handle),       Is.EqualTo(64));
            Assert.That(HapNative.hap_get_height(_handle),      Is.EqualTo(64));
            Assert.That(HapNative.hap_get_frame_count(_handle), Is.EqualTo(30));
            Assert.That(HapNative.hap_get_frame_rate(_handle),  Is.EqualTo(30f).Within(0.001f));
        }

        [Test]
        public void HapAlpha_DecodeAllFrames_AllSucceed()
        {
            Assume.That(File.Exists(DXT5Path), "HAP Alpha fixture not found: " + DXT5Path);
            _handle = HapNative.hap_open(DXT5Path, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);
            int frameCount      = HapNative.hap_get_frame_count(_handle);

            System.IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    Assert.That(HapNative.hap_read_sample(_handle, i),        Is.GreaterThan(0), $"read_sample failed for frame {i}");
                    Assert.That(HapNative.hap_decompress_frame(_handle, buf, frameBufferSize), Is.EqualTo(HapNative.ErrorNone), $"decompress failed for frame {i}");
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ── HAP Q (YCoCg DXT5) ─────────────────────────────────────────────

        [Test]
        public void HapQ_Open_ReturnsValidHandle()
        {
            Assume.That(File.Exists(HapQPath), "HAP Q fixture not found: " + HapQPath);
            _handle = HapNative.hap_open(HapQPath, out int err);
            Assert.That(_handle, Is.Not.EqualTo(System.IntPtr.Zero));
            Assert.That(err, Is.EqualTo(HapNative.ErrorNone));
        }

        [Test]
        public void HapQ_TextureFormat_ReturnsYCoCgDXT5()
        {
            Assume.That(File.Exists(HapQPath), "HAP Q fixture not found: " + HapQPath);
            _handle = HapNative.hap_open(HapQPath, out int _);
            Assert.That(HapNative.hap_get_texture_format(_handle), Is.EqualTo(HapNative.TexFormatYCoCgDXT5));
        }

        [Test]
        public void HapQ_FrameBufferSize_Returns4096()
        {
            Assume.That(File.Exists(HapQPath), "HAP Q fixture not found: " + HapQPath);
            _handle = HapNative.hap_open(HapQPath, out int _);
            // YCoCg DXT5 = 1 byte/pixel, 64×64 = 4096 bytes
            Assert.That(HapNative.hap_get_frame_buffer_size(_handle), Is.EqualTo(4096));
        }

        [Test]
        public void HapQ_Metadata_MatchesExpected()
        {
            Assume.That(File.Exists(HapQPath), "HAP Q fixture not found: " + HapQPath);
            _handle = HapNative.hap_open(HapQPath, out int _);
            Assert.That(HapNative.hap_get_width(_handle),       Is.EqualTo(64));
            Assert.That(HapNative.hap_get_height(_handle),      Is.EqualTo(64));
            Assert.That(HapNative.hap_get_frame_count(_handle), Is.EqualTo(30));
            Assert.That(HapNative.hap_get_frame_rate(_handle),  Is.EqualTo(30f).Within(0.001f));
        }

        [Test]
        public void HapQ_DecodeAllFrames_AllSucceed()
        {
            Assume.That(File.Exists(HapQPath), "HAP Q fixture not found: " + HapQPath);
            _handle = HapNative.hap_open(HapQPath, out int _);
            int frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);
            int frameCount      = HapNative.hap_get_frame_count(_handle);

            System.IntPtr buf = Marshal.AllocHGlobal(frameBufferSize);
            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    Assert.That(HapNative.hap_read_sample(_handle, i),        Is.GreaterThan(0), $"read_sample failed for frame {i}");
                    Assert.That(HapNative.hap_decompress_frame(_handle, buf, frameBufferSize), Is.EqualTo(HapNative.ErrorNone), $"decompress failed for frame {i}");
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ── Cross-format ────────────────────────────────────────────────────

        [Test]
        public void AllFormats_TextureFormatCodesAreDistinct()
        {
            Assume.That(File.Exists(DXT1Path), "DXT1 fixture not found");
            Assume.That(File.Exists(DXT5Path), "DXT5 fixture not found");
            Assume.That(File.Exists(HapQPath), "HAP Q fixture not found");

            IntPtr h1 = HapNative.hap_open(DXT1Path, out int _);
            IntPtr h2 = HapNative.hap_open(DXT5Path, out int _);
            IntPtr h3 = HapNative.hap_open(HapQPath, out int _);
            try
            {
                int fmt1 = HapNative.hap_get_texture_format(h1);
                int fmt2 = HapNative.hap_get_texture_format(h2);
                int fmt3 = HapNative.hap_get_texture_format(h3);

                Assert.That(fmt1, Is.EqualTo(HapNative.TexFormatDXT1));
                Assert.That(fmt2, Is.EqualTo(HapNative.TexFormatDXT5));
                Assert.That(fmt3, Is.EqualTo(HapNative.TexFormatYCoCgDXT5));
                Assert.That(fmt1, Is.Not.EqualTo(fmt2));
                Assert.That(fmt2, Is.Not.EqualTo(fmt3));
                Assert.That(fmt1, Is.Not.EqualTo(fmt3));
            }
            finally
            {
                if (h1 != System.IntPtr.Zero) HapNative.hap_close(h1);
                if (h2 != System.IntPtr.Zero) HapNative.hap_close(h2);
                if (h3 != System.IntPtr.Zero) HapNative.hap_close(h3);
            }
        }

        [Test]
        public void DXT1VsDXT5_FrameBufferSizeDiffers()
        {
            Assume.That(File.Exists(DXT1Path), "DXT1 fixture not found");
            Assume.That(File.Exists(DXT5Path), "DXT5 fixture not found");

            IntPtr h1 = HapNative.hap_open(DXT1Path, out int _);
            IntPtr h2 = HapNative.hap_open(DXT5Path, out int _);
            try
            {
                int size1 = HapNative.hap_get_frame_buffer_size(h1);
                int size2 = HapNative.hap_get_frame_buffer_size(h2);
                // DXT5 holds 2× as much data as DXT1 for the same dimensions
                Assert.That(size2, Is.EqualTo(size1 * 2));
            }
            finally
            {
                if (h1 != System.IntPtr.Zero) HapNative.hap_close(h1);
                if (h2 != System.IntPtr.Zero) HapNative.hap_close(h2);
            }
        }
    }
}
