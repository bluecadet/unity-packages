using UnityEngine;
using UnityEngine.InputSystem;

namespace Bluecadet.Touchscreen {
    
    public class MultiTouchSimulator {
        
        private bool m_IsActive = false;
        private Vector2 m_PivotPoint;
        private Vector2 m_InitialMousePosition;
        
        // Shift key state tracking
        private bool m_WasShiftHeld = false;
        private float m_RememberedAngle = 0f;
        private float m_RememberedDistance = 0f;
        
        public bool IsActive => m_IsActive;
        
        public void Reset() {
            m_IsActive = false;
            m_WasShiftHeld = false;
        }
        
        public void ProcessSimulation(Vector2 mousePosition, out Vector2 point1, out Vector2 point2) {
            bool isShiftHeld = Keyboard.current?.shiftKey.isPressed ?? false;
            
            // Initialize simulation on first call
            if (!m_IsActive) {
                InitializeSimulation(mousePosition);
            }
            
            HandleShiftTransitions(mousePosition, isShiftHeld);
            CalculatePoints(mousePosition, isShiftHeld, out point1, out point2);
        }
        
        private void InitializeSimulation(Vector2 mousePosition) {
            m_IsActive = true;
            m_PivotPoint = mousePosition;
            m_InitialMousePosition = mousePosition;
            m_WasShiftHeld = false;
        }
        
        private void HandleShiftTransitions(Vector2 mousePosition, bool isShiftHeld) {
            // Shift pressed - remember current state
            if (isShiftHeld && !m_WasShiftHeld) {
                Vector2 deltaFromPivot = mousePosition - m_PivotPoint;
                m_RememberedDistance = Mathf.Max(deltaFromPivot.magnitude, 10f);
                m_RememberedAngle = Mathf.Atan2(deltaFromPivot.y, deltaFromPivot.x);
                m_InitialMousePosition = mousePosition;
            }
            // Shift released - update pivot
            else if (!isShiftHeld && m_WasShiftHeld) {
                Vector2 translationOffset = mousePosition - m_InitialMousePosition;
                m_PivotPoint += translationOffset;
            }
            
            m_WasShiftHeld = isShiftHeld;
        }
        
        private void CalculatePoints(Vector2 mousePosition, bool isShiftHeld, out Vector2 point1, out Vector2 point2) {
            if (isShiftHeld) {
                // Translate mode - use remembered rotation and distance
                Vector2 translationOffset = mousePosition - m_InitialMousePosition;
                Vector2 currentPivot = m_PivotPoint + translationOffset;
                
                Vector2 direction = new Vector2(Mathf.Cos(m_RememberedAngle), Mathf.Sin(m_RememberedAngle));
                point1 = currentPivot + direction * m_RememberedDistance;
                point2 = currentPivot - direction * m_RememberedDistance;
            } else {
                // Rotate/scale mode
                Vector2 deltaFromPivot = mousePosition - m_PivotPoint;
                float distance = Mathf.Max(deltaFromPivot.magnitude, 10f);
                
                float angle = Mathf.Atan2(deltaFromPivot.y, deltaFromPivot.x);
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                
                point1 = m_PivotPoint + direction * distance;
                point2 = m_PivotPoint - direction * distance;
            }
        }
    }
}
