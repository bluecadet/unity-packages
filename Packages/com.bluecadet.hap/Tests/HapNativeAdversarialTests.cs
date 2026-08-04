using System;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Error paths, boundaries, and malformed input: everything the player has to survive
    /// without taking the editor down with it.
    /// </summary>
    [TestFixture]
    public class HapNativeAdversarialTests
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

        void OpenFixture(string path)
        {
            HapTestFixtures.Require(path);
            Assert.That(HapNative.Open(path, out _handle), Is.EqualTo(HapError.Ok));
        }

        // ── Frame and texture indices ────────────────────────────────────────

        [Test]
        public void DecodeTexture_NegativeFrameIndex_ReportsOutOfRange()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            using var buffer = HapTestFixtures.NativeBuffer(size);

            Assert.That(HapNative.DecodeTexture(_handle, -1, 0, buffer.Ptr, buffer.Size),
                Is.EqualTo(HapError.FrameOutOfRange));
        }

        [Test]
        public void DecodeTexture_FrameIndexPastEnd_ReportsOutOfRange()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            int frameCount = HapNative.hap_get_frame_count(_handle);
            using var buffer = HapTestFixtures.NativeBuffer(size);

            Assert.That(HapNative.DecodeTexture(_handle, frameCount, 0, buffer.Ptr, buffer.Size),
                Is.EqualTo(HapError.FrameOutOfRange));
            Assert.That(HapNative.DecodeTexture(_handle, 9999, 0, buffer.Ptr, buffer.Size),
                Is.EqualTo(HapError.FrameOutOfRange));
        }

        [Test]
        public void DecodeTexture_TextureIndexOutOfRange_ReportsInvalidArgument()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            using var buffer = HapTestFixtures.NativeBuffer(size);

            Assert.That(HapNative.DecodeTexture(_handle, 0, 1, buffer.Ptr, buffer.Size),
                Is.EqualTo(HapError.InvalidArgument));
            Assert.That(HapNative.DecodeTexture(_handle, 0, -1, buffer.Ptr, buffer.Size),
                Is.EqualTo(HapError.InvalidArgument));
        }

        [Test]
        public void DecodeTexture_BufferTooSmall_ReportsBufferTooSmall()
        {
            OpenFixture(HapTestFixtures.Hap1);
            using var tiny = HapTestFixtures.NativeBuffer(4);

            Assert.That(HapNative.DecodeTexture(_handle, 0, 0, tiny.Ptr, tiny.Size),
                Is.EqualTo(HapError.BufferTooSmall));
        }

        [Test]
        public void DecodeTexture_NullBuffer_ReportsInvalidArgument()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            Assert.That(HapNative.DecodeTexture(_handle, 0, 0, IntPtr.Zero, size),
                Is.EqualTo(HapError.InvalidArgument));
        }

        [Test]
        public void DecodeTexture_NullHandle_ReportsInvalidArgument()
        {
            using var buffer = HapTestFixtures.NativeBuffer(16);

            Assert.That(HapNative.DecodeTexture(IntPtr.Zero, 0, 0, buffer.Ptr, buffer.Size),
                Is.EqualTo(HapError.InvalidArgument));
        }

        [Test]
        public void DecodeTexture_OversizedBuffer_IsAccepted()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            using var buffer = HapTestFixtures.NativeBuffer(size * 2);

            Assert.That(HapNative.DecodeTexture(_handle, 0, 0, buffer.Ptr, buffer.Size), Is.EqualTo(HapError.Ok));
        }

        // ── Repeat and interleaved access ────────────────────────────────────

        [Test]
        public void DecodeTexture_SameFrameTwice_ProducesIdenticalBytes()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);

            byte[] first = HapTestFixtures.DecodeToBytes(_handle, 10, 0, size);
            byte[] second = HapTestFixtures.DecodeToBytes(_handle, 10, 0, size);
            Assert.That(second, Is.EqualTo(first), "decode is not deterministic");
        }

        [Test]
        public void DecodeTexture_ScrubbingOrder_AllSucceed()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            int frameCount = HapNative.hap_get_frame_count(_handle);
            int last = frameCount - 1;
            int[] sequence = { 0, last / 2, 5, last, 1, last - 1, 0 };

            using var buffer = HapTestFixtures.NativeBuffer(size);
            foreach (int frame in sequence)
                Assert.That(HapNative.DecodeTexture(_handle, frame, 0, buffer.Ptr, buffer.Size),
                    Is.EqualTo(HapError.Ok), $"frame {frame}");
        }

        [Test]
        public void DecodeTexture_AlphaBeforeColor_StillSucceeds()
        {
            HapTestFixtures.Require(HapTestFixtures.HapM);
            Assert.That(HapNative.Open(HapTestFixtures.HapM, out _handle), Is.EqualTo(HapError.Ok));

            int alphaSize = HapNative.hap_get_texture_buffer_size(_handle, 1);
            int colorSize = HapNative.hap_get_texture_buffer_size(_handle, 0);
            using var alpha = HapTestFixtures.NativeBuffer(alphaSize);
            using var color = HapTestFixtures.NativeBuffer(colorSize);

            Assert.That(HapNative.DecodeTexture(_handle, 0, 1, alpha.Ptr, alpha.Size), Is.EqualTo(HapError.Ok));
            Assert.That(HapNative.DecodeTexture(_handle, 0, 0, color.Ptr, color.Size), Is.EqualTo(HapError.Ok));
        }

        // ── Prefetch hints ───────────────────────────────────────────────────
        //
        // hap_prefetch_frame only advises the OS about paging, so it has no
        // observable result to assert on — its effect shows up as fewer major
        // faults on a cold page cache, which only the benchmark can measure.
        // What these cover is that it is harmless: no index can take the process
        // down, and decoding after any amount of hinting is still byte-exact.

        [Test]
        public void PrefetchFrame_AnyIndexOrNullHandle_DoesNotThrow()
        {
            OpenFixture(HapTestFixtures.Hap1);
            int frameCount = HapNative.hap_get_frame_count(_handle);

            Assert.DoesNotThrow(() =>
            {
                HapNative.hap_prefetch_frame(IntPtr.Zero, 0);
                HapNative.hap_prefetch_frame(_handle, -1);
                HapNative.hap_prefetch_frame(_handle, int.MinValue);
                HapNative.hap_prefetch_frame(_handle, frameCount);
                HapNative.hap_prefetch_frame(_handle, int.MaxValue);
                for (int frame = 0; frame < frameCount; frame++)
                    HapNative.hap_prefetch_frame(_handle, frame);
            });
        }

        [Test]
        public void PrefetchFrame_BeforeDecode_LeavesPixelsUnchanged()
        {
            string goldenPath = Path.Combine(HapTestFixtures.Dir, "hap1_golden.bin");
            HapTestFixtures.Require(goldenPath);
            OpenFixture(HapTestFixtures.Hap1);

            int size = HapNative.hap_get_texture_buffer_size(_handle, 0);
            int frameCount = HapNative.hap_get_frame_count(_handle);

            HapNative.hap_prefetch_frame(_handle, 0);
            HapNative.hap_prefetch_frame(_handle, frameCount - 1);
            HapNative.hap_prefetch_frame(_handle, frameCount + 100);

            Assert.That(HapTestFixtures.DecodeToBytes(_handle, 0, 0, size),
                Is.EqualTo(File.ReadAllBytes(goldenPath)));
        }

        [Test]
        public void PrefetchFrame_BetweenAQAlphaTexturePair_LeavesBothCorrect()
        {
            HapTestFixtures.Require(HapTestFixtures.HapM);
            HapTestFixtures.Require(HapTestFixtures.HapMGoldenTex0);
            HapTestFixtures.Require(HapTestFixtures.HapMGoldenTex1);
            Assert.That(HapNative.Open(HapTestFixtures.HapM, out _handle), Is.EqualTo(HapError.Ok));

            int colorSize = HapNative.hap_get_texture_buffer_size(_handle, 0);
            int alphaSize = HapNative.hap_get_texture_buffer_size(_handle, 1);

            byte[] color = HapTestFixtures.DecodeToBytes(_handle, 0, 0, colorSize);
            // The two textures of a frame share one demuxed sample; a hint landing
            // between them must not disturb it.
            HapNative.hap_prefetch_frame(_handle, 1);
            byte[] alpha = HapTestFixtures.DecodeToBytes(_handle, 0, 1, alphaSize);

            Assert.That(color, Is.EqualTo(File.ReadAllBytes(HapTestFixtures.HapMGoldenTex0)));
            Assert.That(alpha, Is.EqualTo(File.ReadAllBytes(HapTestFixtures.HapMGoldenTex1)));
        }

        // ── Multiple handles ─────────────────────────────────────────────────

        [Test]
        public void Open_MultipleHandlesOnOneFile_AllUsable()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            var handles = new IntPtr[3];
            try
            {
                for (int i = 0; i < handles.Length; i++)
                {
                    Assert.That(HapNative.Open(HapTestFixtures.Hap1, out handles[i]), Is.EqualTo(HapError.Ok));
                    Assert.That(handles[i], Is.Not.EqualTo(IntPtr.Zero));
                }

                int width = HapNative.hap_get_width(handles[0]);
                int frames = HapNative.hap_get_frame_count(handles[0]);
                foreach (var h in handles)
                {
                    Assert.That(HapNative.hap_get_width(h), Is.EqualTo(width));
                    Assert.That(HapNative.hap_get_frame_count(h), Is.EqualTo(frames));
                }
            }
            finally
            {
                foreach (var h in handles)
                    if (h != IntPtr.Zero) HapNative.hap_close(h);
            }
        }

        [Test]
        public void DecodeTexture_ConcurrentHandles_ProduceIdenticalBytes()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            // Different handles carry no shared state, so two threads may decode at once.
            IntPtr h1 = IntPtr.Zero, h2 = IntPtr.Zero;
            try
            {
                Assert.That(HapNative.Open(HapTestFixtures.Hap1, out h1), Is.EqualTo(HapError.Ok));
                Assert.That(HapNative.Open(HapTestFixtures.Hap1, out h2), Is.EqualTo(HapError.Ok));

                int size = HapNative.hap_get_texture_buffer_size(h1, 0);
                byte[] a = null, b = null;
                Exception failure = null;

                // A failed decode throws out of the worker, where NUnit cannot see it, so each
                // thread hands its exception back to be reported from here.
                Thread Decoding(IntPtr handle, Action<byte[]> store) => new(() =>
                {
                    try { store(HapTestFixtures.DecodeToBytes(handle, 3, 0, size)); }
                    catch (Exception ex) { Volatile.Write(ref failure, ex); }
                });

                var t1 = Decoding(h1, bytes => a = bytes);
                var t2 = Decoding(h2, bytes => b = bytes);
                t1.Start(); t2.Start();
                t1.Join(); t2.Join();

                Assert.That(failure, Is.Null, $"a decode thread threw: {failure}");
                Assert.That(b, Is.EqualTo(a));
            }
            finally
            {
                if (h1 != IntPtr.Zero) HapNative.hap_close(h1);
                if (h2 != IntPtr.Zero) HapNative.hap_close(h2);
            }
        }

        // ── Malformed input ──────────────────────────────────────────────────

        [Test]
        public void Open_FuzzRegressionInputs_FailCleanly()
        {
            var inputs = HapTestFixtures.FuzzRegressions;
            Assume.That(inputs.Length, Is.GreaterThan(0), "no fuzz regression inputs found");

            foreach (string path in inputs)
            {
                var error = HapNative.Open(path, out IntPtr handle);
                string name = Path.GetFileName(path);

                if (handle != IntPtr.Zero)
                {
                    // A few of these parse as containers; whatever they claim, the getters and
                    // the decoder must stay inside the file.
                    Assert.That(error, Is.EqualTo(HapError.Ok), name);
                    int size = HapNative.hap_get_texture_buffer_size(handle, 0);
                    if (size > 0)
                    {
                        using var buffer = HapTestFixtures.NativeBuffer(size);
                        int frames = HapNative.hap_get_frame_count(handle);
                        for (int i = 0; i < Math.Min(frames, 8); i++)
                            Assert.DoesNotThrow(() => HapNative.DecodeTexture(handle, i, 0, buffer.Ptr, buffer.Size), name);
                    }
                    HapNative.hap_close(handle);
                }
                else
                {
                    Assert.That(error, Is.Not.EqualTo(HapError.Ok), $"{name} returned no handle but reported success");
                }
            }
        }

        [Test]
        public void Open_TruncatedFixture_FailsWithoutHandle()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            byte[] full = File.ReadAllBytes(HapTestFixtures.Hap1);
            string tempFile = Path.GetTempFileName();
            try
            {
                var truncated = new byte[full.Length / 4];
                Array.Copy(full, truncated, truncated.Length);
                File.WriteAllBytes(tempFile, truncated);
                var error = HapNative.Open(tempFile, out IntPtr handle);
                if (handle != IntPtr.Zero) HapNative.hap_close(handle);
                Assert.That(handle, Is.EqualTo(IntPtr.Zero));
                Assert.That(error, Is.Not.EqualTo(HapError.Ok));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Test]
        public void Open_EmptyFile_FailsWithoutHandle()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tempFile, new byte[0]);
                var error = HapNative.Open(tempFile, out IntPtr handle);
                if (handle != IntPtr.Zero) HapNative.hap_close(handle);
                Assert.That(handle, Is.EqualTo(IntPtr.Zero));
                Assert.That(error, Is.Not.EqualTo(HapError.Ok));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Test]
        public void Open_DirectoryPath_FailsWithoutHandle()
        {
            var error = HapNative.Open(Path.GetTempPath(), out IntPtr handle);
            if (handle != IntPtr.Zero) HapNative.hap_close(handle);
            Assert.That(handle, Is.EqualTo(IntPtr.Zero));
            Assert.That(error, Is.Not.EqualTo(HapError.Ok));
        }
    }
}
