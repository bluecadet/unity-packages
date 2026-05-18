using System;
using System.Threading;
using NUnit.Framework;
using Bluecadet.Hap;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class RingBufferAdversarialTests
    {
        private const int SlotSize = 64;
        private const int JoinTimeoutMs = 10_000;

        private HapFrameRingBuffer _buffer;
        private volatile bool _writerDone;
        private volatile bool _dataCorrupted;

        [SetUp]
        public void SetUp()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
            _writerDone = false;
            _dataCorrupted = false;
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        private static void WriteFrameIndex(NativeArray<byte> slot, int fi)
        {
            slot[0] = (byte)(fi & 0xFF);
            slot[1] = (byte)((fi >> 8) & 0xFF);
            slot[2] = (byte)((fi >> 16) & 0xFF);
            slot[3] = (byte)((fi >> 24) & 0xFF);
        }

        private static int ReadFrameIndex(NativeArray<byte> data)
        {
            return data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Single-threaded adversarial
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void CommitWrite_ManyTimesWithoutRead_DoesNotDeadlock()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    var s = _buffer.WriteSlot;
                    s[0] = (byte)(i & 0xFF);
                    _buffer.CommitWrite(i);
                }
            });

            bool result = _buffer.TryRead(out int frameIndex, out NativeArray<byte> data);
            Assert.That(result, Is.True);
            Assert.That(frameIndex, Is.EqualTo(99));
        }

        [Test]
        public void TryRead_CalledTwice_ReturnsSameSlot()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);

            var s = _buffer.WriteSlot;
            s[0] = 0xAB;
            _buffer.CommitWrite(42);

            bool first = _buffer.TryRead(out int fi1, out NativeArray<byte> d1);
            bool second = _buffer.TryRead(out int fi2, out NativeArray<byte> d2);

            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
            Assert.That(fi1, Is.EqualTo(42));
            Assert.That(fi2, Is.EqualTo(42));
        }

        [Test]
        public void ClearPin_WhenNotPinned_DoesNotThrow()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);
            Assert.DoesNotThrow(() => _buffer.ClearPin());
        }

        [Test]
        public void ClearPin_AfterTryRead_AllowsSlotReuse()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);

            var s = _buffer.WriteSlot;
            s[0] = 0;
            _buffer.CommitWrite(0);

            _buffer.TryRead(out int _, out NativeArray<byte> _);
            _buffer.ClearPin();

            Assert.DoesNotThrow(() =>
            {
                for (int i = 1; i <= 10; i++)
                {
                    var si = _buffer.WriteSlot;
                    si[0] = (byte)(i & 0xFF);
                    _buffer.CommitWrite(i);
                }
            });
        }

        [Test]
        public void CommitWrite_SameFrameIndexRepeatedly_NoCorruption()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);

            for (int i = 0; i < 20; i++)
            {
                var s = _buffer.WriteSlot;
                s[0] = 7;
                _buffer.CommitWrite(7);

                bool result = _buffer.TryRead(out int frameIndex, out NativeArray<byte> data);
                Assert.That(result, Is.True, $"TryRead returned false on iteration {i}");
                Assert.That(frameIndex, Is.EqualTo(7), $"Wrong frame index on iteration {i}");
                _buffer.ClearPin();
            }
        }

        [Test]
        public void LargeNumberOfWriteReadCycles_NoLeak()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var s = _buffer.WriteSlot;
                    WriteFrameIndex(s, i);
                    _buffer.CommitWrite(i);

                    if (_buffer.TryRead(out int _, out NativeArray<byte> _))
                        _buffer.ClearPin();
                }
            });

            _buffer.Dispose();
            _buffer = null;
        }

        [Test]
        public void TryRead_WithoutClearPin_NextTryReadGetsLatest()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);

            var s0 = _buffer.WriteSlot;
            WriteFrameIndex(s0, 0);
            _buffer.CommitWrite(0);

            bool firstRead = _buffer.TryRead(out int firstFi, out NativeArray<byte> _);
            Assert.That(firstRead, Is.True);
            Assert.That(firstFi, Is.EqualTo(0));

            for (int i = 1; i <= 4; i++)
            {
                var si = _buffer.WriteSlot;
                WriteFrameIndex(si, i);
                _buffer.CommitWrite(i);
            }

            bool secondRead = _buffer.TryRead(out int secondFi, out NativeArray<byte> _);
            Assert.That(secondRead, Is.True);
            Assert.That(secondFi, Is.GreaterThan(0), "Second TryRead should return a fresher frame than 0");
        }

        [Test]
        public void ReaderNeverClearsPin_WriterStillMakesProgress()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);

            var s0 = _buffer.WriteSlot;
            WriteFrameIndex(s0, 0);
            _buffer.CommitWrite(0);

            _buffer.TryRead(out int _, out NativeArray<byte> _);

            Assert.DoesNotThrow(() =>
            {
                for (int i = 1; i <= 20; i++)
                {
                    var si = _buffer.WriteSlot;
                    WriteFrameIndex(si, i);
                    _buffer.CommitWrite(i);
                }
            });

            bool result = _buffer.TryRead(out int frameIndex, out NativeArray<byte> _);
            Assert.That(result, Is.True);
            Assert.That(frameIndex, Is.GreaterThanOrEqualTo(1));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Concurrent adversarial
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void StressTest_DataIntegrity_NoByteCorruption()
        {
            const int iterations = 5000;
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            Exception readerException = null;

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        var s = _buffer.WriteSlot;
                        WriteFrameIndex(s, i);
                        _buffer.CommitWrite(i);
                    }
                }
                catch (Exception ex)
                {
                    writerException = ex;
                }
                finally
                {
                    _writerDone = true;
                }
            });

            var readerThread = new Thread(() =>
            {
                try
                {
                    while (!_writerDone)
                    {
                        if (_buffer.TryRead(out int fi, out NativeArray<byte> data))
                        {
                            int decoded = ReadFrameIndex(data);
                            if (decoded != fi)
                                _dataCorrupted = true;
                            _buffer.ClearPin();
                        }
                    }
                    if (_buffer.TryRead(out int finalFi, out NativeArray<byte> finalData))
                    {
                        int decoded = ReadFrameIndex(finalData);
                        if (decoded != finalFi)
                            _dataCorrupted = true;
                        _buffer.ClearPin();
                    }
                }
                catch (Exception ex)
                {
                    readerException = ex;
                }
            });

            try
            {
                writerThread.Start();
                readerThread.Start();

                Assert.That(writerThread.Join(JoinTimeoutMs), Is.True, "Writer thread timed out");
                Assert.That(readerThread.Join(JoinTimeoutMs), Is.True, "Reader thread timed out");
            }
            finally
            {
                writerThread.Join(JoinTimeoutMs);
                readerThread.Join(JoinTimeoutMs);
            }

            Assert.That(writerException, Is.Null, $"Writer threw: {writerException}");
            Assert.That(readerException, Is.Null, $"Reader threw: {readerException}");
            Assert.That(_dataCorrupted, Is.False, "Data corruption detected: decoded frame index did not match returned frame index");
        }

        [Test]
        public void StressTest_ReaderNeverClearsPin_WriterDoesNotDeadlock()
        {
            const int iterations = 5000;
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        var s = _buffer.WriteSlot;
                        WriteFrameIndex(s, i);
                        _buffer.CommitWrite(i);
                    }
                }
                catch (Exception ex)
                {
                    writerException = ex;
                }
                finally
                {
                    _writerDone = true;
                }
            });

            var readerThread = new Thread(() =>
            {
                while (!_writerDone)
                {
                    _buffer.TryRead(out int _, out NativeArray<byte> _);
                }
            });

            try
            {
                writerThread.Start();
                readerThread.Start();

                Assert.That(writerThread.Join(JoinTimeoutMs), Is.True, "Writer thread timed out — possible deadlock when reader never clears pin");
                Assert.That(readerThread.Join(JoinTimeoutMs), Is.True, "Reader thread timed out");
            }
            finally
            {
                writerThread.Join(JoinTimeoutMs);
                readerThread.Join(JoinTimeoutMs);
            }

            Assert.That(writerException, Is.Null, $"Writer threw: {writerException}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Shutdown-safety tests (mirrors Unity editor-quit scenario)
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Dispose_WhileWriterRunning_DoesNotCrash()
        {
            // Mirrors HapPlayer.Close() being called while the decode thread is
            // mid-write — the sequence that causes the editor quit crash popup.
            const int iterations = 500;
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            var writeStarted = new ManualResetEventSlim(false);

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        if (i == 10) writeStarted.Set();
                        var s = _buffer.WriteSlot;
                        WriteFrameIndex(s, i);
                        _buffer.CommitWrite(i);
                        Thread.SpinWait(5);
                    }
                }
                catch (Exception ex)
                {
                    writerException = ex;
                }
            });

            writerThread.Start();
            writeStarted.Wait();

            // Dispose while writer is still looping — must not crash or hang
            Assert.DoesNotThrow(() =>
            {
                _buffer.Dispose();
                _buffer = null;
            });

            writerThread.Join(JoinTimeoutMs);
            writeStarted.Dispose();

            // Writer may throw after Dispose — that's acceptable;
            // the important thing is the main thread didn't crash or hang.
        }

        [Test]
        public void Dispose_AfterWriterExits_DoesNotThrow()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);
            var writerDone = new ManualResetEventSlim(false);

            var writerThread = new Thread(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    var s = _buffer.WriteSlot;
                    WriteFrameIndex(s, i);
                    _buffer.CommitWrite(i);
                }
                writerDone.Set();
            });

            writerThread.Start();
            writerDone.Wait(JoinTimeoutMs);
            writerThread.Join(JoinTimeoutMs);
            writerDone.Dispose();

            Assert.DoesNotThrow(() =>
            {
                _buffer.Dispose();
                _buffer = null;
            });
        }

        [Test]
        public void StressTest_BothThreadsAtMaxSpeed_NoException()
        {
            const int iterations = 20_000;
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            Exception readerException = null;

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        var s = _buffer.WriteSlot;
                        WriteFrameIndex(s, i);
                        _buffer.CommitWrite(i);
                    }
                }
                catch (Exception ex)
                {
                    writerException = ex;
                }
                finally
                {
                    _writerDone = true;
                }
            });

            var readerThread = new Thread(() =>
            {
                try
                {
                    while (!_writerDone)
                    {
                        if (_buffer.TryRead(out int _, out NativeArray<byte> _))
                            _buffer.ClearPin();
                    }
                    if (_buffer.TryRead(out int _, out NativeArray<byte> _))
                        _buffer.ClearPin();
                }
                catch (Exception ex)
                {
                    readerException = ex;
                }
            });

            try
            {
                writerThread.Start();
                readerThread.Start();

                Assert.That(writerThread.Join(JoinTimeoutMs), Is.True, "Writer thread timed out");
                Assert.That(readerThread.Join(JoinTimeoutMs), Is.True, "Reader thread timed out");
            }
            finally
            {
                writerThread.Join(JoinTimeoutMs);
                readerThread.Join(JoinTimeoutMs);
            }

            Assert.That(writerException, Is.Null, $"Writer threw: {writerException}");
            Assert.That(readerException, Is.Null, $"Reader threw: {readerException}");
        }
    }
}
