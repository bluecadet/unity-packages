using System;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Bluecadet.Utils.Editor;

namespace Bluecadet.Utils.Tests
{
	[TestFixture]
	public class SettingsTierWriterTests
	{
		private string _tempDir;

		[SetUp]
		public void SetUp()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), "SettingsTierWriterTests_" + Guid.NewGuid());
			Directory.CreateDirectory(_tempDir);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_tempDir))
				Directory.Delete(_tempDir, true);
		}

		private string BasePath => Path.Combine(_tempDir, "settings.json");
		private string MachinePath => Path.Combine(_tempDir, "settings.TEST-MACHINE.json");
		private string LocalPath => Path.Combine(_tempDir, "settings.local.json");

		private AppEnvironment MakeEnvironment() =>
			new AppEnvironment(_tempDir, "TEST-MACHINE", CommandLineArgs.ParseText(string.Empty));

		private SettingsTierWriter MakeWriter() =>
			new SettingsTierWriter(new SettingsCascade(MakeEnvironment(), "settings"));

		private void WriteBase(string json) => File.WriteAllText(BasePath, json);
		private void WriteMachine(string json) => File.WriteAllText(MachinePath, json);
		private void WriteLocal(string json) => File.WriteAllText(LocalPath, json);

		private static JObject Read(string path) => File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;

		[Test]
		public void SaveDirtyPaths_SparseWriteToBase_PreservesUnrelatedKeys()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false, ""other"": ""keep"" } }");
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true, ""other"": ""keep"" } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "general.debugMode" });

			JObject written = Read(BasePath);
			Assert.That((bool)written["general"]["debugMode"], Is.True);
			Assert.That((string)written["general"]["other"], Is.EqualTo("keep"));
		}

		[Test]
		public void SaveDirtyPaths_NestedDottedPath_CreatesNestedObjects()
		{
			var fullValue = JObject.Parse(@"{ ""a"": { ""b"": { ""c"": 5 } } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "a.b.c" });

			JObject written = Read(BasePath);
			Assert.That((int)written["a"]["b"]["c"], Is.EqualTo(5));
		}

		[Test]
		public void SaveDirtyPaths_ToBase_StripsSamePathFromLocal_UnrelatedLocalKeysSurvive()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");
			WriteLocal(@"{ ""general"": { ""debugMode"": true, ""other"": ""keep"" } }");
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true, ""other"": ""keep"" } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "general.debugMode" });

			JObject writtenBase = Read(BasePath);
			Assert.That((bool)writtenBase["general"]["debugMode"], Is.True);

			JObject writtenLocal = Read(LocalPath);
			Assert.That(writtenLocal, Is.Not.Null);
			Assert.That(((JObject)writtenLocal["general"]).ContainsKey("debugMode"), Is.False);
			Assert.That((string)writtenLocal["general"]["other"], Is.EqualTo("keep"));
		}

		[Test]
		public void SaveDirtyPaths_ToMachine_StripsSamePathFromLocal()
		{
			WriteMachine(@"{ ""general"": { ""debugMode"": false } }");
			WriteLocal(@"{ ""general"": { ""debugMode"": true } }");
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Machine, fullValue, new[] { "general.debugMode" });

			JObject writtenMachine = Read(MachinePath);
			Assert.That((bool)writtenMachine["general"]["debugMode"], Is.True);

			Assert.That(File.Exists(LocalPath), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_ToLocal_LeafEqualsEffectiveValue_RemovesRedundantOverride()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": true } }");
			WriteLocal(@"{ ""general"": { ""debugMode"": true } }");
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Local, fullValue, new[] { "general.debugMode" });

			Assert.That(File.Exists(LocalPath), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_PathMissingFromValue_IsSkipped_OtherPathsStillWritten()
		{
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "general.debugMode", "general.notSerialized" });

			JObject written = Read(BasePath);
			Assert.That((bool)written["general"]["debugMode"], Is.True);
			Assert.That(((JObject)written["general"]).ContainsKey("notSerialized"), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_ExplicitJsonNull_IsWritten()
		{
			var fullValue = JObject.Parse(@"{ ""general"": { ""profile"": null } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "general.profile" });

			JObject written = Read(BasePath);
			Assert.That(((JObject)written["general"]).ContainsKey("profile"), Is.True);
			Assert.That(written["general"]["profile"].Type, Is.EqualTo(JTokenType.Null));
		}

		[Test]
		public void SaveDirtyPaths_ToLocal_FloatLeafEqualsEffectiveValue_RemovesRedundantOverride()
		{
			WriteBase(@"{ ""general"": { ""speed"": 0.1 } }");
			WriteLocal(@"{ ""general"": { ""speed"": 0.1 } }");

			// A float boxed by the serializer widens to 0.10000000149..., so it must be normalized before
			// it is compared against the 0.1 parsed out of the Base file.
			var fullValue = new JObject { ["general"] = new JObject { ["speed"] = 0.1f } };

			MakeWriter().SaveDirtyPaths(SettingsTier.Local, fullValue, new[] { "general.speed" });

			Assert.That(File.Exists(LocalPath), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_ToLocal_LeafDiffersFromEffectiveValue_Writes()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Local, fullValue, new[] { "general.debugMode" });

			JObject writtenLocal = Read(LocalPath);
			Assert.That((bool)writtenLocal["general"]["debugMode"], Is.True);
		}

		[Test]
		public void SaveDirtyPaths_StrippingEmptiesFile_DeletesFileAndMeta_PrunesEmptyParents()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");
			WriteLocal(@"{ ""general"": { ""debugMode"": true } }");
			File.WriteAllText(LocalPath + ".meta", "fileFormatVersion: 2\nguid: 00000000000000000000000000000000");
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "general.debugMode" });

			Assert.That(File.Exists(LocalPath), Is.False);
			Assert.That(File.Exists(LocalPath + ".meta"), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_ToBase_UnrelatedDirtyPathNoOpOnLocal_PreservesDeliberatelyEmptyObject()
		{
			// "advanced" is an object the user deliberately left empty in Local; the "advanced.feature" dirty
			// path has nothing to remove there. That no-op must not prune "advanced" away just because
			// "general.debugMode" (an unrelated dirty path) genuinely strips its own Local override.
			WriteLocal(@"{ ""general"": { ""debugMode"": true }, ""advanced"": {} }");
			var fullValue = JObject.Parse(@"{ ""general"": { ""debugMode"": true }, ""advanced"": { ""feature"": true } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "general.debugMode", "advanced.feature" });

			JObject writtenLocal = Read(LocalPath);
			Assert.That(writtenLocal, Is.Not.Null);
			Assert.That(writtenLocal.ContainsKey("general"), Is.False);
			Assert.That(writtenLocal.ContainsKey("advanced"), Is.True);
			Assert.That(((JObject)writtenLocal["advanced"]).HasValues, Is.False);
		}

		[Test]
		public void SaveDirtyPaths_TargetCli_Throws()
		{
			var fullValue = new JObject();

			Assert.Throws<ArgumentException>(() =>
				MakeWriter().SaveDirtyPaths(SettingsTier.Cli, fullValue, new[] { "general.debugMode" }));
		}

		[Test]
		public void DeleteTier_RemovesFile()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": true } }");

			MakeWriter().DeleteTier(SettingsTier.Base);

			Assert.That(File.Exists(BasePath), Is.False);
		}

		[Test]
		public void DeleteTier_Cli_Throws()
		{
			Assert.Throws<ArgumentException>(() => MakeWriter().DeleteTier(SettingsTier.Cli));
		}

		[Test]
		public void SaveDirtyPaths_ArrayLeaf_IsWrittenWholesale()
		{
			var fullValue = JObject.Parse(@"{ ""general"": { ""tags"": [""a"", ""b""] } }");

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, fullValue, new[] { "general.tags" });

			JObject written = Read(BasePath);
			Assert.That(written["general"]["tags"].ToObject<string[]>(), Is.EqualTo(new[] { "a", "b" }));
		}

		// --- Deeply nested settings classes -------------------------------------------------------
		//
		// A real settings class nests objects several levels down, so every tier-write rule has to hold
		// for dotted paths like "app.display.window.fullscreen", not just for one level of grouping.

		private const string _deepPath = "app.display.window.fullscreen";
		private const string _deepSiblingPath = "app.display.window.scale";

		private sealed class DeepSettings
		{
			public AppSection app = new();
		}

		private sealed class AppSection
		{
			public string label = "root";
			public DisplaySection display = new();
		}

		private sealed class DisplaySection
		{
			public int index = 1;
			public WindowSection window = new();
		}

		private sealed class WindowSection
		{
			public bool fullscreen;
			public float scale = 1f;
			public string[] tags = { "a" };
		}

		/// <summary>Serializes a settings class exactly the way the editor pane does before saving.</summary>
		private static JObject Serialize(DeepSettings settings) => JObject.FromObject(settings, SettingsJson.Serializer);

		[Test]
		public void SaveDirtyPaths_DeepPathToBase_CreatesWholeNestedChain()
		{
			var settings = new DeepSettings();
			settings.app.display.window.fullscreen = true;

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, Serialize(settings), new[] { _deepPath });

			JObject written = Read(BasePath);
			Assert.That((bool)written["app"]["display"]["window"]["fullscreen"], Is.True);

			// Sparse: only the dirty leaf, none of the siblings along the way.
			Assert.That(((JObject)written["app"]).ContainsKey("label"), Is.False);
			Assert.That(((JObject)written["app"]["display"]).ContainsKey("index"), Is.False);
			Assert.That(((JObject)written["app"]["display"]["window"]).ContainsKey("scale"), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_DeepPathToBase_PreservesUnrelatedDeepSiblings()
		{
			WriteBase(@"{
				""app"": {
					""label"": ""keep"",
					""display"": {
						""index"": 3,
						""window"": { ""fullscreen"": false, ""scale"": 2.0 }
					},
					""audio"": { ""mixer"": { ""volume"": 0.25 } }
				}
			}");

			var settings = new DeepSettings();
			settings.app.display.window.fullscreen = true;

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, Serialize(settings), new[] { _deepPath });

			JObject written = Read(BasePath);
			Assert.That((bool)written["app"]["display"]["window"]["fullscreen"], Is.True);
			Assert.That((string)written["app"]["label"], Is.EqualTo("keep"));
			Assert.That((int)written["app"]["display"]["index"], Is.EqualTo(3));
			Assert.That((double)written["app"]["display"]["window"]["scale"], Is.EqualTo(2.0));
			Assert.That((double)written["app"]["audio"]["mixer"]["volume"], Is.EqualTo(0.25));
		}

		[Test]
		public void SaveDirtyPaths_DeepPathToBase_StripsDeepShadowFromLocal_AndPrunesEmptiedParents()
		{
			WriteLocal(@"{ ""app"": { ""label"": ""keep"", ""display"": { ""window"": { ""fullscreen"": true } } } }");

			var settings = new DeepSettings();
			settings.app.display.window.fullscreen = true;

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, Serialize(settings), new[] { _deepPath });

			JObject writtenLocal = Read(LocalPath);
			Assert.That(writtenLocal, Is.Not.Null);
			Assert.That((string)writtenLocal["app"]["label"], Is.EqualTo("keep"));

			// "window" and then "display" are both emptied by the strip and must not be left behind.
			Assert.That(((JObject)writtenLocal["app"]).ContainsKey("display"), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_DeepPathToBase_StripsDeepShadowFromLocal_KeepsDeepSiblingAndItsParents()
		{
			WriteLocal(@"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": true, ""scale"": 4.0 } } } }");

			var settings = new DeepSettings();
			settings.app.display.window.fullscreen = true;

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, Serialize(settings), new[] { _deepPath });

			JObject writtenLocal = Read(LocalPath);
			Assert.That(((JObject)writtenLocal["app"]["display"]["window"]).ContainsKey("fullscreen"), Is.False);
			Assert.That((double)writtenLocal["app"]["display"]["window"]["scale"], Is.EqualTo(4.0));
		}

		[Test]
		public void SaveDirtyPaths_DeepShadowIsLocalsOnlyValue_DeletesLocalFile()
		{
			WriteLocal(@"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": true } } } }");

			var settings = new DeepSettings();
			settings.app.display.window.fullscreen = true;

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, Serialize(settings), new[] { _deepPath });

			Assert.That(File.Exists(LocalPath), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_DeepPathToLocal_MatchesEffectiveBaseAndMachine_RemovesRedundantOverride()
		{
			WriteBase(@"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": false, ""scale"": 1.0 } } } }");
			WriteMachine(@"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": true } } } }");
			WriteLocal(@"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": true } } } }");

			// Matches the Machine tier, which wins over Base at this depth: Local must not keep the override.
			var settings = new DeepSettings();
			settings.app.display.window.fullscreen = true;

			MakeWriter().SaveDirtyPaths(SettingsTier.Local, Serialize(settings), new[] { _deepPath });

			Assert.That(File.Exists(LocalPath), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_DeepFloatToLocal_MatchesEffectiveValue_RemovesRedundantOverride()
		{
			WriteBase(@"{ ""app"": { ""display"": { ""window"": { ""scale"": 0.1 } } } }");
			WriteLocal(@"{ ""app"": { ""display"": { ""window"": { ""scale"": 0.1 } } } }");

			var settings = new DeepSettings();
			settings.app.display.window.scale = 0.1f;

			MakeWriter().SaveDirtyPaths(SettingsTier.Local, Serialize(settings), new[] { _deepSiblingPath });

			Assert.That(File.Exists(LocalPath), Is.False);
		}

		[Test]
		public void SaveDirtyPaths_DeepPathToLocal_DiffersFromEffectiveValue_WritesWholeNestedChain()
		{
			WriteBase(@"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": false } } } }");

			var settings = new DeepSettings();
			settings.app.display.window.fullscreen = true;

			MakeWriter().SaveDirtyPaths(SettingsTier.Local, Serialize(settings), new[] { _deepPath });

			JObject writtenLocal = Read(LocalPath);
			Assert.That((bool)writtenLocal["app"]["display"]["window"]["fullscreen"], Is.True);
		}

		[Test]
		public void SaveDirtyPaths_DeepArrayLeaf_IsWrittenWholesale()
		{
			var settings = new DeepSettings();
			settings.app.display.window.tags = new[] { "a", "b" };

			MakeWriter().SaveDirtyPaths(SettingsTier.Base, Serialize(settings), new[] { "app.display.window.tags" });

			JObject written = Read(BasePath);
			Assert.That(written["app"]["display"]["window"]["tags"].ToObject<string[]>(), Is.EqualTo(new[] { "a", "b" }));
		}
	}
}
