using NUnit.Framework;
using UnityEngine;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Playback events are raised in the middle of a frame's work, and handlers are entitled to
    /// close the player or swap its file from there. These tests drive the central tick with a
    /// delta of their own so the boundaries are hit deterministically without a running engine
    /// loop.
    /// </summary>
    [TestFixture]
    public class HapPlayerPlaybackEventTests : HapPlayerTestFixture
    {
        OpenResult Open(string path)
        {
            HapTestFixtures.Require(path);
            return HapTestFixtures.Await(Player.OpenAsync(path));
        }

        [Test]
        public void PlaybackCompleted_HandlerThatCloses_DoesNotThrow()
        {
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int completed = 0;
            Player.Loop = false;
            Player.Opened += () => { };
            Player.PlaybackCompleted += () =>
            {
                completed++;
                Player.Close();
            };

            Player.Play();

            // Run past the end of the video in one step: the handler closes the player from
            // inside the event, which takes the session out from under the rest of the tick.
            Assert.DoesNotThrow(() => HapMainLoop.Tick(Player.Duration * 2f));

            Assert.That(completed, Is.EqualTo(1));
            Assert.That(Player.IsPlaying, Is.False);

            Assert.That(HapTestFixtures.PollUntil(() => !Player.IsClosing), Is.True,
                "the close never settled");
            Assert.That(Player.IsOpen, Is.False);
        }

        [Test]
        public void PlaybackCompleted_HandlerThatClosesAsync_DoesNotThrow()
        {
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            Awaitable close = null;
            Player.Loop = false;
            Player.PlaybackCompleted += () => close = Player.CloseAsync();

            Player.Play();
            Assert.DoesNotThrow(() => HapMainLoop.Tick(Player.Duration * 2f));

            Assert.That(close, Is.Not.Null);
            HapTestFixtures.Await(close);
            Assert.That(Player.IsOpen, Is.False);
        }

        [Test]
        public void PlaybackLooped_HandlerThatOpensAnotherFile_DoesNotThrow()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap5);
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int looped = 0;
            Player.Loop = true;
            Player.PlaybackLooped += () =>
            {
                looped++;
                Player.Open(HapTestFixtures.Hap5);
            };

            Player.Play();

            // The loop boundary fires, and the handler swaps the file mid-tick.
            Assert.DoesNotThrow(() => HapMainLoop.Tick(Player.Duration * 1.5f));
            Assert.That(looped, Is.EqualTo(1));

            Assert.That(HapTestFixtures.PollUntil(() => Player.IsOpen), Is.True,
                "the file the handler asked for never opened");
            Assert.That(Player.FilePath, Is.EqualTo(HapTestFixtures.Hap5));
        }

        [Test]
        public void PlaybackLooped_HandlerThatDoesNothing_KeepsPlaying()
        {
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int looped = 0;
            Player.Loop = true;
            Player.PlaybackLooped += () => looped++;
            Player.Play();

            Assert.DoesNotThrow(() => HapMainLoop.Tick(Player.Duration * 1.5f));

            Assert.That(looped, Is.EqualTo(1));
            Assert.That(Player.IsPlaying, Is.True);
            Assert.That(Player.IsOpen, Is.True);
        }
    }
}
