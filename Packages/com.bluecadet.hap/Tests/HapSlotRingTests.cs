using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// The writer/reader slot protocol the decode thread and the main thread share.
    /// Buffers are supplied by the test (a byte array per slot) so the protocol can be
    /// hammered without allocating GPU textures.
    /// </summary>
    [TestFixture]
    public class HapSlotRingTests
    {
        static HapSlotRing NewRing(int retireDepth = 2) =>
            new HapSlotRing(HapSlotRing.SlotCountFor(retireDepth), retireDepth);

        // ── Basic protocol ───────────────────────────────────────────────────

        [Test]
        public void TryPin_BeforeAnyCommit_ReturnsFalse()
        {
            var ring = NewRing();
            Assert.That(ring.TryPin(out int slot, out int frame), Is.False);
            Assert.That(slot, Is.EqualTo(-1));
            Assert.That(frame, Is.EqualTo(-1));
        }

        [Test]
        public void CommitWrite_ThenTryPin_ReturnsCommittedFrame()
        {
            var ring = NewRing();
            int slot = ring.BeginWrite();
            ring.CommitWrite(slot, 7);

            Assert.That(ring.TryPin(out int pinned, out int frame), Is.True);
            Assert.That(pinned, Is.EqualTo(slot));
            Assert.That(frame, Is.EqualTo(7));
        }

        [Test]
        public void MultipleCommits_TryPin_ReturnsNewestFrame()
        {
            var ring = NewRing();
            for (int i = 0; i < 10; i++)
                ring.CommitWrite(ring.BeginWrite(), i);

            Assert.That(ring.TryPin(out _, out int frame), Is.True);
            Assert.That(frame, Is.EqualTo(9));
        }

        [Test]
        public void BeginWrite_NeverReturnsThePublishedSlot()
        {
            var ring = NewRing();
            ring.CommitWrite(ring.BeginWrite(), 0);

            for (int i = 1; i < 50; i++)
            {
                // Where the newest frame lives, as the reader sees it — then let the pin go, so
                // the only thing keeping the writer out of that slot is the publication itself.
                Assert.That(ring.TryPin(out int published, out int frame), Is.True);
                Assert.That(frame, Is.EqualTo(i - 1));
                ring.ClearPin();

                int slot = ring.BeginWrite();
                Assert.That(slot, Is.Not.EqualTo(published),
                    "writer picked the slot holding the newest committed frame");
                ring.CommitWrite(slot, i);
            }
        }

        [Test]
        public void BeginWrite_NeverReturnsThePinnedSlot()
        {
            var ring = NewRing();
            ring.CommitWrite(ring.BeginWrite(), 0);
            Assert.That(ring.TryPin(out int pinned, out _), Is.True);

            for (int i = 1; i < 50; i++)
            {
                int slot = ring.BeginWrite();
                Assert.That(slot, Is.Not.EqualTo(pinned), "writer picked the pinned slot");
                ring.CommitWrite(slot, i);
            }
        }

        [Test]
        public void CommitWrite_ManyTimesWithoutReading_DoesNotThrow()
        {
            var ring = NewRing();
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 1000; i++)
                    ring.CommitWrite(ring.BeginWrite(), i);
            });
            Assert.That(ring.TryPin(out _, out int frame), Is.True);
            Assert.That(frame, Is.EqualTo(999));
        }

        [Test]
        public void TryPin_CalledTwice_ReturnsSameSlot()
        {
            var ring = NewRing();
            ring.CommitWrite(ring.BeginWrite(), 42);

            ring.TryPin(out int slot1, out int frame1);
            ring.TryPin(out int slot2, out int frame2);

            Assert.That(slot2, Is.EqualTo(slot1));
            Assert.That(frame2, Is.EqualTo(frame1));
        }

        [Test]
        public void ClearPin_WhenNotPinned_DoesNotThrow()
        {
            var ring = NewRing();
            Assert.DoesNotThrow(() => ring.ClearPin());
        }

        [Test]
        public void ClearPin_ReleasesSlotForTheWriter()
        {
            var ring = NewRing(retireDepth: 0);
            ring.CommitWrite(ring.BeginWrite(), 0);
            ring.TryPin(out int pinned, out _);
            ring.ClearPin();

            // With the pin gone and no retire window, the slot comes back around.
            bool reused = false;
            for (int i = 1; i <= ring.SlotCount * 2 && !reused; i++)
            {
                int slot = ring.BeginWrite();
                reused = slot == pinned;
                ring.CommitWrite(slot, i);
            }
            Assert.That(reused, Is.True, "the unpinned slot was never handed back to the writer");
        }

        // ── Retire window ────────────────────────────────────────────────────

        [Test]
        public void MarkUploaded_KeepsSlotOutOfRotation_ForRetireDepthUploads()
        {
            const int retireDepth = 2;
            var ring = NewRing(retireDepth);

            int uploaded = ring.BeginWrite();
            ring.CommitWrite(uploaded, 0);
            ring.TryPin(out int pinned, out _);
            Assert.That(pinned, Is.EqualTo(uploaded));
            ring.MarkUploaded(pinned);
            ring.ClearPin();

            // Until retireDepth further uploads have happened, the GPU may still be reading
            // that slot, so the writer must not take it.
            for (int i = 1; i <= retireDepth; i++)
            {
                int slot = ring.BeginWrite();
                Assert.That(slot, Is.Not.EqualTo(uploaded), $"slot reused only {i} upload(s) later");
                ring.CommitWrite(slot, i);
                ring.TryPin(out int next, out _);
                ring.MarkUploaded(next);
                ring.ClearPin();
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void SlotCountFor_SurvivesThePlaybackInterleaving(int retireDepth)
        {
            var ring = NewRing(retireDepth);
            int reused = RunPlaybackInterleaving(ring, retireDepth, out _);

            Assert.That(reused, Is.Zero,
                $"retireDepth {retireDepth}: the writer took back a slot the GPU may still be " +
                $"reading — the ring ran out of slots and gave up the GPU-lag margin");
        }

        [Test]
        public void UndersizedRing_HandsBackSlotsTheGpuMayStillBeReading()
        {
            // The other side of the sizing rule, and the reason SlotCountFor exists: a ring
            // built too small for its retire depth cannot honour the margin, and gives it up
            // rather than stalling the decode thread. The margin is the only thing it drops —
            // it still never hands back the published or the pinned slot.
            const int retireDepth = 4;
            var ring = new HapSlotRing(HapSlotRing.MinSlotCount, retireDepth);

            int reused = RunPlaybackInterleaving(ring, retireDepth, out int pinnedCollisions);

            Assert.That(reused, Is.GreaterThan(0),
                "an undersized ring somehow kept the whole retire window free");
            Assert.That(pinnedCollisions, Is.Zero, "the writer took the slot being uploaded");
        }

        /// <summary>
        /// Drive a ring through the order playback actually runs in, which a strict
        /// write/commit/pin/upload/unpin lockstep does not reach: the main thread pins at the
        /// top of a present, and the decode thread both commits the frame that was asked for
        /// and grabs its prefetch slot before that present marks its upload. That leaves the
        /// published slot, the pinned slot and the whole retire window excluded at the same
        /// time.
        ///
        /// Counts how often the writer was handed a slot the test had recently uploaded (so
        /// the GPU may still be reading it) and, separately, the slot being uploaded right now.
        /// Both are counted from calls the test made itself, so nothing here depends on how the
        /// ring tracks them.
        /// </summary>
        static int RunPlaybackInterleaving(HapSlotRing ring, int retireDepth, out int pinnedCollisions)
        {
            var window = new RetireWindow(retireDepth);
            int reused = 0;
            int collisions = 0;
            int frame = 0;

            ring.CommitWrite(ring.BeginWrite(), frame++);

            for (int cycle = 0; cycle < 500; cycle++)
            {
                // Main thread, top of the present: take the newest frame.
                bool pinned = ring.TryPin(out int pinnedSlot, out _);

                // Decode thread: the frame the main thread asked for...
                int explicitSlot = TakeWriteSlot();
                ring.CommitWrite(explicitSlot, frame++);

                // ...and the one-frame-ahead prefetch, still before the present has finished.
                int prefetchSlot = TakeWriteSlot();

                // Main thread, bottom of the present: the frame is on the GPU now.
                if (pinned)
                {
                    ring.MarkUploaded(pinnedSlot);
                    window.Uploaded(pinnedSlot);
                    ring.ClearPin();
                }

                ring.CommitWrite(prefetchSlot, frame++);

                int TakeWriteSlot()
                {
                    int slot = ring.BeginWrite();
                    if (window.Contains(slot)) reused++;
                    if (pinned && slot == pinnedSlot) collisions++;
                    return slot;
                }
            }

            pinnedCollisions = collisions;
            return reused;
        }

        [Test]
        public void SlotCountFor_LeavesRoomForEveryExclusion()
        {
            for (int retireDepth = 0; retireDepth <= 8; retireDepth++)
            {
                // Published + pinned + the retire window (this upload and the retireDepth
                // before it), and one slot left over to actually decode into.
                Assert.That(HapSlotRing.SlotCountFor(retireDepth),
                    Is.GreaterThanOrEqualTo(retireDepth + 1 + 3),
                    $"retireDepth {retireDepth}");
            }
        }

        [Test]
        public void Constructor_ClampsSlotCountToTheMinimum()
        {
            var ring = new HapSlotRing(1, 0);
            Assert.That(ring.SlotCount, Is.EqualTo(HapSlotRing.MinSlotCount));

            // Even starved of slots, the writer never hands back the published or pinned one.
            ring.CommitWrite(ring.BeginWrite(), 0);
            ring.TryPin(out int pinned, out _);
            for (int i = 1; i < 20; i++)
            {
                int slot = ring.BeginWrite();
                Assert.That(slot, Is.Not.EqualTo(pinned));
                ring.CommitWrite(slot, i);
            }
        }

        // ── Concurrency ──────────────────────────────────────────────────────

        [Test]
        public void WriterAndReader_UnderLoad_NeverSeeATornSlot()
        {
            const int iterations = 20_000;
            var ring = NewRing();
            var buffers = NewBuffers(ring.SlotCount);
            Exception writerException = null;
            Exception readerException = null;
            bool corrupted = false;
            bool writerDone = false;
            int framesRead = 0;

            var writer = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        int slot = ring.BeginWrite();
                        FillBuffer(buffers[slot], i);
                        ring.CommitWrite(slot, i);
                    }
                }
                catch (Exception ex) { writerException = ex; }
                finally { Volatile.Write(ref writerDone, true); }
            });

            var reader = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref writerDone))
                        ReadOnce();
                    ReadOnce();
                }
                catch (Exception ex) { readerException = ex; }

                void ReadOnce()
                {
                    if (!ring.TryPin(out int slot, out int frame)) return;
                    // The pin must hold the slot still for as long as we look at it.
                    for (int spin = 0; spin < 4; spin++)
                    {
                        if (!BufferHolds(buffers[slot], frame)) corrupted = true;
                        Thread.SpinWait(20);
                    }
                    ring.MarkUploaded(slot);
                    ring.ClearPin();
                    framesRead++;
                }
            });

            writer.Start();
            reader.Start();
            Assert.That(writer.Join(HapTestFixtures.TimeoutMs), Is.True, "writer timed out");
            Assert.That(reader.Join(HapTestFixtures.TimeoutMs), Is.True, "reader timed out");

            Assert.That(writerException, Is.Null, $"writer threw: {writerException}");
            Assert.That(readerException, Is.Null, $"reader threw: {readerException}");
            Assert.That(corrupted, Is.False, "a pinned slot was overwritten while it was being read");
            Assert.That(framesRead, Is.GreaterThan(0), "reader never saw a frame");
        }

        [Test]
        public void ReaderThatNeverClearsItsPin_DoesNotStallTheWriter()
        {
            const int iterations = 20_000;
            var ring = NewRing();
            bool writerDone = false;
            Exception writerException = null;

            var writer = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                        ring.CommitWrite(ring.BeginWrite(), i);
                }
                catch (Exception ex) { writerException = ex; }
                finally { Volatile.Write(ref writerDone, true); }
            });

            var reader = new Thread(() =>
            {
                while (!Volatile.Read(ref writerDone))
                    ring.TryPin(out _, out _);
            });

            writer.Start();
            reader.Start();
            Assert.That(writer.Join(HapTestFixtures.TimeoutMs), Is.True, "writer stalled behind a pin that is never released");
            Assert.That(reader.Join(HapTestFixtures.TimeoutMs), Is.True, "reader timed out");
            Assert.That(writerException, Is.Null, $"writer threw: {writerException}");
        }

        [Test]
        public void ReaderThatOnlyEverUploads_NeverStarvesTheWriter()
        {
            // The playback shape: the main thread uploads every frame it sees, so every slot
            // spends time inside a retire window.
            const int iterations = 20_000;
            var ring = NewRing();
            bool writerDone = false;
            Exception writerException = null;

            var writer = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                        ring.CommitWrite(ring.BeginWrite(), i);
                }
                catch (Exception ex) { writerException = ex; }
                finally { Volatile.Write(ref writerDone, true); }
            });

            var reader = new Thread(() =>
            {
                while (!Volatile.Read(ref writerDone))
                {
                    if (!ring.TryPin(out int slot, out _)) continue;
                    ring.MarkUploaded(slot);
                    ring.ClearPin();
                }
            });

            writer.Start();
            reader.Start();
            Assert.That(writer.Join(HapTestFixtures.TimeoutMs), Is.True, "writer stalled");
            Assert.That(reader.Join(HapTestFixtures.TimeoutMs), Is.True, "reader timed out");
            Assert.That(writerException, Is.Null, $"writer threw: {writerException}");
        }

        [Test]
        public void ScrubbingFramePattern_KeepsFrameIndicesConsistent()
        {
            var ring = NewRing();
            var buffers = NewBuffers(ring.SlotCount);
            var rng = new Random(1234);

            for (int i = 0; i < 5000; i++)
            {
                int frame = rng.Next(0, 600);
                int slot = ring.BeginWrite();
                FillBuffer(buffers[slot], frame);
                ring.CommitWrite(slot, frame);

                if (!ring.TryPin(out int pinned, out int pinnedFrame)) continue;
                Assert.That(BufferHolds(buffers[pinned], pinnedFrame), Is.True,
                    "the pinned slot's contents do not match its frame index");
                ring.MarkUploaded(pinned);
                ring.ClearPin();
            }
        }

        // ── Diagnostics ──────────────────────────────────────────────────────
        //
        // These pin the implementation rather than the contract: they read the counter the ring
        // keeps of writes that gave up the retire margin, which no caller can observe. The
        // behaviour they cover is asserted observably above; what they add is a direct reading
        // of the ring's own account of it, so a sizing regression says so in one number instead
        // of showing up as a slot the GPU may still be reading.

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void CorrectlySizedRing_NeverCountsAFallbackWrite(int retireDepth)
        {
            var ring = NewRing(retireDepth);
            RunPlaybackInterleaving(ring, retireDepth, out _);

            Assert.That(ring.FallbackWrites, Is.Zero,
                $"retireDepth {retireDepth}: the ring ran out of slots and gave up the GPU-lag margin");
        }

        [Test]
        public void UndersizedRing_CountsItsFallbackWrites()
        {
            const int retireDepth = 4;
            var ring = new HapSlotRing(HapSlotRing.MinSlotCount, retireDepth);
            RunPlaybackInterleaving(ring, retireDepth, out _);

            Assert.That(ring.FallbackWrites, Is.GreaterThan(0));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// The test's own account of which slots the GPU may still be reading: the slots of the
        /// last <c>retireDepth + 1</c> uploads the test performed. Everything it knows comes
        /// from calls the test made itself, so it can tell whether the ring handed a slot back
        /// too early without asking the ring anything.
        /// </summary>
        sealed class RetireWindow
        {
            readonly int _depth;
            readonly Queue<int> _recent = new();

            public RetireWindow(int retireDepth) => _depth = retireDepth;

            public void Uploaded(int slot)
            {
                _recent.Enqueue(slot);
                while (_recent.Count > _depth + 1) _recent.Dequeue();
            }

            public bool Contains(int slot) => _recent.Contains(slot);
        }

        static byte[][] NewBuffers(int slotCount)
        {
            var buffers = new byte[slotCount][];
            for (int i = 0; i < slotCount; i++) buffers[i] = new byte[64];
            return buffers;
        }

        /// <summary>Stamp the frame index across the whole buffer so a partial overwrite shows.</summary>
        static void FillBuffer(byte[] buffer, int frameIndex)
        {
            for (int i = 0; i < buffer.Length; i += 4)
            {
                buffer[i + 0] = (byte)frameIndex;
                buffer[i + 1] = (byte)(frameIndex >> 8);
                buffer[i + 2] = (byte)(frameIndex >> 16);
                buffer[i + 3] = (byte)(frameIndex >> 24);
            }
        }

        static bool BufferHolds(byte[] buffer, int frameIndex)
        {
            for (int i = 0; i < buffer.Length; i += 4)
            {
                int stored = buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16) | (buffer[i + 3] << 24);
                if (stored != frameIndex) return false;
            }
            return true;
        }
    }
}
