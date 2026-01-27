using System;

using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Bluecadet.Utils {

    [Serializable]
    public partial class AppSettings {

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

    [ExecuteInEditMode]
    public class SettingsManager : Singleton<SettingsManager> {
        public event Action<AppSettings> OnSettingsLoaded;

        public static AppSettings Settings {
            get {
                return Get().currentSettings;
            }
        }

        [SerializeField]
        private string fileName = "settings.json";

        // NonSerialized so settings aren't baked into the scene/prefab.
        // Always populated at runtime via LoadFromFile().
        // Drawn in the inspector via SettingsManagerEditor using a temporary ScriptableObject wrapper.
        [NonSerialized]
        public AppSettings currentSettings = new();

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

        public void LoadFromFile() {
            try {
                string filePath = Path.Combine(Application.streamingAssetsPath, fileName);
                string jsonString = File.ReadAllText(filePath);
                currentSettings = JsonUtility.FromJson<AppSettings>(jsonString);
            }
            catch (Exception ex) {
                Debug.LogException(ex);
                Debug.LogWarning("Unable to load settings. Creating new settings object and saving to file.");

                currentSettings = new AppSettings();
                SaveToFile();
            }
            finally {
                ApplyGeneralSettings();
                BroadcastSettingsLoaded();
            }
        }
        
        void SaveToFile() {
            string filePath = Path.Combine(Application.streamingAssetsPath, fileName);
            string jsonString = JsonUtility.ToJson(currentSettings, true);
            File.WriteAllText(filePath, jsonString);
        }

        void BroadcastSettingsLoaded() {
            OnSettingsLoaded?.Invoke(currentSettings);
        }

        void ApplyGeneralSettings() {
            AppSettings.GeneralSettings s = currentSettings.general;

            Screen.SetResolution(s.screenSize.x, s.screenSize.y, s.fullscreen);
            Cursor.visible = s.showCursor;
        }
    }
}