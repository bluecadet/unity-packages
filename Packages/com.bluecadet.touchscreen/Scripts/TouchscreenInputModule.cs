using System.Collections.Generic;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Bluecadet.Touchscreen {

    public class TouchscreenInputModule : BaseInputModule {

        [SerializeField] private InputActionAsset m_ActionsAsset;
        [SerializeField] private InputActionReference m_PointAction;
        [SerializeField] private InputActionReference m_ClickAction;

        private Dictionary<int, PointerEventData> m_PointerData = new Dictionary<int, PointerEventData>();
        private MultiTouchSimulator m_MultiTouchSimulator = new MultiTouchSimulator();

        // Reusable collections to reduce allocations
        private HashSet<int> m_ProcessedPointers = new HashSet<int>();
        private List<int> m_PointersToRemove = new List<int>();

        // Mouse pointer IDs
        private const int PRIMARY_MOUSE_ID = 1000;
        private const int SIMULATED_TOUCH_1_ID = 1001;
        private const int SIMULATED_TOUCH_2_ID = 1002;

        protected override void Start() {
            base.Start();
            m_ActionsAsset?.Enable();
        }

        protected override void OnDestroy() {
            m_ActionsAsset?.Disable();
            base.OnDestroy();
        }

        public override void Process() {
            m_ProcessedPointers.Clear();

            ProcessRealTouches();
            ProcessMouseInput();

            CleanupInactivePointers();
        }

        private void ProcessRealTouches() {
            var touchscreen = UnityEngine.InputSystem.Touchscreen.current;
            if (touchscreen == null) return;

            foreach (var touch in touchscreen.touches) {
                if (!touch.isInProgress) continue;

                int touchId = touch.touchId.ReadValue();
                Vector2 position = touch.position.ReadValue();
                bool isPressed = touch.press.ReadValue() > 0.5f;

                ProcessTouch(touchId, position, isPressed);
                m_ProcessedPointers.Add(touchId);
            }
        }

        private void ProcessMouseInput() {
            if (Mouse.current == null) return;

            Vector2 mousePosition = m_PointAction.action.ReadValue<Vector2>();
            bool isMousePressed = m_ClickAction.action.ReadValue<float>() > 0.5f;
            bool isAltHeld = Keyboard.current?.altKey.isPressed ?? false;

            if (isAltHeld) {
                ProcessMultiTouchSimulation(mousePosition, isMousePressed);
            } else {
                // Reset simulation when Alt is released
                if (m_MultiTouchSimulator.IsActive) {
                    m_MultiTouchSimulator.Reset();
                }

                // Standard single mouse pointer
                ProcessTouch(PRIMARY_MOUSE_ID, mousePosition, isMousePressed);
                m_ProcessedPointers.Add(PRIMARY_MOUSE_ID);
            }
        }

        private void ProcessMultiTouchSimulation(Vector2 mousePosition, bool isPressed) {
            m_MultiTouchSimulator.ProcessSimulation(mousePosition, out Vector2 point1, out Vector2 point2);

            ProcessTouch(SIMULATED_TOUCH_1_ID, point1, isPressed);
            ProcessTouch(SIMULATED_TOUCH_2_ID, point2, isPressed);

            m_ProcessedPointers.Add(SIMULATED_TOUCH_1_ID);
            m_ProcessedPointers.Add(SIMULATED_TOUCH_2_ID);
        }

        private void CleanupInactivePointers() {
            m_PointersToRemove.Clear();

            foreach (var kvp in m_PointerData) {
                if (!m_ProcessedPointers.Contains(kvp.Key)) {
                    m_PointersToRemove.Add(kvp.Key);
                }
            }

            foreach (int pointerId in m_PointersToRemove) {
                if (m_PointerData.TryGetValue(pointerId, out PointerEventData pointerData)) {
                    ProcessPointerUp(pointerData);
                    m_PointerData.Remove(pointerId);
                }
            }
        }

        private void ProcessTouch(int pointerId, Vector2 position, bool pressed) {
            if (!m_PointerData.TryGetValue(pointerId, out PointerEventData pointerData)) {
                pointerData = new PointerEventData(eventSystem) {
                    pointerId = pointerId,
                    position = position
                };
                m_PointerData[pointerId] = pointerData;
            }

            // Update position and delta
            pointerData.delta = position - pointerData.position;
            pointerData.position = position;

            // Raycast to find current target
            UpdatePointerRaycast(pointerData);

            // Handle state transitions
            if (pressed) {
                HandlePointerPressed(pointerData);
            } else if (pointerData.pointerPress != null) {
                ProcessPointerUp(pointerData);
            }
        }

        private void UpdatePointerRaycast(PointerEventData pointerData) {
            eventSystem.RaycastAll(pointerData, m_RaycastResultCache);
            pointerData.pointerCurrentRaycast = FindFirstRaycast(m_RaycastResultCache);
            m_RaycastResultCache.Clear();
        }

        private void HandlePointerPressed(PointerEventData pointerData) {
            if (pointerData.pointerPress == null) {
                ProcessPointerDown(pointerData);
            } else if (pointerData.IsPointerMoving() && pointerData.pointerDrag != null) {
                ProcessPointerDrag(pointerData);
            }
        }

        private void ProcessPointerDown(PointerEventData pointerData) {
            var currentOverGO = pointerData.pointerCurrentRaycast.gameObject;
            if (currentOverGO == null) return;

            // Cache event handlers
            var pointerDownHandler = ExecuteEvents.GetEventHandler<IPointerDownHandler>(currentOverGO);
            pointerData.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(currentOverGO);
            pointerData.pointerClick = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGO);

            // Set pointer state
            pointerData.pointerPress = currentOverGO;
            pointerData.pressPosition = pointerData.position;
            pointerData.clickTime = Time.unscaledTime;
            pointerData.clickCount = 1;

            // Execute events
            if (pointerDownHandler != null) {
                ExecuteEvents.Execute(pointerDownHandler, pointerData, ExecuteEvents.pointerDownHandler);
            }

            if (pointerData.pointerDrag != null) {
                ExecuteEvents.Execute(pointerData.pointerDrag, pointerData, ExecuteEvents.beginDragHandler);
            }
        }

        private void ProcessPointerDrag(PointerEventData pointerData) {
            if (pointerData.pointerDrag != null) {
                ExecuteEvents.Execute(pointerData.pointerDrag, pointerData, ExecuteEvents.dragHandler);
            }
        }

        private void ProcessPointerUp(PointerEventData pointerData) {
            // Handle drag end
            if (pointerData.pointerDrag != null) {
                ExecuteEvents.Execute(pointerData.pointerDrag, pointerData, ExecuteEvents.endDragHandler);
                pointerData.pointerDrag = null;
            }

            if (pointerData.pointerPress == null) return;

            // Execute pointer up
            var pointerUpHandler = ExecuteEvents.GetEventHandler<IPointerUpHandler>(pointerData.pointerPress);
            if (pointerUpHandler != null) {
                ExecuteEvents.Execute(pointerUpHandler, pointerData, ExecuteEvents.pointerUpHandler);
            }

            // Handle click if within drag threshold
            if (pointerData.pointerClick != null &&
                Vector2.Distance(pointerData.pressPosition, pointerData.position) < eventSystem.pixelDragThreshold) {
                ExecuteEvents.Execute(pointerData.pointerClick, pointerData, ExecuteEvents.pointerClickHandler);
            }

            // Clear state
            pointerData.pointerPress = null;
            pointerData.pointerClick = null;
        }

        protected void OnDrawGizmos() {
            PointerGizmoRenderer.DrawPointerGizmos(m_PointerData, m_MultiTouchSimulator.IsActive);
        }
    }
}