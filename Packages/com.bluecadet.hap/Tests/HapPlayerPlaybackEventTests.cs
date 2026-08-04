using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Playback events are raised in the middle of a frame's work, and handlers are entitled to
    /// close the player or swap its file from there. These tests drive the clock directly so
    /// the boundaries are hit deterministically without a running player loop.
    /// </summary>
    [TestFixture]
    public class HapPlayerPlaybackEventTests
    {
        const int TimeoutMs = 15_000;

        GameObject _go;
        HapPlayer _player;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HapPlayerPlaybackEventTests");
            _player = _go.AddComponent<HapPlayer>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                UnityEngine.Object.DestroyImmediate(_go);
            _go = null;
            _player = null;
        }

        static void Pump()
        {
            HapMainLoop.Tick();
            Thread.Sleep(1);
        }

        OpenResult Open(string path)
        {
            HapTestFixtures.Require(path);

            var awaiter = _player.OpenAsync(path).GetAwaiter();
            var clock = Stopwatch.StartNew();
            while (!awaiter.IsCompleted && clock.ElapsedMilliseconds < TimeoutMs)
                Pump();

            Assert.That(awaiter.IsCompleted, Is.True, "the open never completed");
            return awaiter.GetResult();
        }

        [Test]
        public void PlaybackCompleted_HandlerThatCloses_DoesNotThrow()
        {
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int completed = 0;
            _player.Loop = false;
            _player.Opened += () => { };
            _player.PlaybackCompleted += () =>
            {
                completed++;
                _player.Close();
            };

            _player.Play();

            // Run past the end of the video in one step: the handler closes the player from
            // inside the event, which takes the session out from under the rest of the tick.
            Assert.DoesNotThrow(() => _player.TickPlayback(_player.Duration * 2f));

            Assert.That(completed, Is.EqualTo(1));
            Assert.That(_player.IsPlaying, Is.False);

            var clock = Stopwatch.StartNew();
            while (_player.IsClosing && clock.ElapsedMilliseconds < TimeoutMs)
                Pump();

            Assert.That(_player.IsOpen, Is.False);
        }

        [Test]
        public void PlaybackCompleted_HandlerThatClosesAsync_DoesNotThrow()
        {
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            Awaitable close = null;
            _player.Loop = false;
            _player.PlaybackCompleted += () => close = _player.CloseAsync();

            _player.Play();
            Assert.DoesNotThrow(() => _player.TickPlayback(_player.Duration * 2f));

            Assert.That(close, Is.Not.Null);
            var awaiter = close.GetAwaiter();
            var clock = Stopwatch.StartNew();
            while (!awaiter.IsCompleted && clock.ElapsedMilliseconds < TimeoutMs)
                Pump();

            Assert.That(awaiter.IsCompleted, Is.True);
            Assert.That(_player.IsOpen, Is.False);
        }

        [Test]
        public void PlaybackLooped_HandlerThatOpensAnotherFile_DoesNotThrow()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap5);
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int looped = 0;
            _player.Loop = true;
            _player.PlaybackLooped += () =>
            {
                looped++;
                _player.Open(HapTestFixtures.Hap5);
            };

            _player.Play();

            // The loop boundary fires, and the handler swaps the file mid-tick.
            Assert.DoesNotThrow(() => _player.TickPlayback(_player.Duration * 1.5f));
            Assert.That(looped, Is.EqualTo(1));

            var clock = Stopwatch.StartNew();
            while (!_player.IsOpen && clock.ElapsedMilliseconds < TimeoutMs)
                Pump();

            Assert.That(_player.IsOpen, Is.True);
            Assert.That(_player.FilePath, Is.EqualTo(HapTestFixtures.Hap5));
        }

        [Test]
        public void PlaybackLooped_HandlerThatDoesNothing_KeepsPlaying()
        {
            Assert.That(Open(HapTestFixtures.Hap1).Success, Is.True);

            int looped = 0;
            _player.Loop = true;
            _player.PlaybackLooped += () => looped++;
            _player.Play();

            Assert.DoesNotThrow(() => _player.TickPlayback(_player.Duration * 1.5f));

            Assert.That(looped, Is.EqualTo(1));
            Assert.That(_player.IsPlaying, Is.True);
            Assert.That(_player.IsOpen, Is.True);
        }
    }
}
