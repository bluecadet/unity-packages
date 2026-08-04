using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Every Hap variant the plugin decodes, in both single-chunk and chunked encodings:
    /// the format and buffer size each one reports, and that all of its frames decode.
    /// </summary>
    [TestFixture]
    public class HapNativeMultiFormatTests
    {
        readonly List<IntPtr> _handles = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var h in _handles)
                if (h != IntPtr.Zero) HapNative.hap_close(h);
            _handles.Clear();
        }

        IntPtr Open(string path)
        {
            HapTestFixtures.Require(path);
            var error = HapNative.Open(path, out IntPtr handle);
            Assert.That(error, Is.EqualTo(HapError.Ok), $"failed to open {Path.GetFileName(path)}");
            _handles.Add(handle);
            return handle;
        }

        static IEnumerable<TestCaseData> SingleTextureVariants()
        {
            yield return new TestCaseData(HapTestFixtures.Hap1, (int)HapFormat.DXT1, HapTestFixtures.Dxt1Size).SetName("Hap");
            yield return new TestCaseData(HapTestFixtures.Hap1Chunked, (int)HapFormat.DXT1, HapTestFixtures.Dxt1Size).SetName("Hap_chunked");
            yield return new TestCaseData(HapTestFixtures.Hap1Audio, (int)HapFormat.DXT1, HapTestFixtures.Dxt1Size).SetName("Hap_with_audio");
            yield return new TestCaseData(HapTestFixtures.Hap5, (int)HapFormat.DXT5, HapTestFixtures.Dxt5Size).SetName("HapAlpha");
            yield return new TestCaseData(HapTestFixtures.Hap5Chunked, (int)HapFormat.DXT5, HapTestFixtures.Dxt5Size).SetName("HapAlpha_chunked");
            yield return new TestCaseData(HapTestFixtures.HapY, (int)HapFormat.YCoCgDXT5, HapTestFixtures.Dxt5Size).SetName("HapQ");
            yield return new TestCaseData(HapTestFixtures.HapYChunked, (int)HapFormat.YCoCgDXT5, HapTestFixtures.Dxt5Size).SetName("HapQ_chunked");
            yield return new TestCaseData(HapTestFixtures.Hap7, (int)HapFormat.BC7, HapTestFixtures.Dxt5Size).SetName("HapR");
        }

        [TestCaseSource(nameof(SingleTextureVariants))]
        public void Variant_ReportsOneTextureWithExpectedLayout(string path, int formatCode, int bufferSize)
        {
            IntPtr h = Open(path);
            Assert.That(HapNative.hap_get_texture_count(h), Is.EqualTo(1));
            Assert.That(HapNative.hap_get_texture_format(h, 0), Is.EqualTo(formatCode));
            Assert.That(HapNative.hap_get_texture_buffer_size(h, 0), Is.EqualTo(bufferSize));
            Assert.That(HapNative.hap_get_width(h), Is.EqualTo(HapTestFixtures.Width));
            Assert.That(HapNative.hap_get_height(h), Is.EqualTo(HapTestFixtures.Height));
        }

        [TestCaseSource(nameof(SingleTextureVariants))]
        public void Variant_DecodesEveryFrame(string path, int formatCode, int bufferSize)
        {
            IntPtr h = Open(path);
            int frameCount = HapNative.hap_get_frame_count(h);
            Assert.That(frameCount, Is.GreaterThan(0));

            using var buffer = HapTestFixtures.NativeBuffer(bufferSize);
            for (int i = 0; i < frameCount; i++)
                Assert.That(HapNative.DecodeTexture(h, i, 0, buffer.Ptr, buffer.Size),
                    Is.EqualTo(HapError.Ok), $"frame {i} of {Path.GetFileName(path)}");
        }

        // ── Hap Q Alpha (two textures per frame) ─────────────────────────────

        [Test]
        public void HapQAlpha_ReportsTwoTextures()
        {
            IntPtr h = Open(HapTestFixtures.HapM);
            Assert.That(HapNative.hap_get_texture_count(h), Is.EqualTo(2));
        }

        [Test]
        public void HapQAlpha_TextureLayouts_AreColorThenAlpha()
        {
            IntPtr h = Open(HapTestFixtures.HapM);

            Assert.That(HapNative.hap_get_texture_format(h, 0), Is.EqualTo((int)HapFormat.YCoCgDXT5));
            Assert.That(HapNative.hap_get_texture_format(h, 1), Is.EqualTo((int)HapFormat.RGTC1));

            // The alpha texture is half the size of the colour texture: 8 bytes per block vs 16.
            Assert.That(HapNative.hap_get_texture_buffer_size(h, 0), Is.EqualTo(HapTestFixtures.Dxt5Size));
            Assert.That(HapNative.hap_get_texture_buffer_size(h, 1), Is.EqualTo(HapTestFixtures.Dxt1Size));
        }

        [Test]
        public void HapQAlpha_OutOfRangeTextureIndex_ReportsNothing()
        {
            IntPtr h = Open(HapTestFixtures.HapM);
            Assert.That(HapNative.hap_get_texture_format(h, 2), Is.EqualTo(0));
            Assert.That(HapNative.hap_get_texture_buffer_size(h, 2), Is.EqualTo(0));
            Assert.That(HapNative.hap_get_texture_format(h, -1), Is.EqualTo(0));
        }

        [Test]
        public void HapQAlpha_BothTexturesDecode_WithDistinctContent()
        {
            IntPtr h = Open(HapTestFixtures.HapM);
            int colorSize = HapNative.hap_get_texture_buffer_size(h, 0);
            int alphaSize = HapNative.hap_get_texture_buffer_size(h, 1);

            byte[] color = HapTestFixtures.DecodeToBytes(h, 0, 0, colorSize);
            byte[] alpha = HapTestFixtures.DecodeToBytes(h, 0, 1, alphaSize);

            // An aliased-buffer regression (both textures decoded from the same section)
            // would show up as the alpha texture repeating the colour texture's bytes.
            bool identicalPrefix = true;
            for (int i = 0; i < alphaSize; i++)
            {
                if (color[i] != alpha[i]) { identicalPrefix = false; break; }
            }
            Assert.That(identicalPrefix, Is.False, "colour and alpha textures decoded to the same bytes");
        }

        [Test]
        public void HapQAlpha_BothTextures_MatchGoldenFrameZero()
        {
            HapTestFixtures.Require(HapTestFixtures.HapMGoldenTex0);
            HapTestFixtures.Require(HapTestFixtures.HapMGoldenTex1);
            IntPtr h = Open(HapTestFixtures.HapM);

            byte[] golden0 = File.ReadAllBytes(HapTestFixtures.HapMGoldenTex0);
            byte[] golden1 = File.ReadAllBytes(HapTestFixtures.HapMGoldenTex1);

            Assert.That(HapTestFixtures.DecodeToBytes(h, 0, 0, golden0.Length), Is.EqualTo(golden0), "colour texture");
            Assert.That(HapTestFixtures.DecodeToBytes(h, 0, 1, golden1.Length), Is.EqualTo(golden1), "alpha texture");
        }

        [Test]
        public void HapQAlpha_DecodesEveryFrame_BothTextures()
        {
            IntPtr h = Open(HapTestFixtures.HapM);
            int frameCount = HapNative.hap_get_frame_count(h);
            int colorSize = HapNative.hap_get_texture_buffer_size(h, 0);
            int alphaSize = HapNative.hap_get_texture_buffer_size(h, 1);

            using var color = HapTestFixtures.NativeBuffer(colorSize);
            using var alpha = HapTestFixtures.NativeBuffer(alphaSize);
            for (int i = 0; i < frameCount; i++)
            {
                Assert.That(HapNative.DecodeTexture(h, i, 0, color.Ptr, color.Size), Is.EqualTo(HapError.Ok),
                    $"colour texture of frame {i}");
                Assert.That(HapNative.DecodeTexture(h, i, 1, alpha.Ptr, alpha.Size), Is.EqualTo(HapError.Ok),
                    $"alpha texture of frame {i}");
            }
        }

        // ── Cross-format ─────────────────────────────────────────────────────

        [Test]
        public void SingleChunkAndChunked_DecodeToTheSameBytes()
        {
            IntPtr plain = Open(HapTestFixtures.Hap1);
            IntPtr chunked = Open(HapTestFixtures.Hap1Chunked);

            int size = HapNative.hap_get_texture_buffer_size(plain, 0);
            Assert.That(HapNative.hap_get_texture_buffer_size(chunked, 0), Is.EqualTo(size));
            Assert.That(HapTestFixtures.DecodeToBytes(chunked, 0, 0, size),
                Is.EqualTo(HapTestFixtures.DecodeToBytes(plain, 0, 0, size)));
        }

        [Test]
        public void FormatCodes_AreDistinctPerVariant()
        {
            int dxt1 = HapNative.hap_get_texture_format(Open(HapTestFixtures.Hap1), 0);
            int dxt5 = HapNative.hap_get_texture_format(Open(HapTestFixtures.Hap5), 0);
            int ycocg = HapNative.hap_get_texture_format(Open(HapTestFixtures.HapY), 0);
            int bc7 = HapNative.hap_get_texture_format(Open(HapTestFixtures.Hap7), 0);
            int rgtc1 = HapNative.hap_get_texture_format(Open(HapTestFixtures.HapM), 1);

            Assert.That(new[] { dxt1, dxt5, ycocg, bc7, rgtc1 }, Is.Unique);
        }
    }
}
