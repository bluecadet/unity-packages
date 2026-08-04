using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// The awaitable open/close lifecycle: typed results, last-call-wins, and teardown that
    /// never strands a caller waiting.
    ///
    /// Edit mode never runs MonoBehaviour Update, so these tests drive the same main-thread
    /// pump the player is ticked by at runtime — see <see cref="HapTestFixtures.Pump()"/>.
    /// </summary>
    [TestFixture]
    public class HapPlayerLifecycleTests : HapPlayerTestFixture
    {
        /// <summary>Ticks to run after a test destroys a player, to let its teardown finish.</summary>
        const int SettleTicks = 50;

        // ── Awaitable plumbing ───────────────────────────────────────────────

        [Test]
        public void CompletionSource_CompletesSynchronouslyOnTheMainThread()
        {
            var source = new AwaitableCompletionSource<int>();
            var awaiter = source.Awaitable.GetAwaiter();

            Assert.That(awaiter.IsCompleted, Is.False);
            source.SetResult(7);
            Assert.That(awaiter.IsCompleted, Is.True);
            Assert.That(awaiter.GetResult(), Is.EqualTo(7));
        }

        // ── Typed results ────────────────────────────────────────────────────

        [Test]
        public void OpenAsync_ValidFile_CompletesWithSuccess()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            OpenResult result = HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1));

            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(result.Status, Is.EqualTo(HapOpenStatus.Success));
            Assert.That(result.FilePath, Is.EqualTo(HapTestFixtures.Hap1));
            Assert.That(Player.IsOpen, Is.True);
            Assert.That(Player.Width, Is.EqualTo(HapTestFixtures.Width));
            Assert.That(Player.FrameCount, Is.GreaterThan(0));
        }

        [Test]
        public void OpenAsync_ValidFile_RaisesOpened()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            int opened = 0;
            Player.Opened += () => opened++;

            HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1));
            Assert.That(opened, Is.EqualTo(1));
        }

        [Test]
        public void OpenAsync_MissingFile_CompletesWithFileNotFound()
        {
            HapTestFixtures.ExpectOpenFailureLog();

            OpenResult result = HapTestFixtures.Await(Player.OpenAsync("/nonexistent/path/fake.mov"));

            Assert.That(result.Status, Is.EqualTo(HapOpenStatus.FileNotFound));
            Assert.That(result.Success, Is.False);
            Assert.That(Player.IsOpen, Is.False);
        }

        [Test]
        public void OpenAsync_NotAVideoFile_CompletesWithNotAVideoFile()
        {
            HapTestFixtures.ExpectOpenFailureLog();

            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "not a hap file");
                OpenResult result = HapTestFixtures.Await(Player.OpenAsync(tempFile));
                Assert.That(result.Status, Is.EqualTo(HapOpenStatus.NotAVideoFile));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Test]
        public void OpenAsync_CorruptFile_CompletesWithATypedFailure()
        {
            var inputs = HapTestFixtures.FuzzRegressions;
            Assume.That(inputs.Length, Is.GreaterThan(0), "no fuzz regression inputs found");
            LogAssert.ignoreFailingMessages = true;

            try
            {
                foreach (string path in inputs)
                {
                    OpenResult result = HapTestFixtures.Await(Player.OpenAsync(path));
                    Assert.That(Enum.IsDefined(typeof(HapOpenStatus), result.Status), Is.True,
                        $"{Path.GetFileName(path)} produced an unmapped status");
                    Assert.That(result.Status, Is.Not.EqualTo(HapOpenStatus.Superseded));

                    HapTestFixtures.Await(Player.CloseAsync());
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void OpenAsync_EmptyPath_CompletesWithInvalidPath()
        {
            OpenResult result = HapTestFixtures.Await(Player.OpenAsync(""));

            Assert.That(result.Status, Is.EqualTo(HapOpenStatus.InvalidPath));
            Assert.That(Player.IsOpen, Is.False);
        }

        [Test]
        public void OpenAsync_OffTheMainThread_IsRefused()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("main thread"));

            OpenResult result = default;
            var worker = new Thread(() => result = Player.OpenAsync(HapTestFixtures.Hap1).GetAwaiter().GetResult());
            worker.Start();
            Assert.That(worker.Join(HapTestFixtures.TimeoutMs), Is.True, "the refused call did not complete");

            Assert.That(result.Status, Is.EqualTo(HapOpenStatus.Cancelled));
        }

        // ── Supersede ────────────────────────────────────────────────────────

        [Test]
        public void OpenAsync_WhileOpening_SupersedesTheFirstCaller()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            HapTestFixtures.Require(HapTestFixtures.Hap5);

            var first = Player.OpenAsync(HapTestFixtures.Hap1);
            var second = Player.OpenAsync(HapTestFixtures.Hap5);

            OpenResult firstResult = HapTestFixtures.Await(first);
            OpenResult secondResult = HapTestFixtures.Await(second);

            Assert.That(firstResult.Status, Is.EqualTo(HapOpenStatus.Superseded));
            Assert.That(firstResult.FilePath, Is.EqualTo(HapTestFixtures.Hap1));
            Assert.That(secondResult.Success, Is.True, secondResult.ToString());
            Assert.That(Player.FilePath, Is.EqualTo(HapTestFixtures.Hap5));
        }

        [Test]
        public void OpenAsync_WhileOpen_ReplacesTheOpenFile()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            HapTestFixtures.Require(HapTestFixtures.Hap5);

            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);
            OpenResult second = HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap5));

            Assert.That(second.Success, Is.True, second.ToString());
            Assert.That(Player.IsOpen, Is.True);
            Assert.That(Player.FilePath, Is.EqualTo(HapTestFixtures.Hap5));
        }

        [Test]
        public void OpenAsync_Storm_SettlesOnTheLastFileAndCompletesEveryCaller()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            HapTestFixtures.Require(HapTestFixtures.Hap5);
            HapTestFixtures.Require(HapTestFixtures.HapY);

            var awaitables = new[]
            {
                Player.OpenAsync(HapTestFixtures.Hap1),
                Player.OpenAsync(HapTestFixtures.Hap5),
                Player.OpenAsync(HapTestFixtures.Hap1),
                Player.OpenAsync(HapTestFixtures.HapY),
            };

            for (int i = 0; i < awaitables.Length - 1; i++)
            {
                OpenResult superseded = HapTestFixtures.Await(awaitables[i]);
                Assert.That(superseded.Status, Is.EqualTo(HapOpenStatus.Superseded), $"caller {i}");
            }

            OpenResult last = HapTestFixtures.Await(awaitables[^1]);
            Assert.That(last.Success, Is.True, last.ToString());
            Assert.That(Player.FilePath, Is.EqualTo(HapTestFixtures.HapY));
            Assert.That(Player.IsOpen, Is.True);
        }

        [Test]
        public void CloseAsync_WhileOpening_SupersedesTheOpenCaller()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            var open = Player.OpenAsync(HapTestFixtures.Hap1);
            var close = Player.CloseAsync();

            OpenResult openResult = HapTestFixtures.Await(open);
            HapTestFixtures.Await(close);

            Assert.That(openResult.Status, Is.EqualTo(HapOpenStatus.Superseded));
            Assert.That(Player.IsOpen, Is.False);
            Assert.That(Player.IsClosing, Is.False);
        }

        // ── Close ordering and idempotence ───────────────────────────────────

        [Test]
        public void CloseAsync_WithNothingOpen_CompletesImmediately()
        {
            var close = Player.CloseAsync();
            Assert.That(close.GetAwaiter().IsCompleted, Is.True, "an empty close should not wait for anything");
        }

        [Test]
        public void CloseAsync_CalledTwice_BothComplete()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            var first = Player.CloseAsync();
            var second = Player.CloseAsync();

            HapTestFixtures.Await(first);
            HapTestFixtures.Await(second);
            Assert.That(Player.IsOpen, Is.False);

            // And a third, once everything has settled.
            var third = Player.CloseAsync();
            Assert.That(third.GetAwaiter().IsCompleted, Is.True);
        }

        [Test]
        public void CloseAsync_CompletesOnlyAfterTheFileIsReleased()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            var close = Player.CloseAsync();
            Assert.That(close.GetAwaiter().IsCompleted, Is.False,
                "closing an open file cannot finish before its decode thread parks");

            HapTestFixtures.Await(close);
            Assert.That(Player.IsClosing, Is.False);
            Assert.That(Player.Texture, Is.Null);
        }

        [Test]
        public void OpenAsync_DuringClose_WaitsForTheTeardown()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            HapTestFixtures.Require(HapTestFixtures.Hap5);

            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            var close = Player.CloseAsync();
            var reopen = Player.OpenAsync(HapTestFixtures.Hap5);

            Assert.That(Player.IsOpening, Is.True, "the second open should be queued behind the close");

            HapTestFixtures.Await(close);
            OpenResult reopened = HapTestFixtures.Await(reopen);

            Assert.That(reopened.Success, Is.True, reopened.ToString());
            Assert.That(Player.FilePath, Is.EqualTo(HapTestFixtures.Hap5));
        }

        // ── Destroy ──────────────────────────────────────────────────────────

        [Test]
        public void Destroy_MidOpen_CancelsTheCallerAndLeavesNothingBehind()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            var open = Player.OpenAsync(HapTestFixtures.Hap1);
            DestroyHost();

            OpenResult result = HapTestFixtures.Await(open, "destroying the player stranded the caller");
            Assert.That(result.Status, Is.EqualTo(HapOpenStatus.Cancelled));

            // Let any orphaned teardown finish; it must not log or throw.
            HapTestFixtures.Pump(SettleTicks);
        }

        [Test]
        public void Destroy_WhileOpen_ReleasesEverything()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            Assert.DoesNotThrow(DestroyHost);

            HapTestFixtures.Pump(SettleTicks);
        }

        [Test]
        public void Destroy_CloseContinuationThatReopens_IsRefused()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            HapTestFixtures.Require(HapTestFixtures.Hap5);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            // The shape a caller writes: close, then chain the next file. Here the component is
            // destroyed while the close is in flight, so the reopen must start nothing at all.
            var player = Player;
            Awaitable<OpenResult> reopen = null;
            var close = player.CloseAsync();
            close.GetAwaiter().OnCompleted(() => reopen = player.OpenAsync(HapTestFixtures.Hap5));

            DestroyHost();

            HapTestFixtures.PollUntil(() => reopen != null);
            Assert.That(reopen, Is.Not.Null, "the close continuation never ran");

            OpenResult result = HapTestFixtures.Await(reopen, "the reopen after destroy was stranded");
            Assert.That(result.Status, Is.EqualTo(HapOpenStatus.Cancelled));
            Assert.That(player.IsOpen, Is.False);
            Assert.That(player.IsOpening, Is.False, "a destroyed player started a session anyway");

            HapTestFixtures.Pump(SettleTicks);
        }

        [Test]
        public void Destroy_InEditor_CloseContinuationThatReopens_IsRefused()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            HapTestFixtures.Require(HapTestFixtures.Hap5);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            var player = Player;
            Awaitable<OpenResult> reopen = null;
            var close = player.CloseAsync();
            close.GetAwaiter().OnCompleted(() => reopen = player.OpenAsync(HapTestFixtures.Hap5));

            // The path taken when Unity never delivers OnDestroy: the shared loop notices the
            // destroyed player and abandons it.
            DestroyHost();
            player.AbandonAfterDestroy();

            HapTestFixtures.PollUntil(() => reopen != null);
            Assert.That(reopen, Is.Not.Null, "the close continuation never ran");

            OpenResult result = HapTestFixtures.Await(reopen, "the reopen after destroy was stranded");
            Assert.That(result.Status, Is.EqualTo(HapOpenStatus.Cancelled));
            Assert.That(player.IsOpening, Is.False, "an abandoned player started a session anyway");

            HapTestFixtures.Pump(SettleTicks);
        }

        [Test]
        public void Destroy_ThrowingContinuation_DoesNotStrandTheCallersBehindIt()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);
            LogAssert.ignoreFailingMessages = true;

            try
            {
                var first = Player.CloseAsync();
                var second = Player.CloseAsync();
                first.GetAwaiter().OnCompleted(() => throw new InvalidOperationException("boom"));

                DestroyHost();

                HapTestFixtures.Await(second,
                    "a throwing continuation stranded the caller queued behind it");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void Destroy_MidClose_CompletesTheCloseCaller()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            var close = Player.CloseAsync();
            DestroyHost();

            HapTestFixtures.Await(close, "destroying the player stranded a close caller");
        }

        // ── Fire-and-forget wrappers ─────────────────────────────────────────

        [Test]
        public void Open_FireAndForget_OpensAndRaisesOpened()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);

            int opened = 0;
            Player.Opened += () => opened++;
            Player.Open(HapTestFixtures.Hap1);

            Assert.That(HapTestFixtures.PollUntil(() => Player.IsOpen), Is.True);
            Assert.That(opened, Is.EqualTo(1));
        }

        [Test]
        public void Open_FireAndForget_LogsFailuresNobodyIsAwaiting()
        {
            HapTestFixtures.ExpectOpenFailureLog(LogType.Error);

            Player.Open("/nonexistent/path/fake.mov");

            Assert.That(HapTestFixtures.PollUntil(() => !Player.IsOpening), Is.True,
                "the failed open never settled");
            Assert.That(Player.IsOpen, Is.False);
        }

        [Test]
        public void Close_FireAndForget_ReleasesTheFile()
        {
            HapTestFixtures.Require(HapTestFixtures.Hap1);
            Assert.That(HapTestFixtures.Await(Player.OpenAsync(HapTestFixtures.Hap1)).Success, Is.True);

            Player.Close();

            Assert.That(HapTestFixtures.PollUntil(() => !Player.IsClosing), Is.True,
                "the close never settled");
            Assert.That(Player.IsOpen, Is.False);
        }
    }
}
