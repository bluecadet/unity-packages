using NUnit.Framework;
using Bluecadet.Spring;

namespace Bluecadet.Spring.Tests
{
    [TestFixture]
    public class SpringPhysicsTests
    {
        [SetUp]
        public void SetUp() => Spring.KillAll();

        [TearDown]
        public void TearDown() => Spring.KillAll();

        [Test]
        public void Underdamped_OscillatesBeforeConverging()
        {
            // damping=5, stiffness=200 -> zeta ~ 5/(2*sqrt(200*1)) ~ 0.177 (underdamped)
            var spring = Spring.Create(0f)
                .WithDamping(5f)
                .WithStiffness(200f);
            spring.To(1f);

            bool overshot = false;
            float dt = 0.016f;
            for (int i = 0; i < 200; i++)
            {
                spring.Advance(dt);
                if (spring.Value > 1.0f)
                    overshot = true;
            }

            Assert.IsTrue(overshot, "Underdamped spring should overshoot target.");
            Assert.IsTrue(spring.IsFinished, "Spring should eventually come to rest.");
        }

        [Test]
        public void CriticallyDamped_ConvergesWithoutOvershoot()
        {
            // Default params: damping=26, stiffness=170 -> zeta ~ 1 (critically damped)
            var spring = Spring.Create(0f);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 500; i++)
            {
                spring.Advance(dt);
                Assert.That(spring.Value, Is.LessThanOrEqualTo(1.01f),
                    $"Critically damped spring exceeded target+0.01 at step {i}.");
            }

            Assert.IsTrue(spring.IsFinished, "Critically damped spring should come to rest.");
        }

        [Test]
        public void Overdamped_ConvergesSlowlyWithoutOvershoot()
        {
            // damping=60, stiffness=100 -> zeta ~ 60/(2*sqrt(100*1)) ~ 3 (overdamped)
            var spring = Spring.Create(0f)
                .WithDamping(60f)
                .WithStiffness(100f);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 2000; i++)
            {
                spring.Advance(dt);
                Assert.That(spring.Value, Is.LessThanOrEqualTo(1.01f),
                    $"Overdamped spring exceeded target+0.01 at step {i}.");
            }

            Assert.IsTrue(spring.IsFinished, "Overdamped spring should come to rest.");
        }

        [Test]
        public void DecayReducesVelocityExponentially()
        {
            var decay = Spring.CreateDecay(0f);
            decay.Play(100f);

            float dt = 0.016f;
            float prevVelocity = decay.Velocity;

            for (int i = 0; i < 10; i++)
            {
                decay.Advance(dt);
                Assert.That(decay.Velocity, Is.LessThan(prevVelocity),
                    $"Velocity should decrease each step (step {i}).");
                prevVelocity = decay.Velocity;
            }

            // Run until finished
            for (int i = 0; i < 10000; i++)
            {
                if (decay.IsFinished) break;
                decay.Advance(dt);
            }

            Assert.IsTrue(decay.IsFinished, "Decay should eventually come to rest.");
        }

        [Test]
        public void DecayWithHighFriction_StopsFaster()
        {
            float initialVelocity = 10f;

            var lowFriction = Spring.CreateDecay(0f).WithFriction(1f);
            lowFriction.Play(initialVelocity);

            var highFriction = Spring.CreateDecay(0f).WithFriction(20f);
            highFriction.Play(initialVelocity);

            float dt = 0.016f;
            int lowFrictionSteps = -1;
            int highFrictionSteps = -1;

            for (int i = 0; i < 100000; i++)
            {
                if (!lowFriction.IsFinished) lowFriction.Advance(dt);
                if (!highFriction.IsFinished) highFriction.Advance(dt);

                if (highFrictionSteps < 0 && highFriction.IsFinished)
                    highFrictionSteps = i;
                if (lowFrictionSteps < 0 && lowFriction.IsFinished)
                    lowFrictionSteps = i;

                if (lowFrictionSteps >= 0 && highFrictionSteps >= 0)
                    break;
            }

            Assert.That(highFrictionSteps, Is.GreaterThanOrEqualTo(0), "High friction decay should finish.");
            Assert.That(lowFrictionSteps, Is.GreaterThanOrEqualTo(0), "Low friction decay should finish.");
            Assert.That(highFrictionSteps, Is.LessThan(lowFrictionSteps),
                "High friction decay should finish in fewer steps than low friction decay.");
        }

        [Test]
        public void SpringWithPrecision_RespectsPrecisionThreshold()
        {
            float precision = 0.001f;
            var spring = Spring.Create(0f).WithPrecision(precision);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 10000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should come to rest.");
            Assert.That(spring.Value, Is.EqualTo(1f).Within(precision),
                "Final value should be within precision threshold of target.");
        }

        [Test]
        public void RetargetMidFlight_PreservesVelocity()
        {
            var spring = Spring.Create(0f);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 30; i++)
                spring.Advance(dt);

            float velocityBeforeRetarget = spring.Velocity;

            spring.To(2f);

            Assert.That(spring.Velocity, Is.Not.EqualTo(0f).Within(0.0001f),
                "Velocity should be preserved (non-zero) after retargeting mid-flight.");
            Assert.That(spring.Velocity, Is.EqualTo(velocityBeforeRetarget).Within(0.0001f),
                "Velocity value should be identical after retargeting.");
        }
    }
}
