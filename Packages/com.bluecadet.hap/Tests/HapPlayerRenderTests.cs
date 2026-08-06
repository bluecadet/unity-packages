using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Every central tick ends in a render for every player, but the GPU work behind
    /// <see cref="HapRenderMode.RenderTexture"/> and <see cref="HapRenderMode.MaterialOverride"/>
    /// must only actually run when something it depends on changed — not on every tick a paused
    /// or capped-framerate video sits through. <see cref="HapPlayer.RenderWorkCount"/> is a test
    /// seam that counts calls that did that work, so these assert on it rather than the GPU
    /// output itself.
    ///
    /// These drive the central tick with an explicit delta, the way the runtime drives every
    /// registered player, rather than poking the player directly.
    /// </summary>
    [TestFixture]
    public class HapPlayerRenderTests : HapPlayerTestFixture
    {
        RenderTexture _target;
        GameObject _rendererHost;

        [TearDown]
        public void TearDownRenderTargets()
        {
            if (_target != null)
            {
                _target.Release();
                Object.DestroyImmediate(_target);
                _target = null;
            }
            if (_rendererHost != null)
            {
                Object.DestroyImmediate(_rendererHost);
                _rendererHost = null;
            }
        }

        RenderTexture MakeTarget()
        {
            _target = new RenderTexture(HapTestFixtures.Width, HapTestFixtures.Height, 0, RenderTextureFormat.ARGB32);
            _target.Create();
            return _target;
        }

        Renderer MakeRenderer()
        {
            _rendererHost = GameObject.CreatePrimitive(PrimitiveType.Quad);
            return _rendererHost.GetComponent<Renderer>();
        }

        OpenResult Open(string path)
        {
            HapTestFixtures.Require(path);
            return HapTestFixtures.Await(Player.OpenAsync(path));
        }

        /// <summary>
        /// Tick until the render count stops moving, and answer where it settled.
        ///
        /// Opening leaves a decoded frame waiting that no tick has uploaded yet, and the tick
        /// that does upload it shows a new texture — a legitimate render, not repeated work. So
        /// "steady state" only begins once that has happened, and a baseline taken any earlier
        /// is really measuring the tail of the open.
        /// </summary>
        int TickUntilSettled()
        {
            // A run of quiet ticks, not one: the frame waiting at open lands a tick or two after
            // the open itself reports done, and a single unchanged tick happens before that.
            const int quietTicks = 5;

            int quiet = 0;
            Assert.That(HapTestFixtures.PollUntil(
                    () => quiet >= quietTicks,
                    () =>
                    {
                        int before = Player.RenderWorkCount;
                        HapMainLoop.Tick(0f);
                        quiet = Player.RenderWorkCount == before ? quiet + 1 : 0;
                    }),
                Is.True, "the render count never stopped moving");

            return Player.RenderWorkCount;
        }

        [Test]
        public void RenderTexture_TickThatOpensTheFile_RendersTheInitialFrame()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();

            // The open lands inside a central tick, and a tick renders after it has carried every
            // player's lifecycle forward — so the tick that finished the open shows what it
            // produced, exactly once.
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(1),
                "the tick that finished the open must render the initial frame once");
        }

        [Test]
        public void RenderTexture_SteadyStateTick_DoesNotRepeatWork()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int baseline = TickUntilSettled();
            Assert.That(baseline, Is.GreaterThan(0));

            // Not playing, nothing decoded, nothing reassigned: this tick has nothing new to show.
            HapMainLoop.Tick(0f);
            HapMainLoop.Tick(0f);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline),
                "a steady-state tick with no new frame re-did the render work");
        }

        /// <summary>
        /// A UnityTest, not a plain Test: the frame this waits for has to be decoded off the main
        /// thread first, and yielding is what gives that thread wall-clock time to produce it.
        ///
        /// The baseline is taken from a settled state on purpose — the frame waiting at open
        /// would otherwise satisfy this on its own, and the test would pass without a single new
        /// frame ever being decoded.
        /// </summary>
        [UnityTest]
        public IEnumerator RenderTexture_NewFrameUploaded_EventuallyRenders()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int baseline = TickUntilSettled();

            Player.Play();

            bool rendered = false;
            var clock = Stopwatch.StartNew();
            while (!rendered && clock.ElapsedMilliseconds < HapTestFixtures.TimeoutMs)
            {
                // Roughly a frame's worth at typical fixture frame rates.
                HapMainLoop.Tick(0.05f);
                if (Player.RenderWorkCount > baseline) rendered = true;
                yield return null;
            }

            Assert.That(rendered, Is.True, "a new decoded frame never triggered a re-render");
        }

        [Test]
        public void RenderTexture_ReassigningTarget_ReRenders()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int baseline = TickUntilSettled();

            // Steady state first: confirm nothing renders again before the reassignment.
            HapMainLoop.Tick(0f);
            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline));

            var secondTarget = new RenderTexture(HapTestFixtures.Width, HapTestFixtures.Height, 0,
                RenderTextureFormat.ARGB32);
            secondTarget.Create();
            try
            {
                Player.TargetRenderTexture = secondTarget;
                HapMainLoop.Tick(0f);

                Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline + 1),
                    "reassigning the target render texture must re-render even with the same frame");
            }
            finally
            {
                secondTarget.Release();
                Object.DestroyImmediate(secondTarget);
            }
        }

        [Test]
        public void MaterialOverride_SteadyStateTick_DoesNotRepeatWork()
        {
            Player.RenderMode = HapRenderMode.MaterialOverride;
            Player.TargetRenderer = MakeRenderer();
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int baseline = TickUntilSettled();
            Assert.That(baseline, Is.GreaterThan(0));

            HapMainLoop.Tick(0f);
            HapMainLoop.Tick(0f);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline),
                "a steady-state tick with no new frame re-did the property block work");
        }
    }
}
