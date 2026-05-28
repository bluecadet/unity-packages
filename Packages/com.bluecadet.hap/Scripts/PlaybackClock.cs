using UnityEngine;

namespace Bluecadet.Hap
{
    internal enum ClockAdvanceEvent { None, Looped, Completed }

    internal struct PlaybackClock
    {
        public float Time;

        public ClockAdvanceEvent Advance(float dt, float speed, float duration, bool loop)
        {
            Time += dt * speed;
            if (Time >= duration)
            {
                if (loop) { Time %= duration; return ClockAdvanceEvent.Looped; }
                Time = duration;
                return ClockAdvanceEvent.Completed;
            }
            if (Time < 0f)
            {
                if (loop) { Time = ((Time % duration) + duration) % duration; return ClockAdvanceEvent.Looped; }
                Time = 0f;
                return ClockAdvanceEvent.Completed;
            }
            return ClockAdvanceEvent.None;
        }

        public int ToFrame(int frameCount, float frameRate)
        {
            if (frameCount <= 0) return 0;
            return Mathf.Clamp(Mathf.FloorToInt(Time * frameRate), 0, frameCount - 1);
        }
    }
}
