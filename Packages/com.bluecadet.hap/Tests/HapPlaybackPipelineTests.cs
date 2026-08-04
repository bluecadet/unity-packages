using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// The whole playback path short of the MonoBehaviour: open a file, let the decode thread
    /// fill the texture ring, and present a frame through the output shader.
    /// </summary>
    [TestFixture]
    public class HapPlaybackPipelineTests
    {
        const int RetireDepth = 2;

        HapFileSession _session;
        HapOutputPipeline _pipeline;

        [TearDown]
        public void TearDown()
        {
            if (_session != null)
            {
                _session.BeginTeardown();
                Assert.That(_session.WaitForTeardown(HapTestFixtures.TimeoutMs), Is.True,
                    "the decode thread did not stop in time");
            }
            _pipeline?.Dispose();
            _pipeline = null;
            _session = null;
        }

        /// <summary>
        /// Poll step for this suite: everything it waits for is done by the decode thread, so
        /// there is no main-thread work to pump.
        /// </summary>
        static void Wait() => Thread.Sleep(1);

        /// <summary>Opens a fixture and starts its decode thread, or skips the test.</summary>
        void StartPlayback(string path)
        {
            HapTestFixtures.Require(path);

            _session = new HapFileSession(path);
            _session.Open();

            SessionOpenStatus status = SessionOpenStatus.NotReady;
            HapTestFixtures.PollUntil(() => (status = _session.TryConsumeOpenResult()) != SessionOpenStatus.NotReady,
                                      Wait);
            Assert.That(status, Is.EqualTo(SessionOpenStatus.Opened), $"failed to open {path}");

            _pipeline = new HapOutputPipeline(_session.Width, _session.Height, _session.Textures, RetireDepth);
            Assert.That(_pipeline.IsValid, Is.True);
            _session.StartDecoding(_pipeline.DecodeTarget, 0);
        }

        /// <summary>Pumps the main-thread side until a frame has been presented.</summary>
        bool PresentWithinTimeout() => HapTestFixtures.PollUntil(PresentOnce, Wait);

        bool PresentOnce()
        {
            if (!_pipeline.Present()) return false;
            _pipeline.SwapBuffers();
            return true;
        }

        /// <summary>Format the decode thread is filling a slot's colour texture with.</summary>
        TextureFormat DecodeTargetFormat() => _pipeline.DecodeTarget.GetTexture(0, 0).format;

        [Test]
        public void PlainHap_DecodesAndPresentsAFrame()
        {
            StartPlayback(HapTestFixtures.Hap1);

            Assert.That(PresentWithinTimeout(), Is.True, "no frame reached the output shader");
            Assert.That(_pipeline.DisplayTexture, Is.Not.Null);
            Assert.That(_pipeline.DisplayTexture.width, Is.EqualTo(HapTestFixtures.Width));
            Assert.That(DecodeTargetFormat(), Is.EqualTo(TextureFormat.DXT1));
        }

        [Test]
        public void HapQ_DecodesAndPresentsAFrame()
        {
            StartPlayback(HapTestFixtures.HapY);

            Assert.That(PresentWithinTimeout(), Is.True, "no frame reached the YCoCg decode shader");
            Assert.That(_pipeline.DecodeTarget.TextureCount, Is.EqualTo(1));
            Assert.That(DecodeTargetFormat(), Is.EqualTo(TextureFormat.DXT5));
        }

        [Test]
        public void HapQAlpha_DecodesBothTexturesAndPresentsAFrame()
        {
            StartPlayback(HapTestFixtures.HapM);

            Assert.That(_pipeline.DecodeTarget.TextureCount, Is.EqualTo(2),
                "Hap Q Alpha should decode into a colour and an alpha texture");
            Assert.That(PresentWithinTimeout(), Is.True, "no frame reached the alpha decode shader");
            Assert.That(_pipeline.DisplayTexture, Is.Not.Null,
                "the Hap Q Alpha output shader failed to load");
        }

        [Test]
        public void Playback_AdvancesThroughSeveralFrames()
        {
            StartPlayback(HapTestFixtures.Hap1);

            int presented = 0;
            HapTestFixtures.PollUntil(() =>
            {
                _session.RequestDecode(presented, 1);
                if (PresentOnce()) presented++;
                return presented >= 10;
            }, Wait);

            Assert.That(presented, Is.GreaterThanOrEqualTo(10), "playback stalled");
        }

        [Test]
        public void Teardown_ParksTheDecodeThreadBeforeTheTexturesGoAway()
        {
            StartPlayback(HapTestFixtures.Hap1);
            Assert.That(PresentWithinTimeout(), Is.True);

            // The teardown must report the decode thread parked before the caller destroys the
            // textures it decodes into.
            _session.BeginTeardown();
            Assert.That(_session.WaitForTeardown(HapTestFixtures.TimeoutMs), Is.True,
                "the decode thread did not stop in time");
            Assert.That(_session.IsTornDown, Is.True);
            Assert.DoesNotThrow(() => _pipeline.Dispose());
        }

        [Test]
        public void WaitForTeardown_WakesOnTheSignal_NotOnAPollTick()
        {
            // A session that never opened has nothing for the teardown thread to join or close,
            // so the background thread's only real cost is getting scheduled at all — the fastest
            // teardown there is. That makes it the sharpest test of what WaitForTeardown itself
            // adds on top: a poll wakes on the next 1ms tick after the signal, not on the signal,
            // so it pays that tick almost every time; an event-based wait does not. Averaged over
            // many trials the two are worlds apart, which is what keeps this from being flaky on
            // a loaded machine.
            const int Iterations = 30;
            double totalMs = 0;

            for (int i = 0; i < Iterations; i++)
            {
                var session = new HapFileSession($"/never/opened-{i}.mov");
                session.BeginTeardown();

                var clock = Stopwatch.StartNew();
                bool completed = session.WaitForTeardown(HapTestFixtures.TimeoutMs);
                clock.Stop();

                Assert.That(completed, Is.True, $"iteration {i} did not tear down in time");
                totalMs += clock.Elapsed.TotalMilliseconds;
            }

            double averageMs = totalMs / Iterations;
            Assert.That(averageMs, Is.LessThan(0.75),
                $"average wait of {averageMs:F3}ms looks like it is polling on a 1ms tick " +
                "rather than waking on the teardown thread's signal");
        }
    }
}
