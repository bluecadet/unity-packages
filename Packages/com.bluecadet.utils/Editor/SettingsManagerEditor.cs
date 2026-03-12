using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using Bluecadet.Utils;
using Newtonsoft.Json.Linq;

[CustomEditor(typeof(SettingsManagerBase), true)]
[CanEditMultipleObjects]
public class SettingsManagerEditor : Editor
{
    private class SettingsWrapper : ScriptableObject
    {
        [SerializeReference]
        public AppSettings settings;
    }

    private SettingsWrapper _wrapper;
    private SerializedObject _wrapperSO;
    private HashSet<string> _localOverridePaths = new();
    private bool _stateInitialized = false;

    // Dirty paths live on the manager so they survive inspector selection changes.
    private HashSet<string> DirtyPaths => GetManagerDirtyPaths();

    private HashSet<string> GetManagerDirtyPaths()
    {
        var field = target.GetType().GetField("editorDirtyPaths",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(target) as HashSet<string> ?? new HashSet<string>();
    }

    private Type _settingsType;

    private static readonly Color OverrideTint = new Color(0.5f, 0.85f, 1f, 1f);
    private static readonly Color DirtyTint = new Color(1f, 0.9f, 0.6f, 1f);

    void OnEnable()
    {
        // Walk up the generic hierarchy to find SettingsManager<TSettings> and extract TSettings
        _settingsType = typeof(AppSettings);
        Type t = target.GetType();
        while (t != null)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(SettingsManager<>))
            {
                _settingsType = t.GetGenericArguments()[0];
                break;
            }
            t = t.BaseType;
        }

        _wrapper = ScriptableObject.CreateInstance<SettingsWrapper>();
        _wrapper.hideFlags = HideFlags.DontSave;

        if (!Application.isPlaying)
            InvokeMethod("LoadFromFile");
    }

    void OnDisable()
    {
        if (_wrapper != null)
            DestroyImmediate(_wrapper);
    }

    private object GetCurrentSettings()
    {
        // Use the public currentSettings field from SettingsManager<TSettings>
        var field = target.GetType().GetField("currentSettings",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(target);
    }

    private void SetCurrentSettings(AppSettings value)
    {
        var field = target.GetType().GetField("currentSettings",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        field?.SetValue(target, value);
    }

    public override void OnInspectorGUI()
    {
        RefreshFileState();

        // Draw currentSettings via the wrapper
        var currentSettings = (AppSettings)GetCurrentSettings();
        _wrapper.settings = currentSettings;
        _wrapperSO = new SerializedObject(_wrapper);

        var settingsProp = _wrapperSO.FindProperty("settings");

        settingsProp.isExpanded = EditorGUILayout.Foldout(settingsProp.isExpanded, "Current Settings", true);
        if (settingsProp.isExpanded)
        {
            EditorGUI.indentLevel++;
            DrawChildrenRecursive(settingsProp);
            EditorGUI.indentLevel--;
        }

        if (_wrapperSO.hasModifiedProperties)
        {
            _wrapperSO.ApplyModifiedPropertiesWithoutUndo();
            SetCurrentSettings(_wrapper.settings);
        }

        // Legend
        if (_localOverridePaths.Count > 0 || DirtyPaths.Count > 0)
        {
            EditorGUILayout.Space(2);
            var prev = GUI.backgroundColor;
            if (_localOverridePaths.Count > 0)
            {
                GUI.backgroundColor = OverrideTint;
                EditorGUILayout.HelpBox("Highlighted blue = local override", MessageType.None);
            }
            if (DirtyPaths.Count > 0)
            {
                GUI.backgroundColor = DirtyTint;
                EditorGUILayout.HelpBox("Highlighted yellow = unsaved change", MessageType.None);
            }
            GUI.backgroundColor = prev;
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Save to Base File"))
        {
            InvokeMethod("SaveToBaseFile", new HashSet<string>(DirtyPaths));
            DirtyPaths.Clear();
            _stateInitialized = false;
        }

        if (GUILayout.Button("Save to Local File"))
        {
            InvokeMethod("SaveToLocalFile", new HashSet<string>(DirtyPaths));
            DirtyPaths.Clear();
            _stateInitialized = false;
        }

        if (GUILayout.Button("Load from File(s)"))
        {
            InvokeMethod("LoadFromFile");
            DirtyPaths.Clear();
            _stateInitialized = false;
        }

        EditorGUI.BeginDisabledGroup(!Application.isPlaying);

        if (GUILayout.Button("Broadcast Settings Loaded"))
        {
            InvokeMethod("BroadcastSettingsLoaded");
        }

        EditorGUI.EndDisabledGroup();
    }

    private void InvokeMethod(string name, params object[] args)
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var method = target.GetType().GetMethod(name, flags);
        method?.Invoke(target, args.Length > 0 ? args : null);
    }

    /// Reads both JSON files to refresh override paths and, on first call after a
    /// load/save/reset, computes which default fields are not yet persisted anywhere
    /// and marks them dirty.
    private void RefreshFileState()
    {
        var mgr = (SettingsManagerBase)target;
        var basePath = mgr.GetBaseFilePath();
        var localPath = mgr.GetLocalFilePath();

        _localOverridePaths.Clear();

        JObject mergedObj = new JObject();

        if (!string.IsNullOrEmpty(basePath) && File.Exists(basePath))
        {
            try
            {
                mergedObj = JObject.Parse(File.ReadAllText(basePath));
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
        {
            try
            {
                JObject localObj = JObject.Parse(File.ReadAllText(localPath));
                FlattenPaths(localObj, "", _localOverridePaths);
                mergedObj.Merge(localObj, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            }
            catch { }
        }

        if (!_stateInitialized)
        {
            _stateInitialized = true;

            // Any default leaf path not present in either file is dirty — it hasn't been saved yet.
            var defaultInstance = (AppSettings)Activator.CreateInstance(_settingsType);
            JObject defaultObj = JObject.Parse(JsonUtility.ToJson(defaultInstance));

            var allDefaultPaths = new HashSet<string>();
            FlattenPaths(defaultObj, "", allDefaultPaths);

            var persistedPaths = new HashSet<string>();
            FlattenPaths(mergedObj, "", persistedPaths);

            foreach (var path in allDefaultPaths)
            {
                if (!persistedPaths.Contains(path))
                    DirtyPaths.Add(path);
            }
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
            bool isDirty = DirtyPaths.Contains(jsonPath);
            bool hasChildren = iter.hasVisibleChildren;

            Color prevBg = GUI.backgroundColor;

            if (!hasChildren)
            {
                if (isDirty)
                    GUI.backgroundColor = DirtyTint;
                else if (isOverridden)
                    GUI.backgroundColor = OverrideTint;
            }

            if (hasChildren)
            {
                Color? foldoutTint = null;
                string childPrefix = jsonPath + ".";
                foreach (var p in DirtyPaths)
                {
                    if (p.StartsWith(childPrefix) || p == jsonPath)
                    {
                        foldoutTint = DirtyTint;
                        break;
                    }
                }
                if (foldoutTint == null)
                {
                    foreach (var p in _localOverridePaths)
                    {
                        if (p.StartsWith(childPrefix) || p == jsonPath)
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
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(iter, false);
                if (EditorGUI.EndChangeCheck())
                {
                    DirtyPaths.Add(jsonPath);
                }
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
