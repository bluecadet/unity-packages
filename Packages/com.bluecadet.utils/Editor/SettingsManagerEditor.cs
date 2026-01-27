using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using Bluecadet.Utils;
using Newtonsoft.Json.Linq;

[CustomEditor(typeof(SettingsManager))]
[CanEditMultipleObjects]
public class SettingsManagerEditor : Editor
{
    private class SettingsWrapper : ScriptableObject
    {
        public AppSettings settings = new();
    }

    private SettingsWrapper _wrapper;
    private SerializedObject _wrapperSO;
    private HashSet<string> _localOverridePaths = new();
    private HashSet<string> _dirtyPaths = new();
    private JObject _cleanSnapshot;

    private static readonly Color OverrideTint = new Color(0.5f, 0.85f, 1f, 1f);
    private static readonly Color DirtyTint = new Color(1f, 0.9f, 0.6f, 1f);

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

        // Collect local override paths
        CollectLocalOverridePaths(serializedObject.FindProperty("localFileName").stringValue);

        // Collect dirty paths (current vs on-disk snapshot)
        CollectDirtyPaths(manager);

        // Draw currentSettings via the wrapper
        _wrapper.settings = manager.currentSettings;
        _wrapperSO = new SerializedObject(_wrapper);

        var settingsProp = _wrapperSO.FindProperty("settings");
        EditorGUI.BeginChangeCheck();

        settingsProp.isExpanded = EditorGUILayout.Foldout(settingsProp.isExpanded, "Current Settings", true);
        if (settingsProp.isExpanded)
        {
            EditorGUI.indentLevel++;
            DrawChildrenRecursive(settingsProp);
            EditorGUI.indentLevel--;
        }

        if (EditorGUI.EndChangeCheck())
        {
            _wrapperSO.ApplyModifiedPropertiesWithoutUndo();
            manager.currentSettings = _wrapper.settings;
        }

        // Legend
        if (_localOverridePaths.Count > 0 || _dirtyPaths.Count > 0)
        {
            EditorGUILayout.Space(2);
            var prev = GUI.backgroundColor;
            if (_localOverridePaths.Count > 0)
            {
                GUI.backgroundColor = OverrideTint;
                EditorGUILayout.HelpBox("Highlighted blue = local override", MessageType.None);
            }
            if (_dirtyPaths.Count > 0)
            {
                GUI.backgroundColor = DirtyTint;
                EditorGUILayout.HelpBox("Highlighted yellow = unsaved change", MessageType.None);
            }
            GUI.backgroundColor = prev;
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Save to Base File"))
        {
            manager.SaveToBaseFile();
            CaptureCleanSnapshot(manager);
        }

        if (GUILayout.Button("Save to Local File"))
        {
            manager.SaveToLocalFile();
            CaptureCleanSnapshot(manager);
        }

        if (GUILayout.Button("Load from File"))
        {
            manager.LoadFromFile();
            CaptureCleanSnapshot(manager);
        }

        EditorGUI.BeginDisabledGroup(!Application.isPlaying);

        if (GUILayout.Button("Broadcast Settings Loaded"))
        {
            manager.SendMessage("BroadcastSettingsLoaded", null, SendMessageOptions.DontRequireReceiver);
        }

        EditorGUI.EndDisabledGroup();
    }

    private void CaptureCleanSnapshot(SettingsManager manager)
    {
        _cleanSnapshot = JObject.Parse(JsonUtility.ToJson(manager.currentSettings));
    }

    private void CollectDirtyPaths(SettingsManager manager)
    {
        _dirtyPaths.Clear();

        if (_cleanSnapshot == null)
        {
            // First time: read merged on-disk state as the clean baseline
            CaptureCleanSnapshotFromDisk(manager);
        }

        if (_cleanSnapshot == null) return;

        JObject current = JObject.Parse(JsonUtility.ToJson(manager.currentSettings));
        CollectDiffLeaves(_cleanSnapshot, current, "", _dirtyPaths);
    }

    private void CaptureCleanSnapshotFromDisk(SettingsManager manager)
    {
        try
        {
            var baseFileName = serializedObject.FindProperty("baseFileName").stringValue;
            string basePath = Path.Combine(Application.streamingAssetsPath, baseFileName);
            if (!File.Exists(basePath)) return;

            JObject baseObj = JObject.Parse(File.ReadAllText(basePath));

            var localFileName = serializedObject.FindProperty("localFileName").stringValue;
            if (!string.IsNullOrEmpty(localFileName))
            {
                string localPath = Path.Combine(Application.streamingAssetsPath, localFileName);
                if (File.Exists(localPath))
                {
                    JObject localObj = JObject.Parse(File.ReadAllText(localPath));
                    baseObj.Merge(localObj, new JsonMergeSettings
                    {
                        MergeArrayHandling = MergeArrayHandling.Replace
                    });
                }
            }

            _cleanSnapshot = baseObj;
        }
        catch
        {
            // Fall back to current settings as snapshot
            _cleanSnapshot = JObject.Parse(JsonUtility.ToJson(manager.currentSettings));
        }
    }

