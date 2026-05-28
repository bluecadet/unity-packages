using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Newtonsoft.Json.Linq;
using Bluecadet.Utils;

namespace Bluecadet.Utils.Tests
{
    [TestFixture]
    public class SettingsManagerTests
    {
        public class TestSettingsManager : SettingsManager<AppSettings>
        {
            public string BasePath;
            public string InstancePath;
            public string LocalPath;

            public override string GetBaseFilePath() => BasePath;
            public override string GetInstanceFilePath() => InstancePath;
            public override string GetLocalFilePath() => LocalPath;
        }

        private string _tempDir;
        private GameObject _go;
        private TestSettingsManager _manager;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "SettingsManagerTests_" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);

            _go = new GameObject("SettingsManager");
            _manager = _go.AddComponent<TestSettingsManager>();
            _manager.BasePath = Path.Combine(_tempDir, "settings.json");
            _manager.InstancePath = Path.Combine(_tempDir, "settings.TEST-MACHINE.json");
            _manager.LocalPath = Path.Combine(_tempDir, "settings.local.json");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_go);
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private void WriteBaseJson(string json) =>
            File.WriteAllText(_manager.BasePath, json);

        private void WriteInstanceJson(string json) =>
            File.WriteAllText(_manager.InstancePath, json);

        private void WriteLocalJson(string json) =>
            File.WriteAllText(_manager.LocalPath, json);

        private JObject ReadBaseJson() =>
            JObject.Parse(File.ReadAllText(_manager.BasePath));

        private JObject ReadInstanceJson() =>
            JObject.Parse(File.ReadAllText(_manager.InstancePath));

        private JObject ReadLocalJson() =>
            JObject.Parse(File.ReadAllText(_manager.LocalPath));

        // ── Load tests ──────────────────────────────────────────────────────────

        [Test]
        public void LoadFromFile_BaseFileOnly_LoadsBaseValues()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": true } }");

            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings.general.debugMode, Is.True,
                "debugMode from base file should be true.");
        }

        [Test]
        public void LoadFromFile_LocalOverridesBase_LocalValueWins()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": true } }");

            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings.general.debugMode, Is.True,
                "Local override should win over base value.");
        }

        [Test]
        public void LoadFromFile_LocalOnlyOverridesSpecifiedFields_OtherFieldsUnchanged()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": true, ""showCursor"": true } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": false } }");

            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings.general.debugMode, Is.False,
                "Local override should set debugMode to false.");
            Assert.That(_manager.currentSettings.general.showCursor, Is.True,
                "showCursor not in local override should remain true from base.");
        }

        [Test]
        public void LoadFromFile_MissingBaseFile_UsesDefaults()
        {
            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings, Is.Not.Null,
                "currentSettings should not be null when no file exists.");
            Assert.That(_manager.currentSettings.general.debugMode, Is.EqualTo(new AppSettings().general.debugMode),
                "debugMode should be the default value.");
        }

#if UNITY_EDITOR
        [Test]
        public void LoadFromFile_MissingBaseFile_CreatesBaseFile()
        {
            _manager.LoadFromFile();

            Assert.That(File.Exists(_manager.BasePath), Is.True,
                "LoadFromFile should create a base file when none exists.");
        }
