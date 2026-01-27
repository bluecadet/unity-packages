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

    /// Non-generic base class so the custom editor can target all SettingsManager<> variants.
    [ExecuteInEditMode]
    public abstract class SettingsManagerBase : Singleton<SettingsManagerBase> { }

    [ExecuteInEditMode]
    public abstract class SettingsManager<TSettings> : SettingsManagerBase
        where TSettings : AppSettings, new()
    {
        public event Action<TSettings> OnSettingsLoaded;

        public static TSettings Settings {
            get {
                return ((SettingsManager<TSettings>)Get()).currentSettings;
            }
        }

        [SerializeField]
        private string baseFileName = "settings.json";

        [SerializeField]
        private string localFileName = "settings.local.json";

        // NonSerialized so settings aren't baked into the scene/prefab.
        // Always populated at runtime via LoadFromFile().
        // Drawn in the inspector via SettingsManagerEditor using a temporary ScriptableObject wrapper.
        [NonSerialized]
        public TSettings currentSettings = new();


#if UNITY_EDITOR
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
                string basePath = Path.Combine(Application.streamingAssetsPath, baseFileName);
                string baseJson = File.ReadAllText(basePath);
                JObject baseObj = JObject.Parse(baseJson);

                string localPath = Path.Combine(Application.streamingAssetsPath, localFileName);
                if (File.Exists(localPath)) {
                    string localJson = File.ReadAllText(localPath);
                    JObject localObj = JObject.Parse(localJson);
                    baseObj.Merge(localObj, new JsonMergeSettings {
                        MergeArrayHandling = MergeArrayHandling.Replace
                    });
                }

                currentSettings = JsonUtility.FromJson<TSettings>(baseObj.ToString());
            }
            catch (Exception ex) {
                Debug.LogException(ex);
                Debug.LogWarning("Unable to load settings. Using defaults.");

                currentSettings = new TSettings();
#if UNITY_EDITOR
                SaveToBaseFile();
#endif
            }
            finally {
                ApplyGeneralSettings();
                BroadcastSettingsLoaded();
            }
        }

#if UNITY_EDITOR
        /// Writes the full current settings to the base file.
        /// Since all fields are saved to base, the local file is deleted.
        public void SaveToBaseFile() {
            string filePath = Path.Combine(Application.streamingAssetsPath, baseFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            File.WriteAllText(filePath, ToJson(currentSettings));
            DeleteLocalFile();
        }

        /// Reads the existing base file (or defaults), merges only the fields at the
        /// given dot-separated leaf paths from currentSettings, and writes back.
        public void SaveDirtyToBaseFile(IEnumerable<string> dirtyPaths) {
            string filePath = Path.Combine(Application.streamingAssetsPath, baseFileName);
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

        /// Diffs current settings against the base file and writes only the differences
        /// to the local file. Deletes the local file if there are no differences.
        public void SaveToLocalFile() {
            string basePath = Path.Combine(Application.streamingAssetsPath, baseFileName);
            JObject baseObj;
            if (File.Exists(basePath)) {
                baseObj = JObject.Parse(File.ReadAllText(basePath));
            } else {
                baseObj = ToJObject(new TSettings());
            }

            JObject currentObj = ToJObject(currentSettings);
            JObject diff = ComputeDiff(baseObj, currentObj);

            string localPath = Path.Combine(Application.streamingAssetsPath, localFileName);
            if (diff.Count == 0) {
                if (File.Exists(localPath)) {
                    File.Delete(localPath);
                }
                if (File.Exists(localPath + ".meta")) {
                    File.Delete(localPath + ".meta");
                }
            } else {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath));
                File.WriteAllText(localPath, diff.ToString(Newtonsoft.Json.Formatting.Indented));
            }
        }

        /// Recursively compares two JObjects and returns only the properties that differ.
        private static JObject ComputeDiff(JObject baseObj, JObject currentObj) {
            var diff = new JObject();
            foreach (var property in currentObj.Properties()) {
                JToken baseVal = baseObj[property.Name];
                if (baseVal == null) {
                    diff[property.Name] = property.Value;
                } else if (property.Value.Type == JTokenType.Object && baseVal.Type == JTokenType.Object) {
                    JObject childDiff = ComputeDiff((JObject)baseVal, (JObject)property.Value);
                    if (childDiff.Count > 0) {
                        diff[property.Name] = childDiff;
                    }
                } else if (!JToken.DeepEquals(baseVal, property.Value)) {
                    diff[property.Name] = property.Value;
                }
            }
            return diff;
        }

        /// Deletes the local overrides file entirely.
        private void DeleteLocalFile() {
            string localPath = Path.Combine(Application.streamingAssetsPath, localFileName);
            if (File.Exists(localPath))
                File.Delete(localPath);
            if (File.Exists(localPath + ".meta"))
                File.Delete(localPath + ".meta");
        }

        /// Removes the given dot-separated paths from the local overrides file.
        /// Deletes the file if no overrides remain.
        private void RemovePathsFromLocalFile(IEnumerable<string> paths) {
            string localPath = Path.Combine(Application.streamingAssetsPath, localFileName);
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
                File.WriteAllText(localPath, localObj.ToString(Newtonsoft.Json.Formatting.Indented));
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
