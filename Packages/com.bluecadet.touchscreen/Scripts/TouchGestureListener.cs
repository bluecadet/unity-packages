using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace Bluecadet.Touchscreen {

    #region Pan Gesture Event

    public class GesturePanStartData {
        public readonly PointerEventData pointer;
        public readonly Vector2 startPosition;

        public GesturePanStartData(PointerEventData pointer) {
            this.pointer = pointer;
            this.startPosition = pointer.position;
        }
    }

    public class GesturePanEndData {
        public readonly PointerEventData pointer;
        public readonly Vector2 startPosition;
        public readonly Vector2 endPosition;
        /// <summary>
        /// Total movement from start to end position.
        /// </summary>
        public readonly Vector2 totalDelta;
        /// <summary>
        /// Total distance traveled from start to end position.
        /// </summary>
        public readonly float totalDistance;
        /// <summary>
        /// Rolling/averaged velocity of the pan gesture in screen space.
        /// </summary>
        public readonly Vector2 rollingVelocity;
        /// <summary>
        /// Instantaneous velocity at the moment the gesture ended (delta / deltaTime).
        /// </summary>
        public readonly Vector2 finalVelocity;

        public GesturePanEndData(PointerEventData pointer, Vector2 startPosition, Vector2 rollingVelocity, Vector2 finalVelocity) {
            this.pointer = pointer;
            this.startPosition = startPosition;
            this.endPosition = pointer.position;
            this.totalDelta = endPosition - startPosition;
            this.totalDistance = totalDelta.magnitude;
            this.rollingVelocity = rollingVelocity;
            this.finalVelocity = finalVelocity;
        }
    }

    public class GesturePanData {
        /// <summary>
        /// The pointer data for the pan gesture.
        /// </summary>
        public readonly PointerEventData pointer;

        /// <summary>
        /// Current position in screen space.
        /// </summary>
        public readonly Vector2 position;

        /// <summary>
        /// Delta movement since last frame.
        /// </summary>
        public readonly Vector2 delta;

        /// <summary>
        /// Velocity of the pan gesture in screen space (delta / deltaTime).
        /// </summary>
        public readonly Vector2 velocity;

        /// <summary>
        /// Initial position when the pan gesture started.
        /// </summary>
        public readonly Vector2 initialPosition;

        public GesturePanData(PointerEventData pointer, Vector2 initialPosition) {
            this.pointer = pointer;
            this.position = pointer.position;
            this.delta = pointer.delta;
            this.initialPosition = initialPosition;
            this.velocity = delta / Time.deltaTime;
        }
    }

    [Serializable]
    public class GesturePanStartEvent : UnityEvent<GesturePanStartData> { }

    [Serializable]
    public class GesturePanEvent : UnityEvent<GesturePanData> { }

    [Serializable]
    public class GesturePanEndEvent : UnityEvent<GesturePanEndData> { }

    #endregion

    #region Pinch Gesture Event

    public struct PinchValues {
        /// <summary>
        /// Distance between the two pointers in screen space.
        /// </summary>
        public readonly float distance;
        /// <summary>
        /// Angle between the two pointers in radians.
        /// </summary>
        public readonly float angle;
        /// <summary>
        /// Origin point between the two pointers in screen space.
        /// </summary>
        public readonly Vector2 origin;

        public PinchValues(float distance, float angle, Vector2 origin) {
            this.distance = distance;
            this.angle = angle;
            this.origin = origin;
        }
    }

    public class GesturePinchData {
        /// <summary>
        /// Initial values when the pinch gesture started.
        /// </summary>
        public readonly PinchValues initial;

        /// <summary>
        /// Current values for this frame.
        /// </summary>
        public readonly PinchValues current;

        /// <summary>
        /// Delta values (change since last frame).
        /// </summary>
        public readonly PinchValues delta;

        /// <summary>
        /// The scaling factor (current distance / initial distance).
        /// </summary>
        public readonly float scaleFactor;

        /// <summary>
        /// The first pointer data.
        /// </summary>
        public readonly PointerEventData pointer1;

        /// <summary>
        /// The second pointer data.
        /// </summary>
        public readonly PointerEventData pointer2;

        public GesturePinchData(PointerEventData pointer1, PointerEventData pointer2,
                               PinchValues initial, PinchValues previous) {
            this.pointer1 = pointer1;
            this.pointer2 = pointer2;
            this.initial = initial;

            // Calculate current values
            float currentDistance = Vector2.Distance(pointer1.position, pointer2.position);
            Vector2 currentOrigin = (pointer1.position + pointer2.position) * 0.5f;
            float currentAngle = Mathf.Atan2(pointer2.position.y - pointer1.position.y,
                                           pointer2.position.x - pointer1.position.x);

            this.current = new PinchValues(currentDistance, currentAngle, currentOrigin);

            // Calculate delta values
            this.delta = new PinchValues(
                currentDistance - previous.distance,
                Mathf.DeltaAngle(previous.angle * Mathf.Rad2Deg, currentAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad,
                currentOrigin - previous.origin
            );

            this.scaleFactor = initial.distance > 0 ? currentDistance / initial.distance : 1f;
        }
    }

    public class GesturePinchStartData {
        public readonly PointerEventData pointer1;
        public readonly PointerEventData pointer2;
        public readonly PinchValues initial;

        public GesturePinchStartData(PointerEventData pointer1, PointerEventData pointer2, PinchValues initial) {
            this.pointer1 = pointer1;
            this.pointer2 = pointer2;
            this.initial = initial;
        }
    }

    public class GesturePinchEndData {
        public readonly PointerEventData pointer1;
        public readonly PointerEventData pointer2;
        public readonly PinchValues initial;
        public readonly PinchValues final;
        /// <summary>
        /// Total scale change from start to end (final distance / initial distance).
        /// </summary>
        public readonly float totalScaleFactor;
        /// <summary>
        /// Total rotation change from start to end in radians.
        /// </summary>
        public readonly float totalRotation;
        /// <summary>
        /// Rolling/averaged velocity of the pinch origin point in screen space.
        /// </summary>
        public readonly Vector2 rollingOriginVelocity;
        /// <summary>
        /// Instantaneous velocity of the pinch origin at the moment the gesture ended.
        /// </summary>
        public readonly Vector2 finalOriginVelocity;

        public GesturePinchEndData(PointerEventData pointer1, PointerEventData pointer2,
                                  PinchValues initial, PinchValues final,
                                  Vector2 rollingOriginVelocity, Vector2 finalOriginVelocity) {
            this.pointer1 = pointer1;
            this.pointer2 = pointer2;
            this.initial = initial;
            this.final = final;
            this.totalScaleFactor = initial.distance > 0 ? final.distance / initial.distance : 1f;
            this.totalRotation = Mathf.DeltaAngle(initial.angle * Mathf.Rad2Deg, final.angle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            this.rollingOriginVelocity = rollingOriginVelocity;
            this.finalOriginVelocity = finalOriginVelocity;
        }
    }

    [Serializable]
    public class GesturePinchStartEvent : UnityEvent<GesturePinchStartData> { }

    [Serializable]
    public class GesturePinchEvent : UnityEvent<GesturePinchData> { }

    [Serializable]
    public class GesturePinchEndEvent : UnityEvent<GesturePinchEndData> { }

    #endregion

    /// <summary>
    /// Detects and emits pan and pinch touch gestures.
    /// </summary>
    public class TouchGestureListener : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler {
        public GesturePanStartEvent OnPanStart;
        public GesturePanEvent OnPan;
        public GesturePanEndEvent OnPanEnd;

        public GesturePinchStartEvent OnPinchStart;
        public GesturePinchEvent OnPinch;
        public GesturePinchEndEvent OnPinchEnd;

        private Dictionary<int, PointerEventData> activePointers = new Dictionary<int, PointerEventData>();
        private PinchValues initialPinchValues;
        private PinchValues lastPinchValues;
        private Vector2 initialPanPosition;
        private bool isPinching = false;
        private bool isPanning = false;

        // Track which specific pointers started the pinch gesture
        private int pinchPointer1Id = -1;
        private int pinchPointer2Id = -1;

        // Velocity tracking for pan
        private VelocityTracker2D panVelocityTracker = new VelocityTracker2D();
        // Velocity tracking for pinch origin
        private VelocityTracker2D pinchOriginVelocityTracker = new VelocityTracker2D();

        public void OnPointerDown(PointerEventData eventData) {
            activePointers[eventData.pointerId] = eventData;

            if (activePointers.Count == 1) {
                initialPanPosition = eventData.position;
                isPanning = true;
                panVelocityTracker.Clear();
                OnPanStart?.Invoke(new GesturePanStartData(eventData));
            }
        }

        public void OnPointerUp(PointerEventData eventData) {
            if (activePointers.Count == 1 && isPanning) {
                // End pan gesture
                Vector2 rollingVelocity = panVelocityTracker.GetAveragedVelocity();
                Vector2 finalVelocity = panVelocityTracker.GetLastVelocity();
                OnPanEnd?.Invoke(new GesturePanEndData(eventData, initialPanPosition, rollingVelocity, finalVelocity));
            } else if (activePointers.Count == 2 && isPinching) {
                // End pinch gesture
                PointerEventData[] pointers = new PointerEventData[2];
                activePointers.Values.CopyTo(pointers, 0);
                Vector2 rollingOriginVelocity = pinchOriginVelocityTracker.GetAveragedVelocity();
                Vector2 finalOriginVelocity = pinchOriginVelocityTracker.GetLastVelocity();
                OnPinchEnd?.Invoke(new GesturePinchEndData(pointers[0], pointers[1], initialPinchValues, lastPinchValues, rollingOriginVelocity, finalOriginVelocity));
            }

            activePointers.Remove(eventData.pointerId);

            // If one of the pinch pointers was released, end the pinch gesture
            if (isPinching && (eventData.pointerId == pinchPointer1Id || eventData.pointerId == pinchPointer2Id)) {
                isPinching = false;
                pinchPointer1Id = -1;
                pinchPointer2Id = -1;
            }

            if (activePointers.Count == 0) {
                isPanning = false;
            }
        }

        public void OnDrag(PointerEventData eventData) {
            if (activePointers.ContainsKey(eventData.pointerId)) {
                activePointers[eventData.pointerId] = eventData;
            }

            if (activePointers.Count == 1 && isPanning) {
                // Handle pan gesture
                GesturePanData panData = new GesturePanData(eventData, initialPanPosition);

                // Track pan velocity
                if (Time.deltaTime > 0) {
                    Vector2 velocity = eventData.delta / Time.deltaTime;
                    panVelocityTracker.TrackVelocity(velocity, Time.time);
                }

                OnPan?.Invoke(panData);
            } else if (activePointers.Count == 2) {
                // Handle pinch gesture
                isPanning = false; // Stop panning when we have 2 pointers

                PointerEventData[] pointers = new PointerEventData[2];
                activePointers.Values.CopyTo(pointers, 0);

                if (!isPinching) {
                    // Track the specific pointer IDs that started this pinch
                    pinchPointer1Id = pointers[0].pointerId;
                    pinchPointer2Id = pointers[1].pointerId;

                    // Calculate initial values
                    float distance = Vector2.Distance(pointers[0].position, pointers[1].position);
                    Vector2 origin = (pointers[0].position + pointers[1].position) * 0.5f;
                    float angle = Mathf.Atan2(pointers[1].position.y - pointers[0].position.y,
                                            pointers[1].position.x - pointers[0].position.x);

                    initialPinchValues = new PinchValues(distance, angle, origin);
                    lastPinchValues = initialPinchValues;
                    isPinching = true;

                    // Clear velocity tracker for new pinch gesture
                    pinchOriginVelocityTracker.Clear();

                    OnPinchStart?.Invoke(new GesturePinchStartData(pointers[0], pointers[1], initialPinchValues));
                }

                GesturePinchData pinchData = new GesturePinchData(pointers[0], pointers[1], initialPinchValues, lastPinchValues);

                // Track origin velocity (using delta / deltaTime)
                if (Time.deltaTime > 0) {
                    Vector2 originVelocity = pinchData.delta.origin / Time.deltaTime;
                    pinchOriginVelocityTracker.TrackVelocity(originVelocity, Time.time);
                }

                OnPinch?.Invoke(pinchData);

                lastPinchValues = pinchData.current;
            } else if (activePointers.Count >= 2 && isPinching) {
                // Continue existing pinch gesture with the original two pointers
                // Ignore any additional pointers beyond the original two
                if (activePointers.ContainsKey(pinchPointer1Id) && activePointers.ContainsKey(pinchPointer2Id)) {
                    PointerEventData pointer1 = activePointers[pinchPointer1Id];
                    PointerEventData pointer2 = activePointers[pinchPointer2Id];

                    GesturePinchData pinchData = new GesturePinchData(pointer1, pointer2, initialPinchValues, lastPinchValues);

                    // Track origin velocity (using delta / deltaTime)
                    if (Time.deltaTime > 0) {
                        Vector2 originVelocity = pinchData.delta.origin / Time.deltaTime;
                        pinchOriginVelocityTracker.TrackVelocity(originVelocity, Time.time);
                    }

                    OnPinch?.Invoke(pinchData);

                    lastPinchValues = pinchData.current;
                }
            }
        }
    }

}