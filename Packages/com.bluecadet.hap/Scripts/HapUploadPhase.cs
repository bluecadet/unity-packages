using System.Collections.Generic;

namespace Bluecadet.Hap
{
    /// <summary>
    /// A player as the upload phase sees it: how much GPU work its next frame is, and the two
    /// ways a tick can end for it — uploading the frame it has waiting, or showing what it
    /// already uploaded and keeping that frame for a later tick.
    /// </summary>
    internal interface IHapUploadTarget
    {
        /// <summary>Bytes this player's next upload hands to the GPU.</summary>
        long PendingUploadBytes { get; }

        /// <summary>Upload the decoded frame, show it, and swap the display buffer.</summary>
        void TickUpload();

        /// <summary>Show whatever is already on the GPU, uploading nothing.</summary>
        void TickRender();
    }

    /// <summary>
    /// The upload half of a central tick: hands the due players' decoded frames to the GPU.
    ///
    /// Uploading is the expensive part of playing many videos at once — one memcpy of a whole
    /// decoded frame into GPU-visible memory each — and it is main-thread work, so how it is
    /// spread across a tick is what decides how many players a frame budget fits.
    ///
    /// Rotation: the phase starts one player further along the due list every tick, so no player
    /// is systematically last and none is systematically the one a budget cuts off.
    ///
    /// Budget: with <c>budgetBytes</c> set, the phase stops uploading once a tick's uploads have
    /// passed it, and every player left simply keeps its decoded frame and tries again next tick.
    /// Hap is all-keyframe and clocks keep advancing regardless, so a deferred upload is a
    /// dropped frame, never a corrupt one — and the deferred player still renders what it has.
    /// The first player is always allowed to upload, however large its frame, so a budget smaller
    /// than one frame throttles rather than starves.
    /// </summary>
    internal sealed class HapUploadPhase
    {
        int _rotation;

        /// <summary>How far the start index has rotated. Test seam.</summary>
        internal int Rotation => _rotation;

        /// <summary>
        /// Upload the frames of <paramref name="due"/>, starting one player further along than
        /// last tick.
        /// </summary>
        /// <param name="budgetBytes">Byte cap for this tick's uploads, or 0 for no cap.</param>
        public void Run(List<IHapUploadTarget> due, long budgetBytes)
        {
            int count = due.Count;

            // Unsigned so the start index keeps rotating rather than turning negative once the
            // counter wraps, which a long-running installation gets to.
            int start = count > 0 ? (int)((uint)_rotation % (uint)count) : 0;
            _rotation++;

            long uploaded = 0;

            for (int i = 0; i < count; i++)
            {
                var target = due[(start + i) % count];

                if (budgetBytes > 0 && uploaded >= budgetBytes)
                {
                    target.TickRender();
                    continue;
                }

                uploaded += target.PendingUploadBytes;
                target.TickUpload();
            }
        }
    }
}
