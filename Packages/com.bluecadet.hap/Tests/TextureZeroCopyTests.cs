using System;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Empirical verification of the assumption the zero-copy upload path is built on:
    /// a raw data pointer read once, before the first <see cref="Texture2D.Apply"/>, keeps
    /// naming the buffer the GPU samples for the life of the texture — so the decode thread
    /// can write straight into it and the main thread only has to Apply.
    ///
    /// What does *not* hold is pointer identity across a re-query: asking a texture for its
    /// raw data again after an Apply hands back a different buffer and retires the old one
    /// (see <see cref="Requery_AfterApply_RetiresTheCachedPointer"/>). That is why
    /// HapTextureRing reads each texture's pointer exactly once, at construction.
    ///
    /// If these tests ever fail, the decode thread must go back to writing into its own
    /// buffers and the main thread must copy with LoadRawTextureData.
    /// </summary>
    [TestFixture]
    public class TextureZeroCopyTests
    {
        const int Width = 64;
        const int Height = 64;

        /// <summary>Raw size of a 64x64 DXT1 texture, the same arithmetic the plugin uses.</summary>
        static readonly int Dxt1Size = HapTestFixtures.BlockBytes(HapFormat.DXT1, Width, Height);

        const ushort Red565 = 0xF800;
        const ushort Green565 = 0x07E0;
        const ushort Blue565 = 0x001F;

        static unsafe IntPtr RawPtr(Texture2D t) => (IntPtr)t.GetRawTextureData<byte>().GetUnsafePtr();

        /// <summary>
        /// Fill a DXT1 buffer with blocks that decode to a single flat colour:
        /// both endpoints equal <paramref name="color565"/> and all indices 0.
        /// </summary>
        static unsafe void FillSolidDxt1(IntPtr buffer, int size, ushort color565)
        {
            byte* p = (byte*)buffer;
            for (int block = 0; block < size; block += 8)
            {
                p[block + 0] = (byte)(color565 & 0xFF);
                p[block + 1] = (byte)(color565 >> 8);
                p[block + 2] = (byte)(color565 & 0xFF);
                p[block + 3] = (byte)(color565 >> 8);
                p[block + 4] = 0;
                p[block + 5] = 0;
                p[block + 6] = 0;
                p[block + 7] = 0;
            }
        }

        static Color ReadBackCenterPixel(Texture2D source)
        {
            var rt = RenderTexture.GetTemporary(Width, Height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            var readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                readback.Apply();
                return readback.GetPixel(Width / 2, Height / 2);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        /// <summary>
        /// Both construction paths must hold: the default zero-filled texture (what every
        /// non-HAP caller still gets), and the uninitialized one HapTextureRing now requests
        /// on its own open path.
        /// </summary>
        static Texture2D MakeTexture(bool createUninitialized) =>
            new(Width, Height, TextureFormat.DXT1, mipChain: false, linear: false,
                createUninitialized: createUninitialized);

        /// <summary>
        /// The ring's exact pattern: read the pointer once at construction, then write through
        /// it for every frame. Each Apply must upload what was just written through that one
        /// cached pointer, however many frames go by.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void CachedPointer_StaysAuthoritative_AcrossApplies(bool createUninitialized)
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "needs a graphics device");

            var tex = MakeTexture(createUninitialized);
            try
            {
                IntPtr cached = RawPtr(tex);
                Assert.That(cached, Is.Not.EqualTo(IntPtr.Zero));

                for (int i = 0; i < 6; i++)
                {
                    bool wantRed = i % 2 == 0;
                    FillSolidDxt1(cached, Dxt1Size, wantRed ? Red565 : Green565);
                    tex.Apply(false, false);

                    Color got = ReadBackCenterPixel(tex);
                    Assert.That(wantRed ? got.r : got.g, Is.GreaterThan(0.5f),
                        $"frame {i}: writes through the cached pointer stopped reaching the GPU");
                    Assert.That(wantRed ? got.g : got.r, Is.LessThan(0.5f),
                        $"frame {i}: wrong colour on screen");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// The trap this design avoids: re-reading a texture's raw data after an Apply can hand
        /// back a different buffer, and it is that newest buffer Apply uploads — writes through
        /// the previously cached pointer are silently dropped. Anything caching a pointer must
        /// therefore never ask the texture for its raw data again.
        /// </summary>
        [Test]
        public void Requery_AfterApply_RetiresTheCachedPointer()
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "needs a graphics device");

            var tex = MakeTexture(true);
            try
            {
                IntPtr cached = RawPtr(tex);
                FillSolidDxt1(cached, Dxt1Size, Red565);
                tex.Apply(false, false);

                // Nothing samples the texture in between, which is when Unity re-materialises it.
                IntPtr requeried = RawPtr(tex);

                FillSolidDxt1(cached, Dxt1Size, Green565);
                if (requeried != cached)
                    FillSolidDxt1(requeried, Dxt1Size, Blue565);

                tex.Apply(false, false);
                Color got = ReadBackCenterPixel(tex);

                if (requeried != cached)
                    Assert.That(got.b, Is.GreaterThan(0.5f),
                        "expected the re-queried buffer to win; if the cached one still wins, " +
                        "the pointer moved but stayed live and this test's premise is wrong");
                else
                    Assert.That(got.g, Is.GreaterThan(0.5f),
                        "pointer did not move, so the cached buffer must still be the live one");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GetRawTextureData_Length_MatchesCompressedSize(bool createUninitialized)
        {
            var tex = MakeTexture(createUninitialized);
            try
            {
                Assert.That(tex.GetRawTextureData<byte>().Length, Is.EqualTo(Dxt1Size));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WritesThroughCachedPointer_AreVisibleAfterApply(bool createUninitialized)
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "needs a graphics device");

            var tex = MakeTexture(createUninitialized);
            try
            {
                IntPtr cached = RawPtr(tex);

                FillSolidDxt1(cached, Dxt1Size, Red565);
                tex.Apply(false, false);
                Color red = ReadBackCenterPixel(tex);
                Assert.That(red.r, Is.GreaterThan(0.5f), "expected a red frame after the first Apply");
                Assert.That(red.g, Is.LessThan(0.5f));

                // Same cached pointer, new content: the GPU must see the update.
                FillSolidDxt1(cached, Dxt1Size, Green565);
                tex.Apply(false, false);
                Color green = ReadBackCenterPixel(tex);
                Assert.That(green.g, Is.GreaterThan(0.5f), "expected a green frame after the second Apply");
                Assert.That(green.r, Is.LessThan(0.5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WritesFromBackgroundThread_AreVisibleAfterApply(bool createUninitialized)
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "needs a graphics device");

            var tex = MakeTexture(createUninitialized);
            try
            {
                IntPtr cached = RawPtr(tex);

                // The decode thread only ever touches the raw pointer, never a Unity API.
                var worker = new System.Threading.Thread(() => FillSolidDxt1(cached, Dxt1Size, Green565));
                worker.Start();
                worker.Join();

                tex.Apply(false, false);
                Color green = ReadBackCenterPixel(tex);
                Assert.That(green.g, Is.GreaterThan(0.5f));
                Assert.That(green.r, Is.LessThan(0.5f));
                Assert.That(RawPtr(tex), Is.EqualTo(cached));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
