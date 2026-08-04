using System;
using System.IO;
using NUnit.Framework;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Baseline coverage of the native plugin's C ABI through the P/Invoke bindings,
    /// using the plain Hap (DXT1) fixture.
    /// </summary>
    [TestFixture]
    public class HapNativeTests
    {
        IntPtr _handle = IntPtr.Zero;

        [TearDown]
        public void TearDown()
        {
            if (_handle != IntPtr.Zero)
            {
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        HapError OpenFixture(string path)
        {
            HapTestFixtures.Require(path);
            return HapNative.Open(path, out _handle);
        }

        // ── ABI parity pin ───────────────────────────────────────────────────
        //
        // The result codes and texture format codes are hand-maintained in three places:
        // Native~/src/bluecadet_hap.zig, the C header that mirrors it
        // (Native~/src/bluecadet_hap.h), and the C# enums in Scripts. Nothing makes those
        // agree, and a renumbering on any side turns into silently mismatched values across
        // the P/Invoke boundary. These two tests are a deliberate fourth copy of the header's
        // numbers: touch any side without the others and this fails instead of drifting.

        [Test]
        public void HapError_MembersKeepTheirAbiValues()
        {
            Assert.That((int)HapError.Ok, Is.EqualTo(0));
            Assert.That((int)HapError.InvalidArgument, Is.EqualTo(1));
            Assert.That((int)HapError.FileNotFound, Is.EqualTo(2));
            Assert.That((int)HapError.FileRead, Is.EqualTo(3));
            Assert.That((int)HapError.NotAMov, Is.EqualTo(4));
            Assert.That((int)HapError.NoHapTrack, Is.EqualTo(5));
            Assert.That((int)HapError.UnsupportedVariant, Is.EqualTo(6));
            Assert.That((int)HapError.CorruptTrack, Is.EqualTo(7));
            Assert.That((int)HapError.FrameOutOfRange, Is.EqualTo(8));
            Assert.That((int)HapError.InvalidFrame, Is.EqualTo(9));
            Assert.That((int)HapError.BufferTooSmall, Is.EqualTo(10));
            Assert.That((int)HapError.OutOfMemory, Is.EqualTo(11));

            Assert.That(Enum.GetValues(typeof(HapError)).Length, Is.EqualTo(12),
                "a result code was added or removed; give it its header value here too");
        }

        [Test]
        public void HapFormat_MembersKeepTheirAbiValues()
        {
            Assert.That((int)HapFormat.DXT1, Is.EqualTo(1));
            Assert.That((int)HapFormat.DXT5, Is.EqualTo(2));
            Assert.That((int)HapFormat.BC7, Is.EqualTo(3));
            Assert.That((int)HapFormat.YCoCgDXT5, Is.EqualTo(4));
            Assert.That((int)HapFormat.RGTC1, Is.EqualTo(5));

            Assert.That(Enum.GetValues(typeof(HapFormat)).Length, Is.EqualTo(5),
                "a texture format was added or removed; give it its header value here too");

            // 0 is the header's "invalid handle or index" answer, so no format may claim it.
            Assert.That(Enum.IsDefined(typeof(HapFormat), 0), Is.False);
        }

        // ── Open ─────────────────────────────────────────────────────────────

        [Test]
        public void Open_NonExistentPath_ReportsFileNotFound()
        {
            var error = HapNative.Open("/nonexistent/path/fake.mov", out IntPtr handle);
            Assert.That(handle, Is.EqualTo(IntPtr.Zero));
            Assert.That(error, Is.EqualTo(HapError.FileNotFound));
        }

        [Test]
        public void Open_EmptyPath_ReportsInvalidArgument()
        {
            var error = HapNative.Open("", out IntPtr handle);
            Assert.That(handle, Is.EqualTo(IntPtr.Zero));
            Assert.That(error, Is.EqualTo(HapError.InvalidArgument));
        }

        [Test]
        public void Open_NotAContainer_ReportsNotAMov()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "not a hap file");
                var error = HapNative.Open(tempFile, out IntPtr handle);
                if (handle != IntPtr.Zero) HapNative.hap_close(handle);
                Assert.That(handle, Is.EqualTo(IntPtr.Zero));
                Assert.That(error, Is.EqualTo(HapError.NotAMov));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Test]
        public void Open_ValidFile_ReturnsHandle()
        {
            var error = OpenFixture(HapTestFixtures.Hap1);
            Assert.That(error, Is.EqualTo(HapError.Ok));
            Assert.That(_handle, Is.Not.EqualTo(IntPtr.Zero));
        }

        // ── Metadata ─────────────────────────────────────────────────────────

        [Test]
        public void Metadata_MatchesFixture()
        {
            OpenFixture(HapTestFixtures.Hap1);
            Assert.That(HapNative.hap_get_width(_handle), Is.EqualTo(HapTestFixtures.Width));
            Assert.That(HapNative.hap_get_height(_handle), Is.EqualTo(HapTestFixtures.Height));
            Assert.That(HapNative.hap_get_frame_count(_handle), Is.GreaterThan(0));
            Assert.That(HapNative.hap_get_frame_rate(_handle), Is.EqualTo(30f).Within(0.001f));
        }

        [Test]
        public void Getters_NullHandle_ReturnZero()
        {
            Assert.That(HapNative.hap_get_width(IntPtr.Zero), Is.EqualTo(0));
            Assert.That(HapNative.hap_get_height(IntPtr.Zero), Is.EqualTo(0));
            Assert.That(HapNative.hap_get_frame_count(IntPtr.Zero), Is.EqualTo(0));
            Assert.That(HapNative.hap_get_frame_rate(IntPtr.Zero), Is.EqualTo(0f));
            Assert.That(HapNative.hap_get_texture_count(IntPtr.Zero), Is.EqualTo(0));
            Assert.That(HapNative.hap_get_texture_format(IntPtr.Zero, 0), Is.EqualTo(0));
            Assert.That(HapNative.hap_get_texture_buffer_size(IntPtr.Zero, 0), Is.EqualTo(0));
        }

        // ── Decode ───────────────────────────────────────────────────────────

        [Test]
        public void DecodeTexture_Sequential_AllSucceed()
        {
            OpenFixture(HapTestFixtures.Hap1);
            DecodeAll(FrameOrder.Sequential);
        }

        [Test]
        public void DecodeTexture_Reverse_AllSucceed()
        {
            OpenFixture(HapTestFixtures.Hap1);
            DecodeAll(FrameOrder.Reverse);
        }

        [Test]
        public void DecodeTexture_Random_AllSucceed()
        {
            OpenFixture(HapTestFixtures.Hap1);
            DecodeAll(FrameOrder.Random);
        }

        [Test]
        public void DecodeTexture_MatchesGoldenFrameZero()
        {
            string goldenPath = Path.Combine(HapTestFixtures.Dir, "hap1_golden.bin");
            HapTestFixtures.Require(goldenPath);
            OpenFixture(HapTestFixtures.Hap1);

            byte[] golden = File.ReadAllBytes(goldenPath);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            Assert.That(size, Is.EqualTo(golden.Length));

            Assert.That(HapTestFixtures.DecodeToBytes(_handle, 0, 0, size), Is.EqualTo(golden));
        }

        [Test]
        public void Close_AfterOpen_DoesNotThrow()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            HapNative.Open(HapTestFixtures.Hap1, out IntPtr handle);
            Assert.DoesNotThrow(() => HapNative.hap_close(handle));
        }

        [Test]
        public void Close_NullHandle_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => HapNative.hap_close(IntPtr.Zero));
        }

        // ── Thread count (process-global) ────────────────────────────────────

        [Test]
        public void SetThreadCount_ZeroOrNegative_ReportsInvalidArgument()
        {
            Assert.That(HapNative.SetThreadCount(0), Is.EqualTo(HapError.InvalidArgument));
            Assert.That(HapNative.SetThreadCount(-1), Is.EqualTo(HapError.InvalidArgument));
        }

        [Test]
        public void SetThreadCount_DecodeStillSucceeds()
        {
            OpenFixture(HapTestFixtures.Hap1);
            try
            {
                Assert.That(HapNative.SetThreadCount(1), Is.EqualTo(HapError.Ok));
                DecodeAll(FrameOrder.Sequential);
                Assert.That(HapNative.SetThreadCount(64), Is.EqualTo(HapError.Ok));
                DecodeAll(FrameOrder.Sequential);
            }
            finally
            {
                // Process-global: put the pool back to "as many workers as it has".
                HapNative.SetThreadCount(64);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        enum FrameOrder { Sequential, Reverse, Random }

        void DecodeAll(FrameOrder order)
        {
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            int frameCount = HapNative.hap_get_frame_count(_handle);
            var rng = new Random(42);

            using var buffer = HapTestFixtures.NativeBuffer(size);
            for (int n = 0; n < frameCount; n++)
            {
                int frame = order switch
                {
                    FrameOrder.Reverse => frameCount - 1 - n,
                    FrameOrder.Random  => rng.Next(0, frameCount),
                    _                  => n,
                };
                Assert.That(HapNative.DecodeTexture(_handle, frame, 0, buffer.Ptr, buffer.Size),
                    Is.EqualTo(HapError.Ok), $"decode failed for frame {frame}");
            }
        }
    }
}
