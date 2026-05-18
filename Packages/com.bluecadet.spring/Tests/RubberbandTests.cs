using NUnit.Framework;
using UnityEngine;
using Bluecadet.Spring;

namespace Bluecadet.Spring.Tests
{
    [TestFixture]
    public class RubberbandTests
    {
        [Test]
        public void Apply_WithinBounds_ReturnsUnchanged()
        {
            float result = Rubberband.Apply(0.5f, 0f, 1f);

            Assert.That(result, Is.EqualTo(0.5f).Within(0.0001f),
                "Value within bounds should pass through unchanged.");
        }

        [Test]
        public void Apply_BelowMin_ReturnsDampedValue()
        {
            float result = Rubberband.Apply(-1f, 0f, 1f);

            Assert.That(result, Is.LessThan(0f), "Damped value should still be below min.");
            Assert.That(result, Is.GreaterThan(-1f), "Damped value should be less extreme than raw input.");
        }

        [Test]
        public void Apply_AboveMax_ReturnsDampedValue()
        {
            float result = Rubberband.Apply(2f, 0f, 1f);

            Assert.That(result, Is.GreaterThan(1f), "Damped value should still be above max.");
            Assert.That(result, Is.LessThan(2f), "Damped value should be less extreme than raw input.");
        }

        [Test]
        public void Apply_HigherResistance_MoreDamped()
        {
            float lowResistance  = Rubberband.Apply(-10f, 0f, 1f, 0.1f);
            float highResistance = Rubberband.Apply(-10f, 0f, 1f, 0.9f);

            // Both are below min (negative), but high resistance means closer to 0
            Assert.That(highResistance, Is.GreaterThan(lowResistance),
                "Higher resistance should produce a value closer to the min bound (less overshoot).");
        }

        [Test]
        public void Apply_MaxOvershoot_ClampsResult()
        {
            float result = Rubberband.Apply(-100f, 0f, 1f, 0.55f, 5f);

            Assert.That(result, Is.GreaterThanOrEqualTo(-5f),
                "Result should be clamped to maxOvershoot distance from min bound.");
        }

        [Test]
        public void IsOutOfBounds_DetectsCorrectly()
        {
            Assert.IsFalse(Rubberband.IsOutOfBounds(0.5f, 0f, 1f), "0.5 should be within [0,1].");
            Assert.IsFalse(Rubberband.IsOutOfBounds(0f, 0f, 1f),   "0 (min boundary) should be within [0,1].");
            Assert.IsFalse(Rubberband.IsOutOfBounds(1f, 0f, 1f),   "1 (max boundary) should be within [0,1].");
            Assert.IsTrue(Rubberband.IsOutOfBounds(-0.1f, 0f, 1f), "-0.1 should be out of bounds.");
            Assert.IsTrue(Rubberband.IsOutOfBounds(1.1f, 0f, 1f),  "1.1 should be out of bounds.");
        }

        [Test]
        public void GetOvershoot_ReturnsDistance()
        {
            float overshoot = Rubberband.GetOvershoot(3f, 0f, 2f);
            Assert.That(overshoot, Is.EqualTo(1f).Within(0.0001f),
                "Overshoot past max=2 when value=3 should be 1.");
        }

        [Test]
        public void GetOvershoot_WithinBounds_ReturnsZero()
        {
            float overshoot = Rubberband.GetOvershoot(1f, 0f, 2f);
            Assert.That(overshoot, Is.EqualTo(0f).Within(0.0001f),
                "Overshoot should be 0 when value is within bounds.");
        }

        [Test]
        public void DampenVelocity_MovingOutward_Dampens()
        {
            // value=-1 is below min=0, velocity=-1 is moving further out (away from bounds)
            float result = Rubberband.DampenVelocity(-1f, -1f, 0f, 1f);

            Assert.That(result, Is.GreaterThan(-1f),
                "Velocity moving outward past bounds should be dampened (less negative).");
            Assert.That(result, Is.LessThan(0f),
                "Dampened outward velocity should still be in the same direction.");
        }

        [Test]
        public void DampenVelocity_MovingInward_Unchanged()
        {
            // value=-1 is below min=0, velocity=1 is positive (moving back toward/past bounds)
            float result = Rubberband.DampenVelocity(1f, -1f, 0f, 1f);

            Assert.That(result, Is.EqualTo(1f).Within(0.0001f),
                "Velocity moving inward (toward bounds) should not be dampened.");
        }
    }
}
