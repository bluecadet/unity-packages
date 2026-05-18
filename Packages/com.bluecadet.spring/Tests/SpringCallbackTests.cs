using System.Collections.Generic;
using NUnit.Framework;
using Bluecadet.Spring;

namespace Bluecadet.Spring.Tests
{
    [TestFixture]
    public class SpringCallbackTests
    {
        [SetUp]
        public void SetUp() => Spring.KillAll();

        [TearDown]
        public void TearDown() => Spring.KillAll();

        [Test]
        public void OnComplete_FiredWhenSpringRests()
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

            Assert.IsTrue(spring.IsFinished, "Spring should have come to rest.");
            Assert.That(completedCount, Is.EqualTo(1), "OnComplete should have fired exactly once.");
        }

        [Test]
        public void OnComplete_FiredOnce_NotMultipleTimes()
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

            // Advance extra steps after rest
            for (int i = 0; i < 100; i++)
                spring.Advance(dt);

            Assert.That(completedCount, Is.EqualTo(1), "OnComplete should fire exactly once, not on extra steps.");
        }

        [Test]
        public void OnComplete_NotFiredOnKillAll()
        {
            int completedCount = 0;
            var spring = Spring.Create(0f).WithOnComplete(() => completedCount++);
            spring.To(1f);

            Spring.KillAll();

            Assert.That(completedCount, Is.EqualTo(0), "OnComplete should not fire when KillAll is called.");
        }

        [Test]
        public void OnStart_FiredOnTo()
        {
            int onStartCount = 0;
            var spring = Spring.Create(0f);
            spring.OnStart += _ => onStartCount++;

            spring.To(1f);

            Assert.That(onStartCount, Is.EqualTo(1), "OnStart should fire when To() is called on an idle spring.");
        }

        [Test]
        public void OnStart_NotFiredOnRetarget()
        {
            int onStartCount = 0;
            var spring = Spring.Create(0f);
            spring.OnStart += _ => onStartCount++;

            spring.To(1f);

            // Advance a bit so spring is mid-flight
            float dt = 0.016f;
            for (int i = 0; i < 10; i++)
                spring.Advance(dt);

            Assert.IsFalse(spring.IsFinished, "Spring should still be running for this test to be valid.");

            spring.To(2f);

            Assert.That(onStartCount, Is.EqualTo(1),
                "OnStart should not fire again when re-targeting a running spring.");
        }

        [Test]
        public void Stop_FiresOnComplete()
        {
            int completedCount = 0;
            var spring = Spring.Create(0f).WithOnComplete(() => completedCount++);
            spring.To(1f);

            // Advance a bit so it's running
            spring.Advance(0.016f);
            Assert.IsFalse(spring.IsFinished, "Spring should still be running before Stop().");

            spring.Stop();

            Assert.That(completedCount, Is.EqualTo(1), "OnComplete should fire when Stop() is called.");
            Assert.IsTrue(spring.IsFinished, "Spring should be finished after Stop().");
        }

        [Test]
        public void Bind_ReceivesValueEachAdvance()
        {
            var received = new List<float>();
            var spring = Spring.Create(0f).Bind(v => received.Add(v));
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 5; i++)
                spring.Advance(dt);

            Assert.That(received.Count, Is.EqualTo(5), "Bind should receive a value for each Advance call.");

            for (int i = 1; i < received.Count; i++)
            {
                Assert.That(received[i], Is.GreaterThan(received[i - 1]),
                    $"Values should increase toward target (step {i}).");
            }
        }

        [Test]
        public void TypedBind_ReceivesValueEachAdvance()
        {
            var received = new List<float>();
            var target = new System.Object();
            var spring = Spring.Create(0f).Bind(target, (v, t) => received.Add(v));
            spring.To(1f);

            float dt = 0.016f;
            for (int i = 0; i < 5; i++)
                spring.Advance(dt);

            Assert.That(received.Count, Is.EqualTo(5), "Typed bind should receive a value for each Advance call.");

            for (int i = 1; i < received.Count; i++)
            {
                Assert.That(received[i], Is.GreaterThan(received[i - 1]),
                    $"Values should increase toward target (step {i}).");
            }
        }
    }
}
