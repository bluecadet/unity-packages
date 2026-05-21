using NUnit.Framework;
using UnityEngine;
using Bluecadet.Touchscreen;

namespace Bluecadet.Touchscreen.Tests
{
    [TestFixture]
    public class VelocityTrackerTests
    {
        private const float Epsilon = 0.0001f;

        // -------------------------------------------------------------------------
        // Group 1 — Empty tracker
        // -------------------------------------------------------------------------

        [Test]
        public void GetLastVelocity_NoSamples_ReturnsZero()
        {
            var tracker = new VelocityTracker1D();
            Assert.That(tracker.GetLastVelocity(), Is.EqualTo(0f).Within(Epsilon));
        }

        [Test]
        public void GetAveragedVelocity_NoSamples_ReturnsZero()
        {
            var tracker = new VelocityTracker1D();
            Assert.That(tracker.GetAveragedVelocity(), Is.EqualTo(0f).Within(Epsilon));
        }

        // -------------------------------------------------------------------------
        // Group 2 — Track()-based velocity
        // -------------------------------------------------------------------------

        [Test]
        public void Track_SingleSample_GetLastVelocityIsZero()
        {
            var tracker = new VelocityTracker1D();
            tracker.Track(5f, 0f);
            Assert.That(tracker.GetLastVelocity(-1f), Is.EqualTo(0f).Within(Epsilon));
        }

        [Test]
        public void Track_TwoSamples_ComputesVelocityFromDelta()
        {
            var tracker = new VelocityTracker1D();
            tracker.Track(0f, 0f);
            tracker.Track(10f, 1f);
            Assert.That(tracker.GetLastVelocity(-1f), Is.EqualTo(10f).Within(Epsilon));
        }

        [Test]
        public void Track_SamePosition_VelocityIsZero()
        {
            var tracker = new VelocityTracker1D();
            tracker.Track(5f, 0f);
            tracker.Track(5f, 1f);
            Assert.That(tracker.GetLastVelocity(-1f), Is.EqualTo(0f).Within(Epsilon));
        }

        [Test]
        public void Track_NegativeDelta_VelocityIsNegative()
        {
            var tracker = new VelocityTracker1D();
            tracker.Track(10f, 0f);
            tracker.Track(0f, 1f);
            Assert.That(tracker.GetLastVelocity(-1f), Is.EqualTo(-10f).Within(Epsilon));
        }

        // -------------------------------------------------------------------------
        // Group 3 — TrackVelocity()
        // -------------------------------------------------------------------------

        [Test]
        public void TrackVelocity_SingleSample_RetrievableAsLastVelocity()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(5f, 0f);
            Assert.That(tracker.GetLastVelocity(-1f), Is.EqualTo(5f).Within(Epsilon));
        }

        [Test]
        public void TrackVelocity_OverwritesBuffer_OldestDroppedWhenFull()
        {
            var tracker = new VelocityTracker1D(sampleCount: 3);
            tracker.TrackVelocity(1f, 0f);
            tracker.TrackVelocity(2f, 0.01f);
            tracker.TrackVelocity(3f, 0.02f);
            tracker.TrackVelocity(4f, 0.03f);
            // Buffer holds only 3 samples: [2, 3, 4]; average = 3
            Assert.That(tracker.GetAveragedVelocity(-1f), Is.EqualTo(3f).Within(Epsilon));
        }

        // -------------------------------------------------------------------------
        // Group 4 — Age filtering
        // -------------------------------------------------------------------------

