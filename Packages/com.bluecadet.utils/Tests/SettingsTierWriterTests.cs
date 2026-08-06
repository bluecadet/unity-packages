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
	}
}
