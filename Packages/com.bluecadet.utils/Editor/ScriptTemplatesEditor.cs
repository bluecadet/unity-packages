using UnityEditor;
using UnityEngine;
using System.IO;
using System;

#if (UNITY_EDITOR)
namespace Bluecadet {
    public class ScriptTemplatesEditor
    {
        private readonly static string monoTemplate = @"using UnityEngine;

namespace {0} {{
    public class #SCRIPTNAME# : MonoBehaviour {{

        void Start() {{
        
        }}

        void Update() {{
        
        }}

    }}
}}";

        private readonly static string singletonTemplate = @"using UnityEngine;
using Bluecadet;

namespace {0} {{
    public class #SCRIPTNAME# : Singleton<#SCRIPTNAME#> {{

        void Start() {{
        
        }}

        void Update() {{
        
        }}

    }}
}}";

        [MenuItem("Bluecadet/Setup Script Templates")]
        public static void Setup() {
            // Open input dialog to get the namespace
            string ns = EditorInputDialog.Show("Setup Script Templates", "Enter the namespace for the project:", "");
            if (string.IsNullOrEmpty(ns)) return;

            // Create ScriptTemplates folder if it doesn't exist
            string path = Path.Combine(Application.dataPath, "ScriptTemplates");
            Directory.CreateDirectory(path);

            // Create script template for MonoBehaviour
            string monoPath = Path.Combine(path, $"01-{ns}__MonoBehaviour Script-NewMonoBehaviourScript.cs.txt");
            if (!File.Exists(monoPath)) {
                File.WriteAllText(monoPath, string.Format(monoTemplate, ns));
            }

            // Create script template for Singleton
            string singletonPath = Path.Combine(path, $"02-{ns}__Singleton Script-NewSingletonScript.cs.txt");
            if (!File.Exists(singletonPath)) {
                File.WriteAllText(singletonPath, string.Format(singletonTemplate, ns));
            }
        }
    }
}
#endif