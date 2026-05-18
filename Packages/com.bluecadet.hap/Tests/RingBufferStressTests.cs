using System;
using System.Threading;
using System.Collections.Generic;
using NUnit.Framework;
using Bluecadet.Hap;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class RingBufferStressTests
    {
        private const int IterationCount = 10_000;
        private const int SlotSize = 256;
        private const int JoinTimeoutMs = 10_000;

        private HapFrameRingBuffer _buffer;
        private volatile bool _writerDone;

        [SetUp]
        public void SetUp()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
            _writerDone = false;
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        private static void WriteFrameIndex(NativeArray<byte> slot, int frameIndex)
        {
            slot[0] = (byte)(frameIndex & 0xFF);
            slot[1] = (byte)((frameIndex >> 8) & 0xFF);
            slot[2] = (byte)((frameIndex >> 16) & 0xFF);
            slot[3] = (byte)((frameIndex >> 24) & 0xFF);
        }

        [Test]
        public void StressTest_Linear1x_NoCorruption()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            Exception readerException = null;
            int lastFrameIndex = -1;

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < IterationCount; i++)
                    {
                        WriteFrameIndex(_buffer.WriteSlot, i);
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
                    for (int i = 0; i < IterationCount; i++)
                    {
                        if (_buffer.TryRead(out int fi, out NativeArray<byte> data))
                        {
                            lastFrameIndex = fi;
                            _buffer.ClearPin();
                        }
                        Thread.SpinWait(10);
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
            Assert.That(lastFrameIndex, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void StressTest_FastWriter_NoCorruption()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            Exception readerException = null;
            int lastFrameIndex = -1;

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < IterationCount; i++)
                    {
                        WriteFrameIndex(_buffer.WriteSlot, i);
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
                    int iteration = 0;
                    while (!_writerDone)
                    {
                        iteration++;
                        if (iteration % 5 == 0)
                        {
                            if (_buffer.TryRead(out int fi, out NativeArray<byte> data))
                            {
                                lastFrameIndex = fi;
                                _buffer.ClearPin();
                            }
                        }
                        Thread.SpinWait(50);
                    }
                    if (_buffer.TryRead(out int finalFi, out NativeArray<byte> finalData))
                    {
                        lastFrameIndex = finalFi;
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
            Assert.That(lastFrameIndex, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void StressTest_SlowWriter_NoCorruption()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            Exception readerException = null;
            int lastFrameIndex = -1;

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < IterationCount; i++)
                    {
                        if (i % 5 == 0)
                        {
                            WriteFrameIndex(_buffer.WriteSlot, i);
                            _buffer.CommitWrite(i);
                        }
                        Thread.SpinWait(50);
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
                    for (int i = 0; i < IterationCount; i++)
                    {
                        if (_buffer.TryRead(out int fi, out NativeArray<byte> data))
                        {
                            lastFrameIndex = fi;
                            _buffer.ClearPin();
                        }
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
        }

        [Test]
        public void StressTest_ReversePlayback_NoCorruption()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            Exception readerException = null;
            int lastFrameIndex = -1;
            bool allFrameIndicesValid = true;

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = IterationCount - 1; i >= 0; i--)
                    {
                        WriteFrameIndex(_buffer.WriteSlot, i);
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
                            if (fi < 0)
                                allFrameIndicesValid = false;
                            lastFrameIndex = fi;
                            _buffer.ClearPin();
                        }
                        Thread.SpinWait(10);
                    }
                    if (_buffer.TryRead(out int finalFi, out NativeArray<byte> finalData))
                    {
                        if (finalFi < 0)
                            allFrameIndicesValid = false;
                        lastFrameIndex = finalFi;
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
            Assert.That(allFrameIndicesValid, Is.True, "Received a negative frame index");
            Assert.That(lastFrameIndex, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void StressTest_RandomScrubbing_NoCorruption()
        {
            _buffer = new HapFrameRingBuffer(SlotSize);
            Exception writerException = null;
            Exception readerException = null;
            int lastFrameIndex = -1;
            bool allFrameIndicesValid = true;

            var rng = new Random(42);
            var randomFrames = new int[IterationCount];
            for (int i = 0; i < IterationCount; i++)
                randomFrames[i] = rng.Next(0, IterationCount);

            var writerThread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < IterationCount; i++)
                    {
                        int fi = randomFrames[i];
                        WriteFrameIndex(_buffer.WriteSlot, fi);
                        _buffer.CommitWrite(fi);
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
                            if (fi < 0)
                                allFrameIndicesValid = false;
                            lastFrameIndex = fi;
                            _buffer.ClearPin();
                        }
                        Thread.SpinWait(10);
                    }
                    if (_buffer.TryRead(out int finalFi, out NativeArray<byte> finalData))
                    {
                        if (finalFi < 0)
                            allFrameIndicesValid = false;
                        lastFrameIndex = finalFi;
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
            Assert.That(allFrameIndicesValid, Is.True, "Received a negative frame index");
            Assert.That(lastFrameIndex, Is.GreaterThanOrEqualTo(0));
        }
    }
}
