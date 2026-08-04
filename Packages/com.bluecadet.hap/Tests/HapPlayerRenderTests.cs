using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// <see cref="HapPlayer.TickPlayback"/> renders every tick, but the GPU work behind
    /// <see cref="HapRenderMode.RenderTexture"/> and <see cref="HapRenderMode.MaterialOverride"/>
    /// must only actually run when something it depends on changed — not on every tick a paused
    /// or capped-framerate video sits through. <see cref="HapPlayer.RenderWorkCount"/> is a test
    /// seam that counts calls that did that work, so these assert on it rather than the GPU
    /// output itself.
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
        public void RenderTexture_FirstTickAfterOpen_DoesRealWork()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();

            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(0), "opening alone must not render");

            Player.TickPlayback(0f);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(1),
                "the first tick after open must render the initial frame");
        }

        [Test]
        public void RenderTexture_SteadyStateTick_DoesNotRepeatWork()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            Player.TickPlayback(0f);
            int baseline = Player.RenderWorkCount;
            Assert.That(baseline, Is.GreaterThan(0));

            // Not playing, nothing decoded, nothing reassigned: this tick has nothing new to show.
            Player.TickPlayback(0f);
            Player.TickPlayback(0f);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline),
                "a steady-state tick with no new frame re-did the render work");
        }

        /// <summary>
        /// A UnityTest, not a plain Test: <see cref="HapPlayer.UploadFrame"/> refuses to present
        /// in the same engine frame a file opened in (the D3D12 command-list-flush workaround),
        /// and a synchronous Test never leaves that frame. Yielding lets real frames elapse so a
        /// genuinely new decoded frame can reach <see cref="HapPlayer.RenderWorkCount"/>.
        /// </summary>
        [UnityTest]
        public IEnumerator RenderTexture_NewFrameUploaded_EventuallyRenders()
        {
            Player.RenderMode = HapRenderMode.RenderTexture;
            Player.TargetRenderTexture = MakeTarget();
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            Player.TickPlayback(0f);
            int baseline = Player.RenderWorkCount;

            Player.Play();

            bool rendered = false;
            var clock = Stopwatch.StartNew();
            while (!rendered && clock.ElapsedMilliseconds < HapTestFixtures.TimeoutMs)
            {
                Player.TickPlayback(0.05f); // roughly a frame's worth at typical fixture frame rates
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

            Player.TickPlayback(0f);
            int baseline = Player.RenderWorkCount;

            // Steady state first: confirm nothing renders again before the reassignment.
            Player.TickPlayback(0f);
            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline));

            var secondTarget = new RenderTexture(HapTestFixtures.Width, HapTestFixtures.Height, 0,
                RenderTextureFormat.ARGB32);
            secondTarget.Create();
            try
            {
                Player.TargetRenderTexture = secondTarget;
                Player.TickPlayback(0f);

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

            Player.TickPlayback(0f);
            int baseline = Player.RenderWorkCount;
            Assert.That(baseline, Is.GreaterThan(0));

            Player.TickPlayback(0f);
            Player.TickPlayback(0f);

            Assert.That(Player.RenderWorkCount, Is.EqualTo(baseline),
                "a steady-state tick with no new frame re-did the property block work");
        }
    }
}
