using System;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Empirical verification of the assumption the zero-copy upload path is built on:
    /// a readable <see cref="Texture2D"/>'s raw data pointer stays put across
    /// <see cref="Texture2D.Apply"/> calls, and bytes written through that cached pointer
    /// are what the GPU samples after the next Apply.
    ///
    /// If these tests ever fail, the decode thread must go back to writing into its own
    /// buffers and the main thread must copy with LoadRawTextureData.
    /// </summary>
    [TestFixture]
    public class TextureZeroCopyTests
    {
        const int Width = 64;
        const int Height = 64;

        /// <summary>64x64 DXT1 = 16x16 blocks of 8 bytes.</summary>
        const int Dxt1Size = 2048;

        const ushort Red565 = 0xF800;
        const ushort Green565 = 0x07E0;

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

        [Test]
        public void GetRawTextureData_Pointer_IsStableAcrossApply()
        {
            var tex = new Texture2D(Width, Height, TextureFormat.DXT1, false);
            try
            {
                IntPtr first = RawPtr(tex);
                Assert.That(first, Is.Not.EqualTo(IntPtr.Zero));

                for (int i = 0; i < 5; i++)
                {
                    FillSolidDxt1(first, Dxt1Size, i % 2 == 0 ? Red565 : Green565);
                    tex.Apply(false, false);
                    Assert.That(RawPtr(tex), Is.EqualTo(first),
                        $"raw data pointer moved after Apply #{i + 1}");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void GetRawTextureData_Length_MatchesCompressedSize()
        {
            var tex = new Texture2D(Width, Height, TextureFormat.DXT1, false);
            try
            {
                Assert.That(tex.GetRawTextureData<byte>().Length, Is.EqualTo(Dxt1Size));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void WritesThroughCachedPointer_AreVisibleAfterApply()
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "needs a graphics device");

            var tex = new Texture2D(Width, Height, TextureFormat.DXT1, false);
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

        [Test]
        public void WritesFromBackgroundThread_AreVisibleAfterApply()
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                "needs a graphics device");

            var tex = new Texture2D(Width, Height, TextureFormat.DXT1, false);
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
