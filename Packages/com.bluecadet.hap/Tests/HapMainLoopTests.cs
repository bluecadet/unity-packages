using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// The central tick: which players it holds on to, and the order and volume of the uploads it
    /// issues for them.
    ///
    /// Registration is a state transition — a player joins the loop when it starts opening a file
    /// and leaves once that file is released — and never per-frame work, which is what keeps a
    /// wall of players from scanning each other's list every frame.
    /// </summary>
    [TestFixture]
    public class HapMainLoopRegistrationTests : HapPlayerTestFixture
    {
        [Test]
        public void IdlePlayer_IsNotTicked()
        {
            Assert.That(Player.MainLoopIndex, Is.LessThan(0),
                "a player with nothing open has nothing for the loop to do");
        }

        [Test]
        public void OpenAndClose_AreTheOnlyRegistrationTransitions()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            // Settle anything an earlier test left in flight, so the count only moves for us.
            HapTestFixtures.Pump(3);
            int registered = HapMainLoop.RegisteredCount;

            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);
            Assert.That(Player.MainLoopIndex, Is.GreaterThanOrEqualTo(0), "an open player is not being ticked");
            Assert.That(HapMainLoop.RegisteredCount, Is.EqualTo(registered + 1));

            HapTestFixtures.Await(Player.CloseAsync());
            Assert.That(Player.MainLoopIndex, Is.LessThan(0), "a released player is still being ticked");
            Assert.That(HapMainLoop.RegisteredCount, Is.EqualTo(registered));
        }

        [Test]
        public void SteadyPlayback_NeverTouchesTheRegistrationList()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            Player.Play();

            int registered = HapMainLoop.RegisteredCount;
            int changes = HapMainLoop.RegistrationChanges;

            for (int i = 0; i < 20; i++)
                HapMainLoop.Tick(1f / 60f);

            Assert.That(HapMainLoop.RegistrationChanges, Is.EqualTo(changes),
                "playing a video registered or unregistered something every frame");
            Assert.That(HapMainLoop.RegisteredCount, Is.EqualTo(registered));
            Assert.That(Player.IsOpen, Is.True, "the player stopped playing mid-test");
        }
    }

    /// <summary>
    /// <see cref="HapPlayer.UploadBudgetBytesPerFrame"/>, the public knob the README advertises:
    /// it must actually reach <see cref="HapMainLoop"/>, since that is the only place the budget
    /// does anything.
    /// </summary>
    [TestFixture]
    public class HapPlayerUploadBudgetTests
    {
        long _original;

        [SetUp]
        public void SaveBudget() => _original = HapMainLoop.UploadBudgetBytesPerFrame;

        // The budget is process-wide state, so a test that changes it must not leak the change
        // into whichever test runs next.
        [TearDown]
        public void RestoreBudget() => HapMainLoop.UploadBudgetBytesPerFrame = _original;

        [Test]
        public void PublicProperty_ForwardsToTheMainLoop()
        {
            HapPlayer.UploadBudgetBytesPerFrame = 12_345;
            Assert.That(HapMainLoop.UploadBudgetBytesPerFrame, Is.EqualTo(12_345),
                "the public knob did not reach the loop it is supposed to configure");

            HapMainLoop.UploadBudgetBytesPerFrame = 67_890;
            Assert.That(HapPlayer.UploadBudgetBytesPerFrame, Is.EqualTo(67_890),
                "the public knob did not read back what the loop is actually using");
        }
    }

    /// <summary>
    /// The central tick's playback phase must not touch a disabled player, even one with a file
    /// open — a contract every player used to get for free by driving itself from its own
    /// Update, and one <see cref="HapMainLoop"/> now has to enforce explicitly.
    /// </summary>
    [TestFixture]
    public class HapMainLoopEnabledStateTests : HapPlayerTestFixture
    {
        GameObject _otherHost;
        HapPlayer _other;

        [TearDown]
        public void DestroyOther()
        {
            if (_otherHost != null)
                Object.DestroyImmediate(_otherHost);
            _otherHost = null;
            _other = null;
        }

        [Test]
        public void DisabledPlayer_DoesNotAdvanceWhileEnabledPlayerDoes()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            _otherHost = new GameObject(nameof(HapMainLoopEnabledStateTests));
            _other = _otherHost.AddComponent<HapPlayer>();

            // Disabled before either file opens: OpenAsync and Play are not gated by enabled, so
            // this is the only way to get a disabled player with an open session to test against.
            Player.enabled = false;

            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);
            Assert.That(HapTestFixtures.Await(_other.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            Player.Play();
            _other.Play();

            float disabledTimeBefore = Player.Time;
            float otherTimeBefore = _other.Time;

            for (int i = 0; i < 5; i++)
                HapMainLoop.Tick(1f / 30f);

            Assert.That(Player.Time, Is.EqualTo(disabledTimeBefore), "a disabled player's clock advanced");
            Assert.That(_other.Time, Is.GreaterThan(otherTimeBefore),
                "an enabled player alongside a disabled one stopped advancing");

            Assert.That(Player.MainLoopIndex, Is.GreaterThanOrEqualTo(0),
                "a disabled player with an open file must stay registered so its lifecycle keeps advancing");
        }
    }

    /// <summary>
    /// The upload phase in isolation, on stand-in players: rotation and the byte budget decide
    /// who uploads without any of it touching a GPU.
    /// </summary>
    [TestFixture]
    public class HapUploadPhaseTests
    {
        /// <summary>A player as the upload phase sees it, recording what it was asked to do.</summary>
        sealed class FakeTarget : IHapUploadTarget
        {
            readonly List<string> _order;

            public FakeTarget(List<string> order, string name, long bytes)
            {
                _order = order;
                Name = name;
                PendingUploadBytes = bytes;
            }

            public string Name { get; }
            public long PendingUploadBytes { get; }
            public int Uploads { get; private set; }
            public int Renders { get; private set; }

            public void TickUpload()
            {
                Uploads++;
                _order.Add(Name);
            }

            public void TickRender() => Renders++;
        }

        const long FrameBytes = 100;

        List<string> _order;
        List<IHapUploadTarget> _due;
        HapUploadPhase _phase;

        [SetUp]
        public void SetUp()
        {
            _order = new List<string>();
            _due = new List<IHapUploadTarget>();
            _phase = new HapUploadPhase();
        }

        FakeTarget Add(string name, long bytes = FrameBytes)
        {
            var target = new FakeTarget(_order, name, bytes);
            _due.Add(target);
            return target;
        }

        [Test]
        public void NoBudget_UploadsEveryDuePlayer()
        {
            var a = Add("a");
            var b = Add("b");

            _phase.Run(_due, 0);

            Assert.That(a.Uploads, Is.EqualTo(1));
            Assert.That(b.Uploads, Is.EqualTo(1));
            Assert.That(a.Renders, Is.EqualTo(0), "an uploading player renders as part of its upload");
            Assert.That(b.Renders, Is.EqualTo(0));
        }

        [Test]
        public void EachTick_StartsOnePlayerFurtherAlong()
        {
            Add("a");
            Add("b");
            Add("c");

            for (int tick = 0; tick < 4; tick++)
                _phase.Run(_due, 0);

            Assert.That(_order, Is.EqualTo(new[]
            {
                "a", "b", "c",
                "b", "c", "a",
                "c", "a", "b",
                "a", "b", "c",
            }), "the upload phase started from the same player every tick");
        }

        [Test]
        public void OverBudget_DefersTheRestAndStillRendersThem()
        {
            var a = Add("a");
            var b = Add("b");
            var c = Add("c");
            var d = Add("d");

            // Room for two and a half frames: the third upload is what carries the total past the
            // budget, so the fourth player is the first one deferred.
            _phase.Run(_due, FrameBytes * 5 / 2);

            Assert.That(_order, Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(d.Uploads, Is.EqualTo(0), "the budget was overrun");
            Assert.That(d.Renders, Is.EqualTo(1), "a deferred player must still show the frame it has");
            Assert.That(a.Renders + b.Renders + c.Renders, Is.EqualTo(0));
        }

        [Test]
        public void OverBudget_DefersADifferentPlayerEachTick()
        {
            var a = Add("a");
            var b = Add("b");
            var c = Add("c");

            // One frame each, so exactly two of the three fit in a tick.
            _phase.Run(_due, FrameBytes * 3 / 2);
            _phase.Run(_due, FrameBytes * 3 / 2);
            _phase.Run(_due, FrameBytes * 3 / 2);

            Assert.That(_order, Is.EqualTo(new[] { "a", "b", "b", "c", "c", "a" }));
            Assert.That(new[] { a.Uploads, b.Uploads, c.Uploads }, Is.EqualTo(new[] { 2, 2, 2 }),
                "the budget starved one player instead of taking a turn from each");
            Assert.That(new[] { a.Renders, b.Renders, c.Renders }, Is.EqualTo(new[] { 1, 1, 1 }));
        }

        [Test]
        public void BudgetSmallerThanOneFrame_StillUploadsOnePlayer()
        {
            var a = Add("a");
            var b = Add("b");

            _phase.Run(_due, 1);

            Assert.That(a.Uploads, Is.EqualTo(1), "a budget below one frame must throttle, not starve");
            Assert.That(b.Uploads, Is.EqualTo(0));
            Assert.That(b.Renders, Is.EqualTo(1));
        }

        [Test]
        public void NothingDue_StillRotates()
        {
            _phase.Run(_due, 0);

            Add("a");
            Add("b");
            _phase.Run(_due, 0);

            Assert.That(_order, Is.EqualTo(new[] { "b", "a" }),
                "a tick with nothing to upload should still move the rotation on");
        }
    }
}
