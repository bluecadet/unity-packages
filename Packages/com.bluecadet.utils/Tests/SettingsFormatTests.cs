using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Bluecadet.Utils.Tests
{
	/// <summary>
	/// Cascade-level behavior of the YAML/JSON dual-format tiers: per-tier extension probing,
	/// precedence across mixed formats, and the default extension for files that don't exist yet.
	/// </summary>
	[TestFixture]
	public class SettingsFormatTests
	{
		private string _tempDir;

		[SetUp]
		public void SetUp()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), "SettingsFormatTests_" + Guid.NewGuid());
			Directory.CreateDirectory(_tempDir);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_tempDir))
				Directory.Delete(_tempDir, true);
		}

		private AppEnvironment MakeEnvironment(string argsText = "") =>
			new AppEnvironment(_tempDir, "TEST-MACHINE", CommandLineArgs.ParseText(argsText));

		private SettingsCascade MakeCascade(string argsText = "") =>
			new SettingsCascade(MakeEnvironment(argsText), "settings");

		private void WriteFile(string name, string content) =>
			File.WriteAllText(Path.Combine(_tempDir, name), content);

		[Test]
		public void Load_YamlOnlyCascade_MergesAllTiers()
		{
			WriteFile("settings.yaml", "general:\n  debugMode: false\n  label: base\n");
			WriteFile("settings.TEST-MACHINE.yaml", "general:\n  debugMode: true\n");
			WriteFile("settings.local.yaml", "general:\n  label: local\n");

			SettingsCascade cascade = MakeCascade();

			Assert.That((bool)cascade.Merged["general"]["debugMode"], Is.True);
			Assert.That((string)cascade.Merged["general"]["label"], Is.EqualTo("local"));
			Assert.That(cascade.LoadedPaths.Count, Is.EqualTo(3));
			Assert.That(cascade.Warnings, Is.Empty);
		}

		[Test]
		public void Load_MixedFormats_CascadePrecedenceUnchanged()
		{
			WriteFile("settings.json", @"{ ""general"": { ""debugMode"": false, ""label"": ""base"" } }");
			WriteFile("settings.TEST-MACHINE.yaml", "general:\n  debugMode: true\n");

			SettingsCascade cascade = MakeCascade();

			Assert.That((bool)cascade.Merged["general"]["debugMode"], Is.True);
			Assert.That((string)cascade.Merged["general"]["label"], Is.EqualTo("base"));
		}

		[Test]
		public void Load_BothFormatsExistForOneTier_YamlWinsAndWarns()
		{
			WriteFile("settings.yaml", "general:\n  source: yaml\n");
			WriteFile("settings.json", @"{ ""general"": { ""source"": ""json"" } }");

			SettingsCascade cascade = MakeCascade();

			Assert.That((string)cascade.Merged["general"]["source"], Is.EqualTo("yaml"));
			Assert.That(cascade.Warnings.Count, Is.EqualTo(1));
			Assert.That(cascade.Warnings[0], Does.Contain("settings.yaml").And.Contain("settings.json").And.Contain("Base"));
		}

		[Test]
		public void PathFor_ProbesYamlThenJson_PerTier()
		{
			WriteFile("settings.yaml", "a: 1\n");
			WriteFile("settings.TEST-MACHINE.json", @"{ ""b"": 2 }");

			SettingsCascade cascade = MakeCascade();

			Assert.That(cascade.PathFor(SettingsTier.Base), Is.EqualTo(Path.Combine(_tempDir, "settings.yaml")));
			Assert.That(cascade.PathFor(SettingsTier.Machine), Is.EqualTo(Path.Combine(_tempDir, "settings.TEST-MACHINE.json")));
		}

		[Test]
		public void PathFor_NoFiles_DefaultsToYaml()
		{
			SettingsCascade cascade = MakeCascade();

			Assert.That(cascade.PathFor(SettingsTier.Base), Is.EqualTo(Path.Combine(_tempDir, "settings.yaml")));
			Assert.That(cascade.PathFor(SettingsTier.Machine), Is.EqualTo(Path.Combine(_tempDir, "settings.TEST-MACHINE.yaml")));
			Assert.That(cascade.PathFor(SettingsTier.Local), Is.EqualTo(Path.Combine(_tempDir, "settings.local.yaml")));
		}

		[Test]
		public void PathFor_MissingTier_MatchesJsonBaseFileExtension()
		{
			WriteFile("settings.json", @"{ ""a"": 1 }");

			SettingsCascade cascade = MakeCascade();

			Assert.That(cascade.PathFor(SettingsTier.Machine), Is.EqualTo(Path.Combine(_tempDir, "settings.TEST-MACHINE.json")));
			Assert.That(cascade.PathFor(SettingsTier.Local), Is.EqualTo(Path.Combine(_tempDir, "settings.local.json")));
		}

		[Test]
		public void PathFor_YmlExtension_NotRecognized()
		{
			WriteFile("settings.yml", "a: 1\n");

			SettingsCascade cascade = MakeCascade();

			// .yml is a documented limitation: the probe only knows .yaml and .json.
			Assert.That(cascade.PathFor(SettingsTier.Base), Is.EqualTo(Path.Combine(_tempDir, "settings.yaml")));
			Assert.That(cascade.LoadedPaths, Is.Empty);
		}

		[Test]
		public void Load_MalformedYamlTier_WarnsAndSkipsThatTier()
		{
			WriteFile("settings.yaml", "general:\n  debugMode: true\n");
			WriteFile("settings.local.yaml", "general: [unclosed\n");

			SettingsCascade cascade = MakeCascade();

			Assert.That((bool)cascade.Merged["general"]["debugMode"], Is.True);
			Assert.That(cascade.Warnings.Count, Is.EqualTo(1));
			Assert.That(cascade.Warnings[0], Does.Contain("Malformed Local"));
		}

		[Test]
		public void Load_CliSetOverride_WinsOverYamlTiers()
		{
			WriteFile("settings.yaml", "general:\n  debugMode: false\n");

			SettingsCascade cascade = MakeCascade("--set general.debugMode=true");

			Assert.That((bool)cascade.Merged["general"]["debugMode"], Is.True);
			Assert.That(cascade.TierFor("general.debugMode"), Is.EqualTo(SettingsTier.Cli));
		}

		[Test]
		public void TierFor_AcrossMixedFormats_ReportsWinningTier()
		{
			WriteFile("settings.json", @"{ ""general"": { ""debugMode"": false, ""label"": ""base"" } }");
			WriteFile("settings.local.yaml", "general:\n  debugMode: true\n");

			SettingsCascade cascade = MakeCascade();

			Assert.That(cascade.TierFor("general.debugMode"), Is.EqualTo(SettingsTier.Local));
			Assert.That(cascade.TierFor("general.label"), Is.EqualTo(SettingsTier.Base));
		}
	}
}
