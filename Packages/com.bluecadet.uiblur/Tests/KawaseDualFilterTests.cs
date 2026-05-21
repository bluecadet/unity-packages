using NUnit.Framework;
using UnityEngine;
using Bluecadet.UIBlur;

namespace Bluecadet.UIBlur.Tests
{
    [TestFixture]
    public class KawaseDualFilterTests
    {
        // -------------------------------------------------------------------
        // Resolution struct tests
        // -------------------------------------------------------------------

        [Test]
        public void Resolution_WholeNumbers_TexelSizeIsReciprocal()
        {
            var res = new KawaseDualFilter.Resolution(100, 200);
            Assert.That(res.XTexelSize, Is.EqualTo(0.01f).Within(0.0001f));
            Assert.That(res.YTexelSize, Is.EqualTo(0.005f).Within(0.0001f));
        }

        [Test]
        public void Resolution_FloatInput_CeilsToInt()
        {
            var res = new KawaseDualFilter.Resolution(1.1f, 1.9f);
            Assert.That(res.Width, Is.EqualTo(2));
            Assert.That(res.Height, Is.EqualTo(2));
        }

        [Test]
        public void Resolution_ExactWhole_NoRounding()
        {
            var res = new KawaseDualFilter.Resolution(256f, 128f);
            Assert.That(res.Width, Is.EqualTo(256));
            Assert.That(res.Height, Is.EqualTo(128));
        }

        [Test]
        public void Resolution_ZeroWidth_TexelSizeIsZero()
        {
            var res = new KawaseDualFilter.Resolution(0, 100);
            Assert.That(res.XTexelSize, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void Resolution_ZeroHeight_TexelSizeIsZero()
        {
            var res = new KawaseDualFilter.Resolution(100, 0);
            Assert.That(res.YTexelSize, Is.EqualTo(0f).Within(0.0001f));
        }

        // -------------------------------------------------------------------
        // ComputeBlurParams tests
        // -------------------------------------------------------------------

        [Test]
        public void ComputeBlurParams_BlurScale2_Uses1Pass()
        {
            KawaseDualFilter.ComputeBlurParams(2.0f, 6, 2.0f, out int actualPasses, out float sampleOffset);
            Assert.That(actualPasses, Is.EqualTo(1));
            Assert.That(sampleOffset, Is.EqualTo(1.0f).Within(0.0001f));
        }

        [Test]
        public void ComputeBlurParams_BlurScale4_Uses1Pass()
        {
            KawaseDualFilter.ComputeBlurParams(4.0f, 6, 2.0f, out int actualPasses, out float sampleOffset);
            Assert.That(actualPasses, Is.EqualTo(1));
            Assert.That(sampleOffset, Is.EqualTo(2.0f).Within(0.0001f));
        }

        [Test]
        public void ComputeBlurParams_JustAbove4_Uses2Passes()
        {
            KawaseDualFilter.ComputeBlurParams(4.1f, 6, 2.0f, out int actualPasses, out float sampleOffset);
            Assert.That(actualPasses, Is.EqualTo(2));
            Assert.That(sampleOffset, Is.EqualTo(4.1f / Mathf.Pow(2f, 2)).Within(0.0001f));
        }

        [Test]
        public void ComputeBlurParams_BlurScale8_Uses2Passes()
        {
            KawaseDualFilter.ComputeBlurParams(8.0f, 6, 2.0f, out int actualPasses, out float sampleOffset);
            Assert.That(actualPasses, Is.EqualTo(2));
            Assert.That(sampleOffset, Is.EqualTo(2.0f).Within(0.0001f));
        }

        [Test]
        public void ComputeBlurParams_BlurScale16_Uses3Passes()
        {
            KawaseDualFilter.ComputeBlurParams(16.0f, 6, 2.0f, out int actualPasses, out float sampleOffset);
            Assert.That(actualPasses, Is.EqualTo(3));
            Assert.That(sampleOffset, Is.EqualTo(2.0f).Within(0.0001f));
        }

        [Test]
        public void ComputeBlurParams_ExceedsMaxPasses_ClampsToMax()
        {
            KawaseDualFilter.ComputeBlurParams(256f, 6, 2.0f, out int actualPasses, out float sampleOffset);
            Assert.That(actualPasses, Is.EqualTo(6));
            Assert.That(sampleOffset, Is.EqualTo(4.0f).Within(0.0001f));
        }

        [Test]
        public void ComputeBlurParams_SampleOffsetIsBlurScaleOverTwoPowerActualPasses()
        {
            // blurScale=12: n=1→6>2; n=2→3>2; n=3→12/8=1.5 ≤ 2 → actualPasses=3, sampleOffset=1.5
            KawaseDualFilter.ComputeBlurParams(12f, 6, 2.0f, out int actualPasses, out float sampleOffset);
            float expected = 12f / Mathf.Pow(2f, actualPasses);
            Assert.That(actualPasses, Is.EqualTo(3));
            Assert.That(sampleOffset, Is.EqualTo(expected).Within(0.0001f));
            Assert.That(sampleOffset, Is.EqualTo(1.5f).Within(0.0001f));
        }

        [Test]
        public void ComputeBlurParams_SampleOffset_AlwaysLeqMaxOffset_WhenPasses_Sufficient()
        {
            float[] blurScales = { 1f, 2f, 4f, 8f, 16f, 32f, 64f };
            const int maxPasses = 6;
            const float maxOffset = 2.0f;

            foreach (float scale in blurScales)
            {
                KawaseDualFilter.ComputeBlurParams(scale, maxPasses, maxOffset, out int actualPasses, out float sampleOffset);
                if (actualPasses < maxPasses)
                {
                    Assert.That(sampleOffset, Is.LessThanOrEqualTo(maxOffset + 0.0001f),
                        $"sampleOffset {sampleOffset} exceeded maxOffset {maxOffset} for blurScale={scale}");
                }
            }
        }
    }
}
