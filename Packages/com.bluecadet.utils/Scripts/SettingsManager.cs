using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Bluecadet.Utils {

    [Serializable]
    public class AppSettings {

        [Serializable]
        public class GeneralSettings {
            public bool debugMode = false;
            public bool showCursor = false;
            public Vector2Int screenSize = new Vector2Int(1920, 1080);
            public Vector2Int screenPosition = new Vector2Int(0, 0);
            public bool fullscreen = false;
        }

        public GeneralSettings general = new();
    }

    /// Non-generic base class for SettingsManager<TSettings>.
    /// Exists so the custom editor can target all generic variants.
    [ExecuteInEditMode]
    public abstract class SettingsManagerBase : MonoBehaviour {
        public virtual string GetBaseFilePath() {
            return Path.Combine(Application.streamingAssetsPath, "settings.json");
        }

        public virtual string GetLocalFilePath() {
            return Path.Combine(Application.streamingAssetsPath, "settings.local.json");
        }
    }

    [ExecuteInEditMode]
    public abstract class SettingsManager<TSettings> : SettingsManagerBase
        where TSettings : AppSettings, new() {

        public static SettingsManager<TSettings> Get() => SingletonRegistry<SettingsManager<TSettings>>.Get();
        public static SettingsManager<TSettings> Instance => SingletonRegistry<SettingsManager<TSettings>>.Get();

        public event Action<TSettings> OnSettingsLoaded;

        public static TSettings Settings {
            get {
                return Get()?.currentSettings;
            }
        }

        // NonSerialized so settings aren't baked into the scene/prefab.
        // Always populated at runtime via LoadFromFile().
        // Drawn in the inspector via SettingsManagerEditor using a temporary ScriptableObject wrapper.
        [NonSerialized]
        public TSettings currentSettings = new();


#if UNITY_EDITOR
        /// Tracks which leaf paths have unsaved inspector changes.
        /// Stored on the manager (not the Editor class) so state survives inspector selection changes.
        [NonSerialized]
        public HashSet<string> editorDirtyPaths = new();

        /// Converts settings to JSON using JsonUtility (which handles Unity types correctly).
        private static string ToJson(TSettings settings) {
            return JsonUtility.ToJson(settings, true);
        }

        private static JObject ToJObject(TSettings settings) {
            return JObject.Parse(ToJson(settings));
        }
#endif

        void Start() {
            LoadFromFile();
        }


        void Update() {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null) {
                if (Keyboard.current.cKey.wasPressedThisFrame) {
                    currentSettings.general.showCursor = !currentSettings.general.showCursor;
                    ApplyGeneralSettings();
                }
                if (Keyboard.current.rKey.wasPressedThisFrame) {
                    LoadFromFile();
                }
            }
#else
            if (Input.GetKeyDown(KeyCode.C)) {
                currentSettings.general.showCursor = !currentSettings.general.showCursor;
                ApplyGeneralSettings();
            }
            if (Input.GetKeyDown(KeyCode.R)) {
                LoadFromFile();
            }
#endif
        }

        /// Loads settings from the base file, then merges any local overrides on top.
        public void LoadFromFile() {
            try {
                string basePath = GetBaseFilePath();
                string baseJson = File.ReadAllText(basePath);
                JObject baseObj = JObject.Parse(baseJson);

                string localPath = GetLocalFilePath();
                if (File.Exists(localPath)) {
                    string localJson = File.ReadAllText(localPath);
                    JObject localObj = JObject.Parse(localJson);
                    baseObj.Merge(localObj, new JsonMergeSettings {
                        MergeArrayHandling = MergeArrayHandling.Replace
                    });
                }

                currentSettings = JsonUtility.FromJson<TSettings>(baseObj.ToString());
            } catch (Exception ex) {
                Debug.LogException(ex);
                Debug.LogWarning("Unable to load settings. Using defaults.");

                currentSettings = new TSettings();
#if UNITY_EDITOR
                SaveDefaultsToBaseFile();
#endif
            } finally {
                ApplyGeneralSettings();
                BroadcastSettingsLoaded();
            }
        }

#if UNITY_EDITOR
        /// Writes default settings to the base file. Used when no file exists yet.
        private void SaveDefaultsToBaseFile() {
            string filePath = GetBaseFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            File.WriteAllText(filePath, ToJson(new TSettings()));
        }

        /// Writes only the given dirty fields to the base file.
        /// If a dirty field overlaps with a local override, it is removed from the local file.
        /// If no fields are dirty and no base file exists, saves defaults.
        public void SaveToBaseFile(IEnumerable<string> dirtyPaths) {
            string filePath = GetBaseFilePath();
            bool hasDirty = false;
            foreach (var _ in dirtyPaths) { hasDirty = true; break; }

            if (!hasDirty) {
                if (!File.Exists(filePath)) {
                    SaveDefaultsToBaseFile();
                }
                return;
            }

            JObject baseObj;
            if (File.Exists(filePath)) {
                baseObj = JObject.Parse(File.ReadAllText(filePath));
            } else {
                baseObj = ToJObject(new TSettings());
            }

            JObject currentObj = ToJObject(currentSettings);
            foreach (string path in dirtyPaths) {
                string[] parts = path.Split('.');
                SetNestedValue(baseObj, currentObj, parts, 0);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            File.WriteAllText(filePath, baseObj.ToString(Newtonsoft.Json.Formatting.Indented));
            RemovePathsFromLocalFile(dirtyPaths);
        }

        /// Copies a value at the given key path from source into target,
        /// creating intermediate objects as needed.
        private static void SetNestedValue(JObject target, JObject source, string[] parts, int index) {
            string key = parts[index];
            if (index == parts.Length - 1) {
                // Leaf — copy value
                JToken val = source[key];
                if (val != null)
                    target[key] = val.DeepClone();
            } else {
                // Intermediate — ensure object exists and recurse
                if (target[key] == null || target[key].Type != JTokenType.Object)
                    target[key] = new JObject();
                JObject srcChild = source[key] as JObject;
                if (srcChild != null)
                    SetNestedValue((JObject)target[key], srcChild, parts, index + 1);
            }
        }

        /// Writes only the given dirty fields to the local overrides file.
        /// A dirty field is written only if it differs from the base file value;
        /// if it matches base, it is removed from local (no override needed).
        /// Deletes the local file if no overrides remain.
        public void SaveToLocalFile(IEnumerable<string> dirtyPaths) {
            string basePath = GetBaseFilePath();
            JObject baseObj = File.Exists(basePath)
                ? JObject.Parse(File.ReadAllText(basePath))
                : ToJObject(new TSettings());

            JObject currentObj = ToJObject(currentSettings);

            string localPath = GetLocalFilePath();
            JObject localObj = File.Exists(localPath)
                ? JObject.Parse(File.ReadAllText(localPath))
                : new JObject();

            foreach (string path in dirtyPaths) {
                string[] parts = path.Split('.');
                JToken baseVal = GetNestedValue(baseObj, parts);
                JToken currentVal = GetNestedValue(currentObj, parts);

                if (currentVal != null && !JToken.DeepEquals(baseVal, currentVal)) {
                    SetNestedValue(localObj, currentObj, parts, 0);
                } else {
                    RemoveNestedPath(localObj, parts, 0);
                }
            }

            PruneEmpty(localObj);

            if (localObj.Count == 0) {
                DeleteLocalFile();
            } else {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath));
                File.WriteAllText(localPath, localObj.ToString(Newtonsoft.Json.Formatting.Indented));
            }
        }

        /// Returns the value at the given key path within a JObject, or null if not found.
        private static JToken GetNestedValue(JObject obj, string[] parts) {
            JToken current = obj;
            foreach (var part in parts) {
                if (current is JObject jObj && jObj.TryGetValue(part, out JToken next))
                    current = next;
                else
                    return null;
            }
            return current;
        }

        /// Deletes the local overrides file entirely.
        private void DeleteLocalFile() {
            string localPath = GetLocalFilePath();
            if (File.Exists(localPath))
                File.Delete(localPath);
            if (File.Exists(localPath + ".meta"))
                File.Delete(localPath + ".meta");
        }

        /// Removes the given dot-separated paths from the local overrides file.
        /// Deletes the file if no overrides remain.
        private void RemovePathsFromLocalFile(IEnumerable<string> paths) {
            string localPath = GetLocalFilePath();
            if (!File.Exists(localPath)) return;

            JObject localObj;
            try {
                localObj = JObject.Parse(File.ReadAllText(localPath));
            } catch {
                return;
            }

            foreach (string path in paths) {
                RemoveNestedPath(localObj, path.Split('.'), 0);
            }

            // Prune empty parent objects
            PruneEmpty(localObj);

            if (localObj.Count == 0) {
                DeleteLocalFile();
            } else {
                File.WriteAllText(GetLocalFilePath(), localObj.ToString(Newtonsoft.Json.Formatting.Indented));
            }
        }

        /// Removes a leaf value at the given key path from a JObject.
        private static void RemoveNestedPath(JObject obj, string[] parts, int index) {
            string key = parts[index];
            if (obj[key] == null) return;

            if (index == parts.Length - 1) {
                obj.Remove(key);
            } else if (obj[key] is JObject child) {
                RemoveNestedPath(child, parts, index + 1);
            }
        }

        /// Recursively removes empty JObject children.
        private static void PruneEmpty(JObject obj) {
            var toRemove = new List<string>();
            foreach (var prop in obj.Properties()) {
                if (prop.Value is JObject child) {
                    PruneEmpty(child);
                    if (child.Count == 0)
                        toRemove.Add(prop.Name);
                }
            }
            foreach (var key in toRemove)
                obj.Remove(key);
        }
#endif

        void BroadcastSettingsLoaded() {
            OnSettingsLoaded?.Invoke(currentSettings);
        }

        void ApplyGeneralSettings() {
            AppSettings.GeneralSettings s = currentSettings.general;

            Screen.SetResolution(s.screenSize.x, s.screenSize.y, s.fullscreen);
            Cursor.visible = s.showCursor;
        }
    }

    /// Default concrete SettingsManager using the base AppSettings.
    /// Subclass SettingsManager<TSettings> with your own AppSettings subclass for custom fields.
    public class SettingsManager : SettingsManager<AppSettings> { }
}
