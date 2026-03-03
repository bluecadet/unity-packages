using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Bluecadet.Utils {

    // Idle Timeout utils component with tiered idle states. 
    //
    // For example, if a list of [30, 60] is given as the IdleTimeoutIntervals,
    // IdleStateEvent will be triggered with `1` after 30 seconds and again with `2` after 60 seconds of idle time.
    public class IdleTimeout : MonoBehaviour
    {
        [Delayed]
        [Tooltip("List of idle timeouts in seconds. Idle State Event will be triggered with the corresponding \"tier\" number once the timeout intervals have passed. Automatically sorted in increasing order.")]
        public List<float> IdleTimeoutIntervals = new List<float>();
        
        [Tooltip("Event triggered when idle state is reached. Returns the corresponding \"tier\" number based on Idle Timeout Intervals list.")]
        public UnityEvent<int> IdleStateEvent;

        // 0 - Not idle state
        // 1+ - Idle state in tiers; Max state equals the size of IdleTimeoutIntervals
        private int currentState = 0;
        private float idleTime = 0.0f;

        void Update()
        {
            idleTime += Time.deltaTime;

            for (int i = 0; i < IdleTimeoutIntervals.Count; i++) {
                if (idleTime >= IdleTimeoutIntervals[i] && currentState <= i) {
                    currentState = i + 1;
                    IdleStateEvent.Invoke(currentState);
                }
            }
        }

        void OnValidate() {
            IdleTimeoutIntervals.Sort();
        }

        // Should be called when user activity is detected
        public void OnUserActivity() {
            currentState = 0;
            idleTime = 0.0f;
        }
    }

}
