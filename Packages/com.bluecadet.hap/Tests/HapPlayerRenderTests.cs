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

            HapMainLoop.Tick(0f);
            int baseline = Player.RenderWorkCount;
            Assert.That(baseline, Is.GreaterThan(0));

            // Not playing, nothing decoded, nothing reassigned: this tick has nothing new to show.
            HapMainLoop.Tick(0f);
            HapMainLoop.Tick(0f);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline),
                "a steady-state tick with no new frame re-did the render work");
        }

        /// <summary>
        /// A UnityTest, not a plain Test: nothing is uploaded in the same engine frame a file
        /// opened in (the D3D12 command-list-flush workaround), and a synchronous Test never
        /// leaves that frame. Yielding lets real frames elapse so a genuinely new decoded frame
        /// can reach <see cref="HapPlayer.RenderWorkCount"/>.
        /// </summary>
        [UnityTest]
        public IEnumerator RenderTexture_NewFrameUploaded_EventuallyRenders()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            HapMainLoop.Tick(0f);
            int baseline = Player.RenderWorkCount;

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

            HapMainLoop.Tick(0f);
            int baseline = Player.RenderWorkCount;

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

            HapMainLoop.Tick(0f);
            int baseline = Player.RenderWorkCount;
            Assert.That(baseline, Is.GreaterThan(0));

            HapMainLoop.Tick(0f);
            HapMainLoop.Tick(0f);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline),
                "a steady-state tick with no new frame re-did the property block work");
        }
    }
}
