using NUnit.Framework;
using Bluecadet.Spring;
using UnityEngine;

namespace Bluecadet.Spring.Tests
{
    [TestFixture]
    public class SpringEdgeCaseTests
    {
        [SetUp]
        public void SetUp() => Spring.KillAll();

        [TearDown]
        public void TearDown() => Spring.KillAll();

        [Test]
        public void ZeroDeltaTime_SpringDoesNotAdvance()
        {
            var spring = Spring.Create(0f);
            spring.To(1f);

            spring.Advance(0f);

            Assert.That(spring.Value, Is.EqualTo(0f).Within(0.0001f),
                "Spring should not advance with a zero delta time.");
        }

        [Test]
        public void SpringAlreadyAtTarget_ConvergesImmediately()
        {
            var spring = Spring.Create(1f);
            spring.To(1f);

            // To() sets IsFinished=false unconditionally, but one advance with
            // value==target and velocity==0 immediately triggers the rest check.
            spring.Advance(0.016f);

            Assert.IsTrue(spring.IsFinished,
                "Spring already at target should finish within one step.");
        }

        [Test]
        public void NegativeTarget_ConvergesToNegative()
        {
            var spring = Spring.Create(0f);
            spring.To(-5f);

            float dt = 0.016f;
            for (int i = 0; i < 1000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should converge to a negative target.");
            Assert.That(spring.Value, Is.EqualTo(-5f).Within(0.01f),
                "Spring should converge to -5f.");
        }

        [Test]
        public void LargeTarget_ConvergesCorrectly()
        {
            var spring = Spring.Create(0f);
            spring.To(1000f);

            float dt = 0.016f;
            for (int i = 0; i < 2000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should converge to a large target.");
        }

        [Test]
        public void VeryLargeDeltaTime_DoesNotCrash()
        {
            var spring = Spring.Create(0f);
            spring.To(1f);

            Assert.DoesNotThrow(() => spring.Advance(1000f),
                "Advancing with a very large delta time should not throw.");
            Assert.That(spring.Value, Is.EqualTo(1f).Within(0.1f),
                "Spring should be close to target after a very large delta time step.");
        }

        [Test]
        public void GoldenValue_CriticallyDamped_MatchesAnalytic()
        {
            // stiffness=100, damping=20, mass=1 -> zeta = 20 / (2 * sqrt(100 * 1)) = 1.0 exactly
            var spring = Spring.Create(0f)
                .WithStiffness(100f)
                .WithDamping(20f)
                .WithMass(1f)
                .WithPrecision(0.0001f);
            spring.To(1f);

            spring.Advance(0.1f);

            // Critically damped analytic solution after dt=0.1:
            // omega0 = sqrt(k/m) = 10
            // x(t)  = 1 - (1 + omega0*t) * e^(-omega0*t) = 1 - 2*e^{-1} ≈ 0.26424
            // v(t)  = omega0^2 * t * e^(-omega0*t) = 10 * e^{-1} ≈ 3.6788
            Assert.That(spring.Value, Is.EqualTo(0.26424f).Within(0.0005f),
                "Critically damped spring value after dt=0.1 should match analytic solution.");
            Assert.That(spring.Velocity, Is.EqualTo(3.6788f).Within(0.001f),
                "Critically damped spring velocity after dt=0.1 should match analytic solution.");
            Assert.IsFalse(spring.IsFinished,
                "Spring should not be finished after one step (value is far from target).");
        }

        [Test]
        public void UnderdampedGoldenValue_AfterOneStep()
        {
            // stiffness=200, damping=5 -> zeta ~ 5 / (2 * sqrt(200 * 1)) ~ 0.177 (underdamped)
            var spring = Spring.Create(0f)
                .WithStiffness(200f)
                .WithDamping(5f)
                .WithMass(1f);
            spring.To(1f);

            spring.Advance(0.016f);

            Assert.That(spring.Value, Is.InRange(0f, 0.05f),
                "Underdamped spring should have moved slightly toward target after one step.");
            Assert.That(spring.Velocity, Is.GreaterThan(0f),
                "Underdamped spring velocity should be positive (moving toward target) after one step.");
        }

        [Test]
        public void OverdampedGoldenValue_NeverOvershoots()
        {
            // stiffness=100, damping=60 -> zeta ~ 60 / (2 * sqrt(100 * 1)) ~ 3 (overdamped)
            var spring = Spring.Create(0f)
                .WithStiffness(100f)
                .WithDamping(60f)
                .WithMass(1f);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 5000; i++)
            {
                spring.Advance(dt);
                Assert.That(spring.Value, Is.LessThanOrEqualTo(1.001f),
                    $"Overdamped spring should never overshoot target (step {i}).");
                if (spring.IsFinished) break;
            }

            Assert.IsTrue(spring.IsFinished, "Overdamped spring should eventually converge.");
        }

        [Test]
        public void Vector2Spring_BothAxesConverge()
        {
            var target = new Vector2(3f, -2f);
            var spring = Spring.Create(Vector2.zero);
            spring.To(target);

            float dt = 0.016f;
            for (int i = 0; i < 1000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Vector2 spring should converge.");
            Assert.That(spring.Value.x, Is.EqualTo(3f).Within(0.01f), "X axis should converge to 3.");
            Assert.That(spring.Value.y, Is.EqualTo(-2f).Within(0.01f), "Y axis should converge to -2.");
        }

        [Test]
        public void Vector3Spring_AllAxesConverge()
        {
            var target = new Vector3(1f, -1f, 2f);
            var spring = Spring.Create(Vector3.zero);
            spring.To(target);

            float dt = 0.016f;
            for (int i = 0; i < 1000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Vector3 spring should converge.");
            Assert.That(spring.Value.x, Is.EqualTo(1f).Within(0.01f),  "X axis should converge to 1.");
            Assert.That(spring.Value.y, Is.EqualTo(-1f).Within(0.01f), "Y axis should converge to -1.");
            Assert.That(spring.Value.z, Is.EqualTo(2f).Within(0.01f),  "Z axis should converge to 2.");
        }

        [Test]
        public void DecayNegativeVelocity_DecaysToZero()
        {
            var decay = Spring.CreateDecay(0f);
            decay.Play(-20f);

            float dt = 0.016f;
            for (int i = 0; i < 5000; i++)
            {
                if (decay.IsFinished) break;
                decay.Advance(dt);
            }

            Assert.That(decay.Value, Is.LessThan(0f),
                "Decay with negative velocity should have moved in the negative direction.");
            Assert.IsTrue(decay.IsFinished, "Decay should eventually come to rest.");
            Assert.That(decay.Velocity, Is.EqualTo(0f).Within(0.0001f),
                "Velocity should be zero once finished.");
        }

        [Test]
        public void DecayZeroVelocity_DoesNotStart()
        {
            var decay = Spring.CreateDecay(0f);
            decay.Play(0f);

            Assert.IsTrue(decay.IsFinished,
                "Decay with zero velocity should remain finished (never start).");
        }

        [Test]
        public void DecayAddVelocity_AccumulatesCorrectly()
        {
            var decay = Spring.CreateDecay(0f);
            decay.Play(5f);
            decay.AddVelocity(5f);

            Assert.That(decay.Velocity, Is.GreaterThan(5f),
                "AddVelocity should accumulate on top of the existing velocity.");
            Assert.IsFalse(decay.IsFinished, "Decay should still be running after adding velocity.");
        }
    }
}
