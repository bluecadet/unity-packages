using System;
using System.Collections.Generic;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Completions waiting to be handed out. Awaiting continuations resume inline the moment a
    /// caller is completed, and one of them may well open or close the player again, so
    /// completions are queued here and flushed once the call that caused them has finished
    /// mutating state.
    ///
    /// Main thread only.
    /// </summary>
    internal sealed class HapCompletionQueue
    {
        readonly List<Action> _pending = new();
        bool _flushing;

        public void Add(Action completion)
        {
            if (completion == null) return;
            _pending.Add(completion);
        }

        /// <summary>
        /// Run everything queued so far. Re-entrant calls return immediately: the continuations
        /// they queue are picked up by the flush already running.
        /// </summary>
        public void Flush()
        {
            if (_flushing || _pending.Count == 0) return;

            _flushing = true;
            try
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    try
                    {
                        _pending[i]();
                    }
                    catch (Exception ex)
                    {
                        // One caller's continuation throwing must not strand everyone behind it
                        // in the queue, nor abort the teardown that is handing these out.
                        Debug.LogException(ex);
                    }
                }
                _pending.Clear();
            }
            finally
            {
                _flushing = false;
            }
        }
    }
}