#endif

        // ── SaveToBaseFile tests ─────────────────────────────────────────────────

        [Test]
        public void SaveToBaseFile_DirtyPath_WritesValueToFile()
        {
            _manager.LoadFromFile();
            _manager.currentSettings.general.debugMode = true;

            _manager.SaveToBaseFile(new[] { "general.debugMode" });

            JObject saved = ReadBaseJson();
            Assert.That((bool)saved["general"]["debugMode"], Is.True,
                "Saved base file should contain debugMode = true.");
        }

        [Test]
        public void SaveToBaseFile_DirtyPath_PreservesOtherFields()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false, ""showCursor"": true } }");
            _manager.LoadFromFile();
            _manager.currentSettings.general.debugMode = true;

            _manager.SaveToBaseFile(new[] { "general.debugMode" });

            JObject saved = ReadBaseJson();
            Assert.That((bool)saved["general"]["showCursor"], Is.True,
                "showCursor should be preserved in base file after saving only debugMode.");
        }

        [Test]
        public void SaveToBaseFile_RemovesPathFromLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false, ""showCursor"": false } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": true, ""showCursor"": true } }");
            _manager.LoadFromFile();

            _manager.SaveToBaseFile(new[] { "general.debugMode" });

            Assert.That(File.Exists(_manager.LocalPath), Is.True,
                "Local file should still exist (showCursor override remains).");
            JObject local = ReadLocalJson();
            Assert.That(local["general"]?["debugMode"], Is.Null,
                "debugMode should be removed from local file after saving to base.");
        }

        [Test]
        public void SaveToBaseFile_RemovesOnlyPath_DeletesLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": true } }");
            _manager.LoadFromFile();

            _manager.SaveToBaseFile(new[] { "general.debugMode" });

            Assert.That(File.Exists(_manager.LocalPath), Is.False,
                "Local file should be deleted when its only override is promoted to base.");
        }

        [Test]
        public void SaveToBaseFile_NoDirtyPaths_NoFile_CreatesDefaultFile()
        {
            _manager.SaveToBaseFile(Array.Empty<string>());

            Assert.That(File.Exists(_manager.BasePath), Is.True,
                "SaveToBaseFile with no dirty paths should create a default base file when none exists.");
        }

        [Test]
        public void SaveToBaseFile_NoDirtyPaths_FileExists_IsNoOp()
        {
            string original = @"{ ""general"": { ""debugMode"": true } }";
            WriteBaseJson(original);

            _manager.SaveToBaseFile(Array.Empty<string>());

            string afterSave = File.ReadAllText(_manager.BasePath);
            Assert.That(afterSave, Is.EqualTo(original),
                "SaveToBaseFile with no dirty paths should leave existing file unchanged.");
        }

        // ── SaveToLocalFile tests ────────────────────────────────────────────────

        [Test]
        public void SaveToLocalFile_ValueDiffersFromBase_WrittenToLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            _manager.LoadFromFile();
            _manager.currentSettings.general.debugMode = true;

            _manager.SaveToLocalFile(new[] { "general.debugMode" });

            Assert.That(File.Exists(_manager.LocalPath), Is.True,
                "Local file should be created when a value differs from base.");
            JObject local = ReadLocalJson();
            Assert.That((bool)local["general"]["debugMode"], Is.True,
                "Local file should contain the overridden debugMode value.");
        }

        [Test]
        public void SaveToLocalFile_ValueMatchesBase_NotWrittenToLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            _manager.LoadFromFile();

            _manager.SaveToLocalFile(new[] { "general.debugMode" });

            bool localHasDebugMode = File.Exists(_manager.LocalPath)
                && ReadLocalJson()["general"]?["debugMode"] != null;
            Assert.That(localHasDebugMode, Is.False,
                "A value matching base should not be written to the local file.");
        }

        [Test]
        public void SaveToLocalFile_AllMatchBase_DeletesLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": true } }");
            _manager.LoadFromFile();

            // Reset in-memory value back to match base
            _manager.currentSettings.general.debugMode = false;

            _manager.SaveToLocalFile(new[] { "general.debugMode" });

            Assert.That(File.Exists(_manager.LocalPath), Is.False,
                "Local file should be deleted when all overrides match base values.");
        }

        [Test]
        public void SaveToLocalFile_MixedPaths_OnlyDifferingWritten()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false, ""showCursor"": false } }");
            _manager.LoadFromFile();
            _manager.currentSettings.general.debugMode = true;
            // showCursor stays false (matches base)

            _manager.SaveToLocalFile(new[] { "general.debugMode", "general.showCursor" });

            Assert.That(File.Exists(_manager.LocalPath), Is.True,
                "Local file should exist because debugMode differs from base.");
            JObject local = ReadLocalJson();
            Assert.That(local["general"]?["debugMode"], Is.Not.Null,
                "debugMode should be present in local file (differs from base).");
            Assert.That(local["general"]?["showCursor"], Is.Null,
                "showCursor should not be in local file (matches base).");
        }

        // ── SaveToInstanceFile tests ─────────────────────────────────────────────

        [Test]
        public void SaveToInstanceFile_DirtyPath_WritesValueToFile()
        {
            _manager.LoadFromFile();
            _manager.currentSettings.general.debugMode = true;

            _manager.SaveToInstanceFile(new[] { "general.debugMode" });

            JObject saved = ReadInstanceJson();
            Assert.That((bool)saved["general"]["debugMode"], Is.True,
                "Saved instance file should contain debugMode = true.");
        }

        [Test]
        public void SaveToInstanceFile_RemovesPathFromLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false, ""showCursor"": false } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": true, ""showCursor"": true } }");
            _manager.LoadFromFile();

            _manager.SaveToInstanceFile(new[] { "general.debugMode" });

            Assert.That(File.Exists(_manager.LocalPath), Is.True,
                "Local file should still exist (showCursor override remains).");
            JObject local = ReadLocalJson();
            Assert.That(local["general"]?["debugMode"], Is.Null,
                "debugMode should be removed from local file after saving to instance.");
        }

        [Test]
        public void SaveToInstanceFile_OnlyLocalPath_DeletesLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": true } }");
            _manager.LoadFromFile();

            _manager.SaveToInstanceFile(new[] { "general.debugMode" });

            Assert.That(File.Exists(_manager.LocalPath), Is.False,
                "Local file should be deleted when its only override is promoted to instance.");
        }

        // ── Instance layer load tests ────────────────────────────────────────────

        [Test]
        public void LoadFromFile_InstanceOverridesBase_InstanceValueWins()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            WriteInstanceJson(@"{ ""general"": { ""debugMode"": true } }");

            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings.general.debugMode, Is.True,
                "Instance override should win over base value.");
        }

        [Test]
        public void LoadFromFile_LocalOverridesInstance_LocalValueWins()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            WriteInstanceJson(@"{ ""general"": { ""debugMode"": true } }");
            WriteLocalJson(@"{ ""general"": { ""debugMode"": false } }");

            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings.general.debugMode, Is.False,
                "Local override should win over instance value.");
        }

        [Test]
        public void SaveToLocalFile_ValueMatchesInstance_NotWrittenToLocalFile()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            WriteInstanceJson(@"{ ""general"": { ""debugMode"": true } }");
            _manager.LoadFromFile();
            // In-memory value matches the effective (base+instance) value — no local override needed.

            _manager.SaveToLocalFile(new[] { "general.debugMode" });

            bool localHasDebugMode = File.Exists(_manager.LocalPath)
                && ReadLocalJson()["general"]?["debugMode"] != null;
            Assert.That(localHasDebugMode, Is.False,
                "A value matching the effective base+instance should not be written to local.");
        }

        // ── Round-trip tests ─────────────────────────────────────────────────────

        [Test]
        public void RoundTrip_SaveToBase_ThenLoad_PreservesValues()
        {
            _manager.LoadFromFile();
            _manager.currentSettings.general.debugMode = true;
            _manager.currentSettings.general.showCursor = true;

            _manager.SaveToBaseFile(new[] { "general.debugMode", "general.showCursor" });

            // Reset in-memory state
            _manager.currentSettings = new AppSettings();
            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings.general.debugMode, Is.True,
                "debugMode should survive a save-to-base / reload round-trip.");
            Assert.That(_manager.currentSettings.general.showCursor, Is.True,
                "showCursor should survive a save-to-base / reload round-trip.");
        }

        [Test]
        public void RoundTrip_SaveToLocal_ThenLoad_LocalWinsOverBase()
        {
            WriteBaseJson(@"{ ""general"": { ""debugMode"": false } }");
            _manager.LoadFromFile();
            _manager.currentSettings.general.debugMode = true;

            _manager.SaveToLocalFile(new[] { "general.debugMode" });

            // Reset in-memory state and reload
            _manager.currentSettings = new AppSettings();
            _manager.LoadFromFile();

            Assert.That(_manager.currentSettings.general.debugMode, Is.True,
                "Local override should win over base value after a save-to-local / reload round-trip.");
        }
    }
}
