using NUnit.Framework;
using Bluecadet.Hap;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class RingBufferTests
    {
        private HapFrameRingBuffer _buffer;

        [SetUp]
        public void SetUp()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        [Test]
        public void TryRead_BeforeAnyCommit_ReturnsFalse()
        {
            _buffer = new HapFrameRingBuffer(1024);
            bool result = _buffer.TryRead(out int frameIndex, out NativeArray<byte> data);
            Assert.That(result, Is.False);
        }

        [Test]
        public void CommitWrite_ThenTryRead_ReturnsTrueWithFrameIndex()
        {
            _buffer = new HapFrameRingBuffer(1024);
            var slot = _buffer.WriteSlot;
            slot[0] = 0xFF;
            _buffer.CommitWrite(7);

            bool result = _buffer.TryRead(out int frameIndex, out NativeArray<byte> data);

            Assert.That(result, Is.True);
            Assert.That(frameIndex, Is.EqualTo(7));
        }

        [Test]
        public void CommitWrite_ThenTryRead_DataMatchesWritten()
        {
            _buffer = new HapFrameRingBuffer(1024);
            var slot = _buffer.WriteSlot;
            slot[0] = 1;
            slot[1] = 2;
            slot[2] = 3;
            slot[3] = 4;
            _buffer.CommitWrite(0);

            _buffer.TryRead(out int frameIndex, out NativeArray<byte> data);

            Assert.That(data[0], Is.EqualTo(1));
            Assert.That(data[1], Is.EqualTo(2));
            Assert.That(data[2], Is.EqualTo(3));
            Assert.That(data[3], Is.EqualTo(4));
        }

        [Test]
        public void MultipleCommits_TryRead_ReturnsLatestFrame()
        {
            _buffer = new HapFrameRingBuffer(1024);

            var s0 = _buffer.WriteSlot; s0[0] = 0;
            _buffer.CommitWrite(0);

            var s1 = _buffer.WriteSlot; s1[0] = 1;
            _buffer.CommitWrite(1);

            var s2 = _buffer.WriteSlot; s2[0] = 2;
            _buffer.CommitWrite(2);

            bool result = _buffer.TryRead(out int frameIndex, out NativeArray<byte> data);

            Assert.That(result, Is.True);
            Assert.That(frameIndex, Is.EqualTo(2));
        }

        [Test]
        public void TryRead_PinsSlot_CommitWriteSkipsPinned()
        {
            _buffer = new HapFrameRingBuffer(1024);

            var s = _buffer.WriteSlot; s[0] = 0;
            _buffer.CommitWrite(0);

            bool firstRead = _buffer.TryRead(out int firstFrameIndex, out NativeArray<byte> pinnedData);
            Assert.That(firstRead, Is.True);

            for (int i = 1; i <= 4; i++)
            {
                var si = _buffer.WriteSlot; si[0] = (byte)i;
                _buffer.CommitWrite(i);

                bool readResult = _buffer.TryRead(out int fi, out NativeArray<byte> d);
                Assert.That(readResult, Is.True, $"TryRead failed after CommitWrite({i})");
            }
        }

        [Test]
        public void ClearPin_ReleasesSlotForWriter()
        {
            _buffer = new HapFrameRingBuffer(1024);

            var s = _buffer.WriteSlot; s[0] = 0;
            _buffer.CommitWrite(0);

            _buffer.TryRead(out int frameIndex, out NativeArray<byte> data);
            _buffer.ClearPin();

            for (int i = 1; i <= 4; i++)
            {
                var si = _buffer.WriteSlot; si[0] = (byte)i;
                Assert.DoesNotThrow(() => _buffer.CommitWrite(i));
            }
        }

        [Test]
        public void Dispose_CalledOnce_DoesNotThrow()
        {
            var buf = new HapFrameRingBuffer(1024);
            Assert.DoesNotThrow(() => buf.Dispose());
            _buffer = null;
        }

        [Test]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var buf = new HapFrameRingBuffer(1024);
            buf.Dispose();
            Assert.DoesNotThrow(() => buf.Dispose());
            _buffer = null;
        }

        [Test]
        public void Dispose_NativeArraysFreed()
        {
            var buf = new HapFrameRingBuffer(1024);
            buf.Dispose();
            _buffer = null;
        }

        [Test]
        public void SlotSize_ReflectsConstructorArg()
        {
            _buffer = new HapFrameRingBuffer(512);
            Assert.That(_buffer.SlotSize, Is.EqualTo(512));
        }

        [Test]
        public void TryAcquire_BeforeAnyCommit_ReturnsFalse()
        {
            _buffer = new HapFrameRingBuffer(1024);
            bool result = _buffer.TryAcquire(out var lease);
            Assert.That(result, Is.False);
        }

        [Test]
        public void TryAcquire_AfterCommit_ReturnsLeaseWithCorrectFrame()
        {
            _buffer = new HapFrameRingBuffer(1024);
            var s = _buffer.WriteSlot; s[0] = 0xFF;
            _buffer.CommitWrite(7);

            bool result = _buffer.TryAcquire(out var lease);
            Assert.That(result, Is.True);
            Assert.That(lease.FrameIndex, Is.EqualTo(7));
            lease.Dispose();
        }

        [Test]
        public void TryAcquire_Dispose_ClearsPin()
        {
            _buffer = new HapFrameRingBuffer(1024);
            var s = _buffer.WriteSlot; s[0] = 1;
            _buffer.CommitWrite(0);

            _buffer.TryAcquire(out var lease);
            lease.Dispose();

            // After dispose, slots should be freely reusable
            Assert.DoesNotThrow(() =>
            {
                for (int i = 1; i <= 4; i++)
                {
                    var si = _buffer.WriteSlot; si[0] = (byte)i;
                    _buffer.CommitWrite(i);
                }
            });
        }

        [Test]
        public void TryAcquire_DefaultLease_DisposeDoesNotThrow()
        {
            var lease = default(HapFrameLease);
            Assert.DoesNotThrow(() => lease.Dispose());
        }
    }
}
