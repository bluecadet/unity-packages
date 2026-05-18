using NUnit.Framework;
using Bluecadet.Spring;

namespace Bluecadet.Spring.Tests
{
    [TestFixture]
    public class SpringAdversarialTests
    {
        [SetUp]
        public void SetUp() => Spring.KillAll();

        [TearDown]
        public void TearDown() => Spring.KillAll();

        [Test]
        public void RapidRetargets_SpringStillConverges()
        {
            var spring = Spring.Create(0f);

            for (int i = 1; i <= 20; i++)
                spring.To(i * 0.1f);

            float dt = 0.016f;
            for (int i = 0; i < 2000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should converge after rapid retargets.");
            Assert.That(spring.Value, Is.EqualTo(2.0f).Within(0.01f),
                "Spring should converge to the last target (2.0f).");
        }

        [Test]
        public void RetargetAfterFinish_RestartsSmoothly()
        {
            var spring = Spring.Create(0f);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 10000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should be finished before retarget.");

            spring.To(2f);

            Assert.IsFalse(spring.IsFinished, "Spring should be running after retarget from rest.");

            for (int i = 0; i < 1000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should converge to new target.");
            Assert.That(spring.Value, Is.EqualTo(2f).Within(0.01f),
                "Spring should converge to 2f after restart.");
        }

        [Test]
        public void MultipleOnCompleteSubscribers_AllFire()
        {
            int count1 = 0;
            int count2 = 0;
            int count3 = 0;

            var spring = Spring.Create(0f)
                .WithOnComplete(() => count1++)
                .WithOnComplete(() => count2++)
                .WithOnComplete(() => count3++);

            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 10000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should have come to rest.");
            Assert.That(count1, Is.EqualTo(1), "First OnComplete subscriber should fire exactly once.");
            Assert.That(count2, Is.EqualTo(1), "Second OnComplete subscriber should fire exactly once.");
            Assert.That(count3, Is.EqualTo(1), "Third OnComplete subscriber should fire exactly once.");
        }

        [Test]
        public void Stop_OnAlreadyFinishedSpring_NoDoubleCallback()
        {
            int completedCount = 0;
            var spring = Spring.Create(0f).WithOnComplete(() => completedCount++);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 10000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should be finished before second Stop().");
            Assert.That(completedCount, Is.EqualTo(1), "OnComplete should have fired once naturally.");

            spring.Stop();

            Assert.That(completedCount, Is.EqualTo(1),
                "Stop() on an already-finished spring should not fire OnComplete again.");
        }

        [Test]
        public void Set_WhileAnimating_SnapsAndFiresOnComplete()
        {
            int completedCount = 0;
            var spring = Spring.Create(0f).WithOnComplete(() => completedCount++);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 5; i++)
                spring.Advance(dt);

            Assert.IsFalse(spring.IsFinished, "Spring should still be running before Set().");

            spring.Set(0.5f);

            Assert.That(spring.Value, Is.EqualTo(0.5f).Within(0.0001f),
                "Set() should snap the value immediately.");
            Assert.IsTrue(spring.IsFinished, "Spring should be finished after Set().");
            Assert.That(completedCount, Is.EqualTo(1),
                "Set() on a running spring should fire OnComplete exactly once.");
        }

        [Test]
        public void Set_OnFinishedSpring_UpdatesValueNoCallback()
        {
            int completedCount = 0;
            var spring = Spring.Create(1f).WithOnComplete(() => completedCount++);

            Assert.IsTrue(spring.IsFinished, "Spring should start finished.");

            spring.Set(2f);

            Assert.That(spring.Value, Is.EqualTo(2f).Within(0.0001f),
                "Set() should update the value.");
            Assert.IsTrue(spring.IsFinished, "Spring should remain finished after Set().");
            Assert.That(completedCount, Is.EqualTo(0),
                "Set() on a finished spring should not fire OnComplete.");
        }

        [Test]
        public void Release_NeverStartedSpring_NoException()
        {
            var spring = Spring.Create(0f);

            Assert.DoesNotThrow(() => Spring.Release(spring),
                "Releasing a spring that was never started should not throw.");
            Assert.That(SpringManager.ActiveSpringCount, Is.EqualTo(0),
                "No active springs should remain after release.");
        }

        [Test]
        public void Release_CalledTwice_NoException()
        {
            var spring = Spring.Create(0f);
            spring.To(1f);

            Assert.DoesNotThrow(() => Spring.Release(spring),
                "First Release() should not throw.");
            Assert.DoesNotThrow(() => Spring.Release(spring),
                "Second Release() on the same spring should not throw.");
        }

        [Test]
        public void KillAll_WhenNoActiveSprings_NoException()
        {
            Assert.DoesNotThrow(() => Spring.KillAll(),
                "KillAll() with no active springs should not throw.");
            Assert.That(SpringManager.ActiveSpringCount, Is.EqualTo(0),
                "ActiveSpringCount should be 0 after KillAll on empty state.");
        }

        [Test]
        public void DecayRelease_StopsDecay()
        {
            var decay = Spring.CreateDecay(0f);
            decay.Play(10f);

            Assert.IsFalse(decay.IsFinished, "Decay should be running after Play().");

            Spring.Release(decay);

            Assert.IsTrue(decay.IsFinished, "Decay should be finished after Release().");
        }

        [Test]
        public void DecayStop_FiresOnComplete()
        {
            int completedCount = 0;
            var decay = Spring.CreateDecay(0f).WithOnComplete(() => completedCount++);
            decay.Play(10f);

            float dt = 0.016f;
            for (int i = 0; i < 5; i++)
                decay.Advance(dt);

            Assert.IsFalse(decay.IsFinished, "Decay should still be running before Stop().");

            decay.Stop();

            Assert.That(completedCount, Is.EqualTo(1),
                "Stop() on a running decay should fire OnComplete exactly once.");
            Assert.IsTrue(decay.IsFinished, "Decay should be finished after Stop().");
        }

        [Test]
        public void SpringWithOnStartSubscriber_FiredOnRestart()
        {
            int onStartCount = 0;
            var spring = Spring.Create(0f);
            spring.OnStart += _ => onStartCount++;

            spring.To(1f);

            Assert.That(onStartCount, Is.EqualTo(1), "OnStart should fire on first To().");

            float dt = 0.016f;
            for (int i = 0; i < 10000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should have come to rest.");

            spring.To(1f);

            Assert.That(onStartCount, Is.EqualTo(2),
                "OnStart should fire again when To() is called after spring has finished.");

            for (int i = 0; i < 10000; i++)
            {
                if (spring.IsFinished) break;
                spring.Advance(dt);
            }

            Assert.IsTrue(spring.IsFinished, "Spring should finish on second run.");
        }

        [Test]
        public void LargeNumberOfConcurrentSprings_AllConverge()
        {
            const int count = 50;
            var springs = new SpringValue<float>[count];

            for (int i = 0; i < count; i++)
            {
                springs[i] = Spring.Create(0f);
                springs[i].To((i + 1) * 0.1f);
            }

            float dt = 0.016f;
            for (int step = 0; step < 1000; step++)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!springs[i].IsFinished)
                        springs[i].Advance(dt);
                }
            }

            for (int i = 0; i < count; i++)
            {
                Assert.IsTrue(springs[i].IsFinished,
                    $"Spring {i} targeting {(i + 1) * 0.1f} should have converged.");
            }
        }

        [Test]
        public void SpringWith_MinStiffness_DoesNotCrash()
        {
            // Stiffness 0.001 with default damping is massively overdamped (ζ ≈ 400+).
            // Convergence would take tens of thousands of simulated seconds — don't assert IsFinished.
            // Goal: verify no NaN/Inf/exception regardless of damping regime.
            var spring = Spring.Create(0f).WithStiffness(0.001f);
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 10000; i++)
            {
                spring.Advance(dt);
                Assert.That(float.IsNaN(spring.Value),  Is.False, $"NaN at step {i}");
                Assert.That(float.IsInfinity(spring.Value), Is.False, $"Inf at step {i}");
            }
        }
    }
}