    private static void CollectDiffLeaves(JObject baseline, JObject current, string prefix, HashSet<string> paths)
    {
        foreach (var prop in current.Properties())
        {
            string path = string.IsNullOrEmpty(prefix) ? prop.Name : prefix + "." + prop.Name;
            JToken baseVal = baseline[prop.Name];

            if (baseVal == null)
            {
                // New property — mark all leaves as dirty
                if (prop.Value.Type == JTokenType.Object)
                    FlattenAllLeaves((JObject)prop.Value, path, paths);
                else
                    paths.Add(path);
            }
            else if (prop.Value.Type == JTokenType.Object && baseVal.Type == JTokenType.Object)
            {
                CollectDiffLeaves((JObject)baseVal, (JObject)prop.Value, path, paths);
            }
            else if (!JToken.DeepEquals(baseVal, prop.Value))
            {
                paths.Add(path);
            }
        }
    }

    private static void FlattenAllLeaves(JObject obj, string prefix, HashSet<string> paths)
    {
        foreach (var prop in obj.Properties())
        {
            string path = prefix + "." + prop.Name;
            if (prop.Value.Type == JTokenType.Object)
                FlattenAllLeaves((JObject)prop.Value, path, paths);
            else
                paths.Add(path);
        }
    }

    private void CollectLocalOverridePaths(string localFileName)
    {
        _localOverridePaths.Clear();
        if (string.IsNullOrEmpty(localFileName)) return;

        string localPath = Path.Combine(Application.streamingAssetsPath, localFileName);
        if (!File.Exists(localPath)) return;

        try
        {
            string json = File.ReadAllText(localPath);
            JObject obj = JObject.Parse(json);
            FlattenPaths(obj, "", _localOverridePaths);
        }
        catch
        {
            // Ignore parse errors
        }
    }

    private static void FlattenPaths(JObject obj, string prefix, HashSet<string> paths)
    {
        foreach (var prop in obj.Properties())
        {
            string path = string.IsNullOrEmpty(prefix) ? prop.Name : prefix + "." + prop.Name;
            if (prop.Value.Type == JTokenType.Object)
            {
                FlattenPaths((JObject)prop.Value, path, paths);
            }
            else
            {
                paths.Add(path);
            }
        }
    }

    private void DrawChildrenRecursive(SerializedProperty parent)
    {
        SerializedProperty iter = parent.Copy();
        SerializedProperty end = parent.GetEndProperty();
        iter.NextVisible(true); // enter children

        while (!SerializedProperty.EqualContents(iter, end))
        {
            string jsonPath = PropertyPathToJsonPath(iter.propertyPath);
            bool isOverridden = _localOverridePaths.Contains(jsonPath);
            bool isDirty = _dirtyPaths.Contains(jsonPath);
            bool hasChildren = iter.hasVisibleChildren;

            Color prevBg = GUI.backgroundColor;

            if (!hasChildren)
            {
                // Dirty takes priority over override
                if (isDirty)
                    GUI.backgroundColor = DirtyTint;
                else if (isOverridden)
                    GUI.backgroundColor = OverrideTint;
            }

            if (hasChildren)
            {
                // Check if any descendant is overridden or dirty to tint the foldout
                Color? foldoutTint = null;
                string prefix = jsonPath + ".";
                foreach (var p in _dirtyPaths)
                {
                    if (p.StartsWith(prefix) || p == jsonPath)
                    {
                        foldoutTint = DirtyTint;
                        break;
                    }
                }
                if (foldoutTint == null)
                {
                    foreach (var p in _localOverridePaths)
                    {
                        if (p.StartsWith(prefix) || p == jsonPath)
                        {
                            foldoutTint = OverrideTint;
                            break;
                        }
                    }
                }

                if (foldoutTint.HasValue)
                    GUI.backgroundColor = foldoutTint.Value;

                iter.isExpanded = EditorGUILayout.Foldout(iter.isExpanded, iter.displayName, true);
                GUI.backgroundColor = prevBg;

                if (iter.isExpanded)
                {
                    EditorGUI.indentLevel++;
                    DrawChildrenRecursive(iter);
                    EditorGUI.indentLevel--;
                }

                iter.NextVisible(false);
            }
            else
            {
                EditorGUILayout.PropertyField(iter, false);
                GUI.backgroundColor = prevBg;
                iter.NextVisible(false);
            }
        }
    }

    /// Converts a SerializedProperty path like "settings.general.screenSize.x"
    /// to a JSON path like "general.screenSize.x" by stripping the "settings." prefix.
    private static string PropertyPathToJsonPath(string propertyPath)
    {
        const string prefix = "settings.";
        if (propertyPath.StartsWith(prefix))
            return propertyPath.Substring(prefix.Length);
        return propertyPath;
    }
}
