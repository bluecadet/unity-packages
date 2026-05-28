using NUnit.Framework;
using UnityEngine;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class HapTextureUploaderTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // Format mapping
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Constructor_DXT1_CreatesNonNullTexture()
        {
            var uploader = new HapTextureUploader(64, 64, HapFormat.DXT1);
            Assert.That(uploader.Texture, Is.Not.Null);
            uploader.Dispose();
        }

        [Test]
        public void Constructor_DXT5_CreatesNonNullTexture()
        {
            var uploader = new HapTextureUploader(64, 64, HapFormat.DXT5);
            Assert.That(uploader.Texture, Is.Not.Null);
            uploader.Dispose();
        }

        [Test]
        public void Constructor_BC7_CreatesNonNullTexture()
        {
            var uploader = new HapTextureUploader(64, 64, HapFormat.BC7);
            Assert.That(uploader.Texture, Is.Not.Null);
            uploader.Dispose();
        }

        [Test]
        public void Constructor_YCoCgDXT5_CreatesNonNullTexture()
        {
            var uploader = new HapTextureUploader(64, 64, HapFormat.YCoCgDXT5);
            Assert.That(uploader.Texture, Is.Not.Null);
            uploader.Dispose();
        }

        [Test]
        public void Constructor_UnknownFormat_FallsBackAndCreatesTexture()
        {
            var uploader = new HapTextureUploader(64, 64, (HapFormat)(-1));
            Assert.That(uploader.Texture, Is.Not.Null);
            uploader.Dispose();
        }

        [Test]
        public void Constructor_SetsCorrectDimensions()
        {
            var uploader = new HapTextureUploader(128, 256, HapFormat.DXT1);
            Assert.That(uploader.Texture.width,  Is.EqualTo(128));
            Assert.That(uploader.Texture.height, Is.EqualTo(256));
            uploader.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Multi-instance construction
        // Validates the N-uploader allocation pattern introduced in Open()
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void MultipleUploaders_SameFormat_AllHaveNonNullTextures()
        {
            const int count = 3; // matches default uploaderCount (maxQueuedFrames=2 → 3)
            var uploaders = new HapTextureUploader[count];
            for (int i = 0; i < count; i++)
                uploaders[i] = new HapTextureUploader(64, 64, HapFormat.DXT1);

            foreach (var u in uploaders)
                Assert.That(u.Texture, Is.Not.Null);

            foreach (var u in uploaders)
                u.Dispose();
        }

        [Test]
        public void MultipleUploaders_SameFormat_HaveDistinctTextureInstances()
        {
            const int count = 3;
            var uploaders = new HapTextureUploader[count];
            for (int i = 0; i < count; i++)
                uploaders[i] = new HapTextureUploader(64, 64, HapFormat.DXT1);

            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    Assert.That(uploaders[i].Texture, Is.Not.SameAs(uploaders[j].Texture),
                        $"uploaders[{i}] and uploaders[{j}] share the same Texture2D instance");

            foreach (var u in uploaders)
                u.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Disposal safety
        // Validates the foreach-dispose loop in Close()
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Dispose_CalledOnce_DoesNotThrow()
        {
            var uploader = new HapTextureUploader(64, 64, HapFormat.DXT1);
            Assert.DoesNotThrow(() => uploader.Dispose());
        }

        [Test]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var uploader = new HapTextureUploader(64, 64, HapFormat.DXT1);
            uploader.Dispose();
            Assert.DoesNotThrow(() => uploader.Dispose());
        }

        [Test]
        public void Dispose_NullsTexture()
        {
            var uploader = new HapTextureUploader(64, 64, HapFormat.DXT1);
            uploader.Dispose();
            Assert.That(uploader.Texture, Is.Null);
        }

        [Test]
        public void MultipleUploaders_DisposeAll_DoesNotThrow()
        {
            const int count = 3;
            var uploaders = new HapTextureUploader[count];
            for (int i = 0; i < count; i++)
                uploaders[i] = new HapTextureUploader(64, 64, HapFormat.DXT1);

            Assert.DoesNotThrow(() =>
            {
                foreach (var u in uploaders) u?.Dispose();
            });
        }
    }
}
