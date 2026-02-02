using UnityEngine;
using UnityEditor;

namespace Bluecadet.Hap.Editor
{
    [CustomEditor(typeof(HapPlayer))]
    public class HapPlayerEditor : UnityEditor.Editor
    {
        SerializedProperty _filePath;
        SerializedProperty _playOnEnable;
        SerializedProperty _loop;
        SerializedProperty _targetRenderer;

        void OnEnable()
        {
            _filePath = serializedObject.FindProperty("filePath");
            _playOnEnable = serializedObject.FindProperty("playOnEnable");
            _loop = serializedObject.FindProperty("loop");
            _targetRenderer = serializedObject.FindProperty("targetRenderer");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_filePath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFilePanel("Select HAP Video", "", "mov");
                if (!string.IsNullOrEmpty(path))
                    _filePath.stringValue = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(_playOnEnable);
            EditorGUILayout.PropertyField(_loop);
            EditorGUILayout.PropertyField(_targetRenderer);

            var player = (HapPlayer)target;

            if (Application.isPlaying && player.IsOpen)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Playing", player.IsPlaying.ToString());
                EditorGUILayout.LabelField("Frames", player.FrameCount.ToString());
                EditorGUILayout.LabelField("Duration", $"{player.Duration:F2}s");
                EditorGUILayout.LabelField("Time", $"{player.Time:F2}s");

                if (player.Texture != null)
                {
                    EditorGUILayout.LabelField("Texture",
                        $"{player.Texture.width}x{player.Texture.height} {player.Texture.format}");
                }

                EditorGUILayout.Space();
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(player.IsPlaying ? "Pause" : "Play"))
                {
                    if (player.IsPlaying) player.Pause();
                    else player.Play();
                }
                if (GUILayout.Button("Stop"))
                    player.Stop();
                EditorGUILayout.EndHorizontal();

                Repaint();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
