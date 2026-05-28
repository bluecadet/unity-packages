using NUnit.Framework;
using Bluecadet.Hap;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class PlaybackClockTests
    {
        const float Duration = 10f;
        const float Eps = 1e-5f;

        // ── Advance — no boundary ────────────────────────────────────────────

        [Test]
        public void Advance_ForwardWithinBounds_ReturnsNone()
        {
            var clock = new PlaybackClock { Time = 3f };
            var result = clock.Advance(0.1f, 1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.None));
            Assert.That(clock.Time, Is.EqualTo(3.1f).Within(Eps));
        }

        [Test]
        public void Advance_ReverseWithinBounds_ReturnsNone()
        {
            var clock = new PlaybackClock { Time = 5f };
            var result = clock.Advance(0.1f, -1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.None));
            Assert.That(clock.Time, Is.EqualTo(4.9f).Within(Eps));
        }

        // ── Forward overshoot — loop ─────────────────────────────────────────

        [Test]
        public void Advance_ForwardPastEnd_Loop_ReturnsLooped()
        {
            var clock = new PlaybackClock { Time = 9.9f };
            var result = clock.Advance(0.5f, 1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Looped));
            Assert.That(clock.Time, Is.EqualTo(0.4f).Within(Eps));
        }

        [Test]
        public void Advance_ForwardExactDuration_Loop_WrapsToZero()
        {
            var clock = new PlaybackClock { Time = 9f };
            var result = clock.Advance(1f, 1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Looped));
            Assert.That(clock.Time, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void Advance_ForwardOvershootMultipleLengths_Loop_WrapsCorrectly()
        {
            var clock = new PlaybackClock { Time = 0f };
            var result = clock.Advance(25f, 1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Looped));
            Assert.That(clock.Time, Is.EqualTo(5f).Within(Eps));
        }

        // ── Forward overshoot — no loop ──────────────────────────────────────

        [Test]
        public void Advance_ForwardPastEnd_NoLoop_ReturnsCompleted()
        {
            var clock = new PlaybackClock { Time = 9.9f };
            var result = clock.Advance(0.5f, 1f, Duration, loop: false);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Completed));
            Assert.That(clock.Time, Is.EqualTo(Duration).Within(Eps));
        }

        [Test]
        public void Advance_ForwardExactDuration_NoLoop_ClampsAndCompletes()
        {
            var clock = new PlaybackClock { Time = 9f };
            var result = clock.Advance(1f, 1f, Duration, loop: false);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Completed));
            Assert.That(clock.Time, Is.EqualTo(Duration).Within(Eps));
        }

        // ── Reverse overshoot — loop ─────────────────────────────────────────

        [Test]
        public void Advance_ReversePastStart_Loop_ReturnsLooped()
        {
            var clock = new PlaybackClock { Time = 0.2f };
            var result = clock.Advance(0.5f, -1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Looped));
            Assert.That(clock.Time, Is.EqualTo(9.7f).Within(Eps));
        }

        [Test]
        public void Advance_ReverseBelowZero_Loop_WrapsToNearEnd()
        {
            // Time = 0.5 - 1.0 = -0.5 → ((-0.5 % 10) + 10) % 10 = 9.5
            var clock = new PlaybackClock { Time = 0.5f };
            var result = clock.Advance(1f, -1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Looped));
            Assert.That(clock.Time, Is.EqualTo(9.5f).Within(Eps));
        }

        [Test]
        public void Advance_ReverseOvershootMultipleLengths_Loop_WrapsCorrectly()
        {
            // Time = 3 - 25 = -22 → ((-22 % 10) + 10) % 10 = (-2 + 10) % 10 = 8
            var clock = new PlaybackClock { Time = 3f };
            var result = clock.Advance(25f, -1f, Duration, loop: true);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Looped));
            Assert.That(clock.Time, Is.EqualTo(8f).Within(Eps));
        }

        // ── Reverse overshoot — no loop ──────────────────────────────────────

        [Test]
        public void Advance_ReversePastStart_NoLoop_ReturnsCompleted()
        {
            var clock = new PlaybackClock { Time = 0.2f };
            var result = clock.Advance(0.5f, -1f, Duration, loop: false);
            Assert.That(result, Is.EqualTo(ClockAdvanceEvent.Completed));
            Assert.That(clock.Time, Is.EqualTo(0f).Within(Eps));
        }

        // ── ToFrame ──────────────────────────────────────────────────────────

        [Test]
        public void ToFrame_TimeZero_ReturnsFirstFrame()
        {
            var clock = new PlaybackClock { Time = 0f };
            Assert.That(clock.ToFrame(300, 30f), Is.EqualTo(0));
        }

        [Test]
        public void ToFrame_MidVideo_ReturnsCorrectFrame()
        {
            var clock = new PlaybackClock { Time = 1f };
            Assert.That(clock.ToFrame(300, 30f), Is.EqualTo(30));
        }

        [Test]
        public void ToFrame_NearEnd_ReturnsLastFrame()
        {
            var clock = new PlaybackClock { Time = 9.999f };
            Assert.That(clock.ToFrame(300, 30f), Is.EqualTo(299));
        }

        [Test]
        public void ToFrame_AtOrPastDuration_ClampsToLastFrame()
        {
            // Clocks at or past Duration (e.g. after Completed) must not go out of range.
            var clock = new PlaybackClock { Time = 10f };
            Assert.That(clock.ToFrame(300, 30f), Is.EqualTo(299));
        }

        [Test]
        public void ToFrame_ZeroFrameCount_ReturnsZero()
        {
            var clock = new PlaybackClock { Time = 5f };
            Assert.That(clock.ToFrame(0, 30f), Is.EqualTo(0));
        }

        [Test]
        public void ToFrame_SingleFrame_AlwaysReturnsZero()
        {
            var clock = new PlaybackClock { Time = 5f };
            Assert.That(clock.ToFrame(1, 30f), Is.EqualTo(0));
        }
    }
}
