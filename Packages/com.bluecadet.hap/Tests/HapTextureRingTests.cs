using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// The ring of decode-target textures: one texture per texture index per slot, a cached
    /// raw pointer for each, and the pin/commit protocol on top.
    /// </summary>
    [TestFixture]
    public class HapTextureRingTests
    {
        const int Width = 64;
        const int Height = 64;

        static readonly int Dxt1Size = HapTestFixtures.BlockBytes(HapFormat.DXT1, Width, Height);
        static readonly int Dxt5Size = HapTestFixtures.BlockBytes(HapFormat.DXT5, Width, Height);

        readonly List<HapTextureRing> _rings = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var ring in _rings) ring.Dispose();
            _rings.Clear();
        }

        HapTextureRing NewRing(params HapTextureInfo[] textures)
        {
            const int retireDepth = 2;
            var ring = new HapTextureRing(Width, Height, textures, retireDepth);
            _rings.Add(ring);
            return ring;
        }

        static HapTextureInfo Dxt1 => new(HapFormat.DXT1, Dxt1Size);
        static HapTextureInfo Dxt5 => new(HapFormat.DXT5, Dxt5Size);
        static HapTextureInfo YCoCg => new(HapFormat.YCoCgDXT5, Dxt5Size);
        static HapTextureInfo Rgtc1 => new(HapFormat.RGTC1, Dxt1Size);

        // ── Texture creation ─────────────────────────────────────────────────

        [TestCase(1, TextureFormat.DXT1)]
        [TestCase(2, TextureFormat.DXT5)]
        [TestCase(3, TextureFormat.BC7)]
        [TestCase(4, TextureFormat.DXT5)]
        [TestCase(5, TextureFormat.BC4)]
        public void Slots_UseTheUnityFormatForEachHapFormat(int hapFormatCode, TextureFormat unityFormat)
        {
            int bufferSize = HapTestFixtures.BlockBytes((HapFormat)hapFormatCode, Width, Height);
            var ring = NewRing(new HapTextureInfo((HapFormat)hapFormatCode, bufferSize));
            Assert.That(ring.IsValid, Is.True);

            for (int slot = 0; slot < ring.SlotCount; slot++)
            {
                var tex = ring.GetTexture(slot, 0);
                Assert.That(tex, Is.Not.Null);
                Assert.That(tex.format, Is.EqualTo(unityFormat));
                Assert.That(tex.width, Is.EqualTo(Width));
                Assert.That(tex.height, Is.EqualTo(Height));
            }
            Assert.That(ring.GetBufferSize(0), Is.EqualTo(bufferSize));
        }

        [Test]
        public void UnknownFormat_FallsBackToDxt1WithAWarning()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Unknown texture format"));
            var ring = NewRing(new HapTextureInfo((HapFormat)(-1), Dxt1Size));
            Assert.That(ring.GetTexture(0, 0).format, Is.EqualTo(TextureFormat.DXT1));
        }

        [Test]
        public void TooSmallATexture_IsReportedInvalid()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("video will not play"));
            // Claim the decoder needs more bytes than a 64x64 DXT1 texture can hold.
            var ring = NewRing(new HapTextureInfo(HapFormat.DXT1, Dxt1Size * 2));
            Assert.That(ring.IsValid, Is.False);
        }

        [Test]
        public void EverySlotAndTexture_HasItsOwnDistinctPointer()
        {
            var ring = NewRing(YCoCg, Rgtc1);
            Assert.That(ring.TextureCount, Is.EqualTo(2));

            var seen = new HashSet<IntPtr>();
            for (int slot = 0; slot < ring.SlotCount; slot++)
            {
                for (int t = 0; t < ring.TextureCount; t++)
                {
                    IntPtr ptr = ring.GetWritePtr(slot, t);
                    Assert.That(ptr, Is.Not.EqualTo(IntPtr.Zero));
                    Assert.That(seen.Add(ptr), Is.True, $"slot {slot} texture {t} aliases another buffer");
                }
            }
        }

        [Test]
        public void DualTextureRing_ReportsEachTexturesOwnSize()
        {
            var ring = NewRing(YCoCg, Rgtc1);
            Assert.That(ring.GetBufferSize(0), Is.EqualTo(Dxt5Size));
            Assert.That(ring.GetBufferSize(1), Is.EqualTo(Dxt1Size));
            Assert.That(ring.GetTexture(0, 0).format, Is.EqualTo(TextureFormat.DXT5));
            Assert.That(ring.GetTexture(0, 1).format, Is.EqualTo(TextureFormat.BC4));
        }

        // ── Lease protocol ───────────────────────────────────────────────────

        [Test]
        public void TryAcquire_BeforeAnyCommit_ReturnsFalse()
        {
            // A refused acquire hands back nothing usable: the lease it writes out is only
            // valid, and only safe to dispose, when the call returned true.
            var ring = NewRing(Dxt1);
            Assert.That(ring.TryAcquire(out _), Is.False);
        }

        [Test]
        public void TryAcquire_AfterCommit_LeasesTheCommittedFrameTextures()
        {
            var ring = NewRing(YCoCg, Rgtc1);
            int slot = ring.BeginWrite();
            ring.CommitWrite(slot, 12);

            Assert.That(ring.TryAcquire(out var lease), Is.True);
            using (lease)
            {
                Assert.That(lease.FrameIndex, Is.EqualTo(12));
                Assert.That(lease.Slot, Is.EqualTo(slot));
                Assert.That(lease.ColorTexture, Is.SameAs(ring.GetTexture(slot, 0)));
                Assert.That(lease.AlphaTexture, Is.SameAs(ring.GetTexture(slot, 1)));
                Assert.DoesNotThrow(() => lease.Apply());
            }
        }

        [Test]
        public void SingleTextureRing_LeaseHasNoAlphaTexture()
        {
            var ring = NewRing(Dxt1);
            ring.CommitWrite(ring.BeginWrite(), 0);

            Assert.That(ring.TryAcquire(out var lease), Is.True);
            using (lease)
                Assert.That(lease.AlphaTexture, Is.Null);
        }

        [Test]
        public void LeasedSlot_IsNotHandedToTheWriter()
        {
            var ring = NewRing(Dxt1);
            ring.CommitWrite(ring.BeginWrite(), 0);
            Assert.That(ring.TryAcquire(out var lease), Is.True);

            using (lease)
            {
                for (int i = 1; i < 20; i++)
                {
                    int slot = ring.BeginWrite();
                    Assert.That(slot, Is.Not.EqualTo(lease.Slot));
                    ring.CommitWrite(slot, i);
                }
            }
        }

        // ── Zero-copy writes ─────────────────────────────────────────────────

        [Test]
        public void BytesWrittenThroughTheSlotPointer_LandInThatSlotsTexture()
        {
            var ring = NewRing(Dxt1);
            int slot = ring.BeginWrite();

            unsafe
            {
                byte* p = (byte*)ring.GetWritePtr(slot, 0);
                for (int i = 0; i < Dxt1Size; i++) p[i] = (byte)(i & 0xFF);
            }
            ring.CommitWrite(slot, 3);

            Assert.That(ring.TryAcquire(out var lease), Is.True);
            using (lease)
            {
                lease.Apply();
                var raw = lease.ColorTexture.GetRawTextureData<byte>();
                Assert.That(raw[0], Is.EqualTo(0));
                Assert.That(raw[255], Is.EqualTo(255));
                Assert.That(raw[Dxt1Size - 1], Is.EqualTo((byte)((Dxt1Size - 1) & 0xFF)));
            }
        }

        [Test]
        public void BackgroundWriterAndMainThreadReader_KeepFramesConsistent()
        {
            var ring = NewRing(Dxt1);
            bool writerDone = false;
            bool corrupted = false;
            Exception writerException = null;

            var writer = new Thread(() =>
            {
                try
                {
                    for (int frame = 1; frame <= 500; frame++)
                    {
                        int slot = ring.BeginWrite();
                        unsafe
                        {
                            byte* p = (byte*)ring.GetWritePtr(slot, 0);
                            for (int i = 0; i < Dxt1Size; i++) p[i] = (byte)frame;
                        }
                        ring.CommitWrite(slot, frame);
                    }
                }
                catch (Exception ex) { writerException = ex; }
                finally { Volatile.Write(ref writerDone, true); }
            });

            writer.Start();
            while (!Volatile.Read(ref writerDone))
                CheckOnce();
            Assert.That(writer.Join(HapTestFixtures.TimeoutMs), Is.True, "writer timed out");
            CheckOnce();

            Assert.That(writerException, Is.Null, $"writer threw: {writerException}");
            Assert.That(corrupted, Is.False, "a leased slot was decoded into while it was being read");

            void CheckOnce()
            {
                if (!ring.TryAcquire(out var lease)) return;
                using (lease)
                {
                    var raw = lease.ColorTexture.GetRawTextureData<byte>();
                    byte expected = (byte)lease.FrameIndex;
                    for (int i = 0; i < Dxt1Size; i += 128)
                    {
                        if (raw[i] != expected) { corrupted = true; return; }
                    }
                    lease.MarkUploaded();
                }
            }
        }

        // ── Disposal ─────────────────────────────────────────────────────────

        [Test]
        public void Dispose_DestroysEveryTexture()
        {
            var ring = NewRing(YCoCg, Rgtc1);
            var textures = new List<Texture2D>();
            for (int slot = 0; slot < ring.SlotCount; slot++)
                for (int t = 0; t < ring.TextureCount; t++)
                    textures.Add(ring.GetTexture(slot, t));

            ring.Dispose();

            foreach (var tex in textures)
                Assert.That(tex == null, Is.True, "a ring texture outlived the ring");
        }

        [Test]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var ring = NewRing(Dxt1);
            ring.Dispose();
            Assert.DoesNotThrow(() => ring.Dispose());
        }
    }
}
