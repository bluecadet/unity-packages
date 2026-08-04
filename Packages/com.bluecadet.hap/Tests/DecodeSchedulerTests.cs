using NUnit.Framework;
using Bluecadet.Hap;

namespace Bluecadet.Hap.Tests
{
    [TestFixture]
    public class DecodeSchedulerTests
    {
        const int FrameCount = 30;

        [Test]
        public void Next_InitialState_DecodesRequestedFrame()
        {
            var s = new DecodeScheduler(FrameCount);
            var request = s.Next(5, 1);
            Assert.That(request.Kind, Is.EqualTo(DecodeRequestKind.Decode));
            Assert.That(request.Frame, Is.EqualTo(5));
            Assert.That(request.IsPrefetch, Is.False);
        }

        [Test]
        public void Next_AfterDecode_PrefetchesNextFrame()
        {
            var s = new DecodeScheduler(FrameCount);
            s.Next(0, 1);                // decide to decode 0
            s.OnDecoded(0, false, 1);    // decode done

            var request = s.Next(0, 1);  // main still wants 0
            Assert.That(request.Kind, Is.EqualTo(DecodeRequestKind.Prefetch));
            Assert.That(request.Frame, Is.EqualTo(1));
            Assert.That(request.IsPrefetch, Is.True);
        }

        [Test]
        public void Next_AfterPrefetch_Waits()
        {
            var s = new DecodeScheduler(FrameCount);
            s.Next(0, 1);
            s.OnDecoded(0, false, 1);
            var prefetch = s.Next(0, 1);
            s.OnDecoded(prefetch.Frame, true, 1);

            var request = s.Next(0, 1);  // main still wants 0
            Assert.That(request.Kind, Is.EqualTo(DecodeRequestKind.Wait));
        }

        [Test]
        public void Next_PrefetchedFrameRequested_ReturnsAlreadyBuffered()
        {
            var s = new DecodeScheduler(FrameCount);
            s.Next(0, 1);
            s.OnDecoded(0, false, 1);      // explicit decode of 0
            s.Next(0, 1);                  // prefetch decision
            s.OnDecoded(1, true, 1);       // prefetch frame 1

            var request = s.Next(1, 1);    // main now requests 1
            Assert.That(request.Kind, Is.EqualTo(DecodeRequestKind.Skip));  // already in buffer
        }

        [Test]
        public void Next_PrefetchedFrameRequested_Sequential_EnablesNextPrefetch()
        {
            var s = new DecodeScheduler(FrameCount);
            s.Next(0, 1);
            s.OnDecoded(0, false, 1);
            s.Next(0, 1);
            s.OnDecoded(1, true, 1);
            s.Next(1, 1);  // already buffered — sets lastExplicit=1, prefetchDone=false

            var request = s.Next(1, 1);  // should now prefetch frame 2
            Assert.That(request.Frame, Is.EqualTo(2));
            Assert.That(request.IsPrefetch, Is.True);
        }

        [Test]
        public void Next_SeekAfterSequential_DisablesPrefetch()
        {
            var s = new DecodeScheduler(FrameCount);
            s.Next(0, 1);
            s.OnDecoded(0, false, 1);  // decoded 0, lastExplicit=0

            // Seek to frame 15 (non-sequential)
            var seek = s.Next(15, 1);
            Assert.That(seek.Kind, Is.EqualTo(DecodeRequestKind.Decode));
            Assert.That(seek.Frame, Is.EqualTo(15));
            Assert.That(seek.IsPrefetch, Is.False);
            s.OnDecoded(15, false, 1);  // wasSeq = false → prefetchDone = true

            var request = s.Next(15, 1);  // no prefetch because seek
            Assert.That(request.Kind, Is.EqualTo(DecodeRequestKind.Wait));
        }

        [Test]
        public void Next_ReversePlayback_PrefetchesPreviousFrame()
        {
            var s = new DecodeScheduler(FrameCount);
            s.Next(10, -1);
            s.OnDecoded(10, false, -1);

            var request = s.Next(10, -1);
            Assert.That(request.Frame, Is.EqualTo(9));  // previous frame in reverse
            Assert.That(request.IsPrefetch, Is.True);
        }

        [Test]
        public void Next_WrapAround_Forward_PrefetchesFrame0()
        {
            var s = new DecodeScheduler(FrameCount);
            s.Next(FrameCount - 1, 1);
            s.OnDecoded(FrameCount - 1, false, 1);

            var request = s.Next(FrameCount - 1, 1);
            Assert.That(request.Frame, Is.EqualTo(0));  // wraps to first frame
            Assert.That(request.IsPrefetch, Is.True);
        }

        [Test]
        public void LastExplicit_UpdatedAfterExplicitDecode()
        {
            var s = new DecodeScheduler(FrameCount);
            Assert.That(s.LastExplicit, Is.EqualTo(-1));

            s.Next(5, 1);
            s.OnDecoded(5, false, 1);

            Assert.That(s.LastExplicit, Is.EqualTo(5));
        }

        [Test]
        public void Next_SingleFrame_NeverPrefetches()
        {
            var s = new DecodeScheduler(1);  // one-frame video
            s.Next(0, 1);
            s.OnDecoded(0, false, 1);

            var request = s.Next(0, 1);
            Assert.That(request.Kind, Is.EqualTo(DecodeRequestKind.Wait));  // nothing to prefetch
        }
    }
}