        [Test]
        public void GetLastVelocity_FreshSample_ReturnsVelocity()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(5f, 0f);
            // currentTime=0.05, maxAge=0.1 → age=0.05 ≤ 0.1 → valid
            Assert.That(tracker.GetLastVelocity(0.05f, 0.1f), Is.EqualTo(5f).Within(Epsilon));
        }

        [Test]
        public void GetLastVelocity_StaleSample_ReturnsZero()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(5f, 0f);
            // currentTime=0.2, maxAge=0.1 → age=0.2 > 0.1 → stale
            Assert.That(tracker.GetLastVelocity(0.2f, 0.1f), Is.EqualTo(0f).Within(Epsilon));
        }

        [Test]
        public void GetLastVelocity_NegativeCurrentTime_SkipsAgeCheck()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(5f, 0f);
            Assert.That(tracker.GetLastVelocity(-1f), Is.EqualTo(5f).Within(Epsilon));
        }

        [Test]
        public void GetAveragedVelocity_StaleAndFreshSamplesMixed_OnlyFreshIncluded()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(1f, 0f);
            tracker.TrackVelocity(2f, 0.05f);
            tracker.TrackVelocity(3f, 0.10f);
            // currentTime=0.15, maxAge=0.08
            // t=0.10 → age=0.05 ≤ 0.08 → include (val=3)
            // t=0.05 → age=0.10 > 0.08 → exclude
            // t=0.00 → age=0.15 > 0.08 → exclude
            // Only 1 valid sample → average = 3
            Assert.That(tracker.GetAveragedVelocity(0.15f, 0.08f), Is.EqualTo(3f).Within(Epsilon));
        }

        // -------------------------------------------------------------------------
        // Group 5 — Averaging
        // -------------------------------------------------------------------------

        [Test]
        public void GetAveragedVelocity_TwoEqualSamples_ReturnsSameValue()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(4f, 0f);
            tracker.TrackVelocity(4f, 0.01f);
            Assert.That(tracker.GetAveragedVelocity(-1f), Is.EqualTo(4f).Within(Epsilon));
        }

        [Test]
        public void GetAveragedVelocity_ThreeDistinctSamples_ReturnsArithmeticMean()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(2f, 0f);
            tracker.TrackVelocity(4f, 0.01f);
            tracker.TrackVelocity(6f, 0.02f);
            // (2 + 4 + 6) / 3 = 4
            Assert.That(tracker.GetAveragedVelocity(-1f), Is.EqualTo(4f).Within(Epsilon));
        }

        // -------------------------------------------------------------------------
        // Group 6 — Buffer wraparound
        // -------------------------------------------------------------------------

        [Test]
        public void TrackVelocity_CircularWrap_OnlyNewestSamplesAveraged()
        {
            var tracker = new VelocityTracker1D(sampleCount: 3);
            tracker.TrackVelocity(10f, 0f);
            tracker.TrackVelocity(20f, 0.01f);
            tracker.TrackVelocity(30f, 0.02f);
            tracker.TrackVelocity(40f, 0.03f);
            tracker.TrackVelocity(50f, 0.04f);
            // Buffer holds only 3 samples: [30, 40, 50]; average = 40
            Assert.That(tracker.GetAveragedVelocity(-1f), Is.EqualTo(40f).Within(Epsilon));
        }

        // -------------------------------------------------------------------------
        // Group 7 — Clear
        // -------------------------------------------------------------------------

        [Test]
        public void Clear_AfterTracking_GetLastVelocityReturnsZero()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(10f, 0f);
            tracker.Clear();
            Assert.That(tracker.GetLastVelocity(-1f), Is.EqualTo(0f).Within(Epsilon));
        }

        [Test]
        public void Clear_AfterTracking_GetAveragedVelocityReturnsZero()
        {
            var tracker = new VelocityTracker1D();
            tracker.TrackVelocity(10f, 0f);
            tracker.TrackVelocity(20f, 0.01f);
            tracker.Clear();
            Assert.That(tracker.GetAveragedVelocity(-1f), Is.EqualTo(0f).Within(Epsilon));
        }

        // -------------------------------------------------------------------------
        // Group 8 — 2D and 3D variants
        // -------------------------------------------------------------------------

        [Test]
        public void VelocityTracker2D_TrackPositions_ComputesVelocity()
        {
            var tracker = new VelocityTracker2D();
            tracker.Track(Vector2.zero, 0f);
            tracker.Track(new Vector2(10f, 5f), 1f);
            var velocity = tracker.GetLastVelocity(-1f);
            Assert.That(velocity.x, Is.EqualTo(10f).Within(Epsilon));
            Assert.That(velocity.y, Is.EqualTo(5f).Within(Epsilon));
        }

        [Test]
        public void VelocityTracker3D_TrackPositions_ComputesVelocity()
        {
            var tracker = new VelocityTracker3D();
            tracker.Track(Vector3.zero, 0f);
            tracker.Track(new Vector3(3f, 4f, 0f), 1f);
            var velocity = tracker.GetLastVelocity(-1f);
            Assert.That(velocity.x, Is.EqualTo(3f).Within(Epsilon));
            Assert.That(velocity.y, Is.EqualTo(4f).Within(Epsilon));
            Assert.That(velocity.z, Is.EqualTo(0f).Within(Epsilon));
        }
    }
}
