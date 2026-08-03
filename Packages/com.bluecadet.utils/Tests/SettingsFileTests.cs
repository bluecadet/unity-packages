using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Utils.Tests
{
	[TestFixture]
	public class SettingsFileTests
	{
		public class TestSettings
		{
			public GeneralSettings general = new();

			public class GeneralSettings
			{
				public bool debugMode;
				public bool showCursor;
				public int targetFrameRate = 60;
				public string label = string.Empty;
				public string[] tags = Array.Empty<string>();
			}
		}

		private string _tempDir;

		[SetUp]
		public void SetUp()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), "SettingsFileTests_" + Guid.NewGuid());
			Directory.CreateDirectory(_tempDir);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_tempDir))
				Directory.Delete(_tempDir, true);
		}

		private AppEnvironment MakeEnvironment(string argsText = "") =>
			AppEnvironment.ForTest(argsText, _tempDir, "TEST-MACHINE");

		private void WriteBase(string json) => File.WriteAllText(Path.Combine(_tempDir, "settings.json"), json);
		private void WriteMachine(string json) => File.WriteAllText(Path.Combine(_tempDir, "settings.TEST-MACHINE.json"), json);
		private void WriteLocal(string json) => File.WriteAllText(Path.Combine(_tempDir, "settings.local.json"), json);

		[Test]
		public void Value_NoFilesExist_ReturnsDefaults()
		{
			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value, Is.Not.Null);
			Assert.That(settingsFile.Value.general.targetFrameRate, Is.EqualTo(60));
		}

		[Test]
		public void Value_BaseFileOnly_LoadsBaseValues()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": true } }");

			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.debugMode, Is.True);
		}

		[Test]
		public void Value_MachineOverridesBase_MachineValueWins()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");
			WriteMachine(@"{ ""general"": { ""debugMode"": true } }");

			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.debugMode, Is.True);
		}

		[Test]
		public void Value_LocalOverridesMachine_LocalValueWins()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");
			WriteMachine(@"{ ""general"": { ""debugMode"": false } }");
			WriteLocal(@"{ ""general"": { ""debugMode"": true } }");

			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.debugMode, Is.True);
		}

		[Test]
		public void Value_CliSetOverridesLocal_CliValueWins()
		{
			WriteLocal(@"{ ""general"": { ""debugMode"": false } }");

			var settingsFile = MakeEnvironment("--set general.debugMode=true").SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.debugMode, Is.True);
		}

		[Test]
		public void Value_RepeatedSet_AllOccurrencesApply_LaterWinsOnSamePath()
		{
			var settingsFile = MakeEnvironment(
				"--set general.debugMode=false --set general.showCursor=true --set general.debugMode=true"
			).SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.debugMode, Is.True);
			Assert.That(settingsFile.Value.general.showCursor, Is.True);
		}

		[Test]
		public void Value_SetWithJsonLiteral_ParsesAsTypedValue()
		{
			var settingsFile = MakeEnvironment("--set general.targetFrameRate=30").SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.targetFrameRate, Is.EqualTo(30));
		}

		[Test]
		public void Value_SetWithNonJsonToken_FallsBackToPlainString()
		{
			var settingsFile = MakeEnvironment("--set general.label=not-json").SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.label, Is.EqualTo("not-json"));
		}

		[Test]
		public void Value_ArraysReplaceRatherThanConcat()
		{
			WriteBase(@"{ ""general"": { ""tags"": [""a"", ""b""] } }");
			WriteLocal(@"{ ""general"": { ""tags"": [""c""] } }");

			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.tags, Is.EqualTo(new[] { "c" }));
		}

		[Test]
		public void LoadedPaths_MissingTiers_AreSkipped()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": true } }");

			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.LoadedPaths.Count, Is.EqualTo(1));
			Assert.That(settingsFile.LoadedPaths[0], Does.EndWith("settings.json"));
		}

		[Test]
		public void Value_MalformedTier_WarnsAndIsSkipped()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": true } }");
			WriteLocal(@"not valid json {{{");

			LogAssert.Expect(LogType.Warning, new Regex("Malformed Local settings file"));

			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.Value.general.debugMode, Is.True);
		}

		[Test]
		public void Reload_PicksUpFileChanges_AndRaisesOnReloaded()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");
			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			TestSettings reloaded = null;
			settingsFile.OnReloaded += value => reloaded = value;

			WriteBase(@"{ ""general"": { ""debugMode"": true } }");
			settingsFile.Reload();

			Assert.That(settingsFile.Value.general.debugMode, Is.True);
			Assert.That(reloaded, Is.Not.Null);
			Assert.That(reloaded.general.debugMode, Is.True);
		}

		[Test]
		public void Explain_ReportsWinningTierAndValue()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");
			WriteMachine(@"{ ""general"": { ""debugMode"": true } }");

			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			var explanation = settingsFile.Explain("general.debugMode");

			Assert.That(explanation.Tier, Is.EqualTo(SettingsTier.Machine));
			Assert.That((bool)explanation.Value, Is.True);
		}

		[Test]
		public void Explain_CliTierWins_ReportsCliTier()
		{
			WriteBase(@"{ ""general"": { ""debugMode"": false } }");

			var settingsFile = MakeEnvironment("--set general.debugMode=true").SettingsFile<TestSettings>();

			var explanation = settingsFile.Explain("general.debugMode");

			Assert.That(explanation.Tier, Is.EqualTo(SettingsTier.Cli));
			Assert.That((bool)explanation.Value, Is.True);
		}

		[Test]
		public void Explain_NoTierSetsPath_ReturnsNoneAndNullValue()
		{
			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			var explanation = settingsFile.Explain("general.unknownField");

			Assert.That(explanation.Tier, Is.Null);
			Assert.That(explanation.Value, Is.Null);
		}

		[Test]
		public void PathFor_FileTiers_ReturnPathsUnderDataPath()
		{
			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.PathFor(SettingsTier.Base), Is.EqualTo(Path.Combine(_tempDir, "settings.json")));
			Assert.That(settingsFile.PathFor(SettingsTier.Machine), Is.EqualTo(Path.Combine(_tempDir, "settings.TEST-MACHINE.json")));
			Assert.That(settingsFile.PathFor(SettingsTier.Local), Is.EqualTo(Path.Combine(_tempDir, "settings.local.json")));
		}

		[Test]
		public void PathFor_CliTier_ReturnsNonNullPseudoPath()
		{
			var settingsFile = MakeEnvironment().SettingsFile<TestSettings>();

			Assert.That(settingsFile.PathFor(SettingsTier.Cli), Is.Not.Null);
		}
	}
}
