using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Bluecadet.Touchscreen {
    
    public static class PointerGizmoRenderer {
        
        private static Vector3[] s_CirclePoints;
        private static readonly object s_CircleLock = new object();
        
        static PointerGizmoRenderer() {
            InitializeCirclePoints();
        }
        
        private static void InitializeCirclePoints() {
            lock (s_CircleLock) {
                if (s_CirclePoints != null) return;
                
                const int segments = 16;
                s_CirclePoints = new Vector3[segments * 2];

                float angleStep = 360f / segments * Mathf.Deg2Rad;
                for (int i = 0; i < segments; i++) {
                    float angle1 = i * angleStep;
                    float angle2 = ((i + 1) % segments) * angleStep;

                    s_CirclePoints[i * 2] = new Vector3(Mathf.Cos(angle1), Mathf.Sin(angle1), 0);
                    s_CirclePoints[i * 2 + 1] = new Vector3(Mathf.Cos(angle2), Mathf.Sin(angle2), 0);
                }
            }
        }
        
        public static void DrawPointerGizmos(Dictionary<int, PointerEventData> pointerData, bool isMultiTouchSimulationActive) {
            if (pointerData.Count == 0 || Camera.main == null) return;

            float drawPlane = Camera.main.nearClipPlane + 0.1f;
            float gizmoSize = CalculateGizmoSize(drawPlane);

            foreach (var kvp in pointerData) {
                var pointer = kvp.Value;

                if (!ShouldDrawPointer(pointer, isMultiTouchSimulationActive)) continue;

                if (pointer.pointerPress != null)
                {
                    Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
                    DrawPointerCircle(pointer.position, drawPlane, gizmoSize);

                    Gizmos.color = new Color(1f, 1f, 1f, 0.1f);
                    DrawPointerCircle(pointer.position, drawPlane, gizmoSize * 0.8f);
                }
                else
                {
                    Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
                    DrawPointerCircle(pointer.position, drawPlane, gizmoSize);
                }

            }
        }
        
        private static float CalculateGizmoSize(float drawPlane) {
            const float pixelSize = 24f;

            if (Camera.main.orthographic) {
                return (pixelSize * Camera.main.orthographicSize * 2f) / Screen.height;
            } else {
                float distance = Vector3.Distance(Camera.main.transform.position, 
                    Camera.main.transform.position + Camera.main.transform.forward * drawPlane);
                return (pixelSize * distance * Mathf.Tan(Camera.main.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f) / Screen.height;
            }
        }

        private static bool ShouldDrawPointer(PointerEventData pointerData, bool isMultiTouchSimulationActive) {
            return pointerData.pointerPress != null || 
                   pointerData.pointerDrag != null || 
                   isMultiTouchSimulationActive;
        }

        private static void DrawPointerCircle(Vector2 screenPos, float drawPlane, float size) {
            Vector3 screenPoint = new Vector3(screenPos.x, screenPos.y, drawPlane);
            Vector3 worldPos = Camera.main.ScreenToWorldPoint(screenPoint);

            // Calculate rotation to face camera
            Vector3 toCameraDirection = (Camera.main.transform.position - worldPos).normalized;
            Quaternion lookRotation = Quaternion.LookRotation(toCameraDirection);

            // Transform and draw circle
            for (int i = 0; i < s_CirclePoints.Length; i += 2) {
                Vector3 point1 = worldPos + lookRotation * (s_CirclePoints[i] * size);
                Vector3 point2 = worldPos + lookRotation * (s_CirclePoints[i + 1] * size);
                Gizmos.DrawLine(point1, point2);
            }
        }
    }
}
