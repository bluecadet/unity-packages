using NUnit.Framework;
using Bluecadet.Spring;

namespace Bluecadet.Spring.Tests
{
    [TestFixture]
    public class SpringPoolTests
    {
        [SetUp]
        public void SetUp() => Spring.KillAll();

        [TearDown]
        public void TearDown() => Spring.KillAll();

        [Test]
        public void Create_ReturnsValidInstance()
        {
            var spring = Spring.Create(0f);

            Assert.IsNotNull(spring, "Spring.Create should return a non-null instance.");
            Assert.IsTrue(spring.IsFinished, "Newly created spring should be finished (idle).");
            Assert.That(spring.Value, Is.EqualTo(0f).Within(0.0001f), "Spring value should equal initial.");
        }

        [Test]
        public void Release_StopsAnimation()
        {
            var spring = Spring.Create(0f);
            spring.To(1f);

            Assert.IsFalse(spring.IsFinished, "Spring should be running after To().");

            Spring.Release(spring);

            Assert.IsTrue(spring.IsFinished, "Spring should be finished after Release().");
        }

        [Test]
        public void KillAll_StopsAllActiveSprings()
        {
            var s1 = Spring.Create(0f);
            var s2 = Spring.Create(0f);
            var s3 = Spring.Create(0f);

            s1.To(1f);
            s2.To(1f);
            s3.To(1f);

            Spring.KillAll();

            Assert.That(SpringManager.ActiveSpringCount, Is.EqualTo(0),
                "ActiveSpringCount should be 0 after KillAll.");
        }

        [Test]
        public void KillAll_StopsAllActiveDecays()
        {
            var d1 = Spring.CreateDecay(0f);
            var d2 = Spring.CreateDecay(0f);
            var d3 = Spring.CreateDecay(0f);

            d1.Play(10f);
            d2.Play(10f);
            d3.Play(10f);

            Spring.KillAll();

            Assert.That(SpringManager.ActiveDecayCount, Is.EqualTo(0),
                "ActiveDecayCount should be 0 after KillAll.");
        }

        [Test]
        public void KillAll_DoesNotFireCallbacks()
        {
            int springCompleted = 0;
            int decayCompleted = 0;

            var spring = Spring.Create(0f).WithOnComplete(() => springCompleted++);
            spring.To(1f);

            var decay = Spring.CreateDecay(0f).WithOnComplete(() => decayCompleted++);
            decay.Play(10f);

            Spring.KillAll();

            Assert.That(springCompleted, Is.EqualTo(0), "Spring OnComplete should not fire on KillAll.");
            Assert.That(decayCompleted, Is.EqualTo(0), "Decay OnComplete should not fire on KillAll.");
        }

        [Test]
        public void CreateAfterRelease_GetsFreshState()
        {
            var spring1 = Spring.Create(0f).WithDamping(99f);
            Spring.Release(spring1);

            var spring2 = Spring.Create(0f);

            Assert.That(spring2.Damping, Is.EqualTo(26f).Within(0.0001f),
                "Spring acquired after pool release should have default damping, not the released spring's damping.");
        }

        [Test]
        public void ActiveDecayCount_TracksDecays()
        {
            var decay = Spring.CreateDecay(0f);
            decay.Play(10f);

            Assert.That(SpringManager.ActiveDecayCount, Is.GreaterThanOrEqualTo(1),
                "ActiveDecayCount should be at least 1 after Play().");

            Spring.KillAll();

            Assert.That(SpringManager.ActiveDecayCount, Is.EqualTo(0),
                "ActiveDecayCount should be 0 after KillAll.");
        }
    }
}
