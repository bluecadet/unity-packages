using System.Threading;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Records which thread Unity's main thread is, so the public async API can refuse calls
    /// from anywhere else.
    /// </summary>
    internal static class HapThread
    {
        /// <summary>
        /// No real thread has this id, so a call made before the main thread has been recorded
        /// is treated as coming from somewhere else. Both initialization hooks run before any
        /// user code, so the only calls that see this are ones from a thread the package could
        /// not have vouched for anyway.
        /// </summary>
        const int Unknown = -1;

        static int s_mainThreadId = Unknown;

        /// <summary>True only on Unity's main thread.</summary>
        public static bool IsMain => Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void CaptureAtRuntime() => s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void CaptureInEditor() => s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
#endif
    }
}
