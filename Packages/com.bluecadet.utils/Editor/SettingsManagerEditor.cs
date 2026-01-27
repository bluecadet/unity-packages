using UnityEngine;
using UnityEditor;
using Bluecadet.Utils;

[CustomEditor(typeof(SettingsManager))]
[CanEditMultipleObjects]
public class SettingsManagerEditor : Editor
{
    // currentSettings is [NonSerialized] on SettingsManager so it doesn't get
    // saved to the scene/prefab. Unity's EditorGUILayout.PropertyField only works
    // with SerializedProperty, which requires serialized data. To bridge the gap,
    // we copy the settings into this temporary ScriptableObject, let Unity draw it
    // with full property-drawer and foldout support, then copy changes back.
    private class SettingsWrapper : ScriptableObject
    {
        public AppSettings settings = new();
    }

    private SettingsWrapper _wrapper;
    private SerializedObject _wrapperSO;

    void OnEnable()
    {
        _wrapper = ScriptableObject.CreateInstance<SettingsWrapper>();
        _wrapper.hideFlags = HideFlags.DontSave;
    }

    void OnDisable()
    {
        if (_wrapper != null)
            DestroyImmediate(_wrapper);
    }

    public override void OnInspectorGUI()
    {
        var manager = (SettingsManager)target;

        // Draw the file name fields via the real serialized object
        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("baseFileName"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("localFileName"));
        serializedObject.ApplyModifiedProperties();

        // Draw currentSettings via the wrapper
        _wrapper.settings = manager.currentSettings;
        _wrapperSO = new SerializedObject(_wrapper);

        var settingsProp = _wrapperSO.FindProperty("settings");
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(settingsProp, new GUIContent("Current Settings"), true);
        if (EditorGUI.EndChangeCheck())
        {
            _wrapperSO.ApplyModifiedPropertiesWithoutUndo();
            manager.currentSettings = _wrapper.settings;
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Save to Base File"))
        {
            manager.SaveToBaseFile();
        }

        if (GUILayout.Button("Save to Local File"))
        {
            manager.SaveToLocalFile();
        }

        if (GUILayout.Button("Load from File"))
        {
            manager.LoadFromFile();
        }

        EditorGUI.BeginDisabledGroup(!Application.isPlaying);

        if (GUILayout.Button("Broadcast Settings Loaded"))
        {
            manager.SendMessage("BroadcastSettingsLoaded", null, SendMessageOptions.DontRequireReceiver);
        }

        EditorGUI.EndDisabledGroup();
    }
}
