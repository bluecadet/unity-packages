using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Bluecadet.Utils.Editor;

namespace Bluecadet.Utils.Tests
{
	/// <summary>
	/// Covers <see cref="SettingsAnalysis.CollectChangedLeaves"/>: the pure JSON diff that decides which
	/// dotted paths actually changed value between draws, used instead of a per-leaf
	/// <c>EditorGUI.BeginChangeCheck</c> so that expanding/collapsing an array or nested-object foldout
	/// (which sets <c>GUI.changed</c> without changing any value) never marks a field dirty.
	/// </summary>
	[TestFixture]
	public class SettingsAnalysisDiffTests
	{
		private static List<string> Diff(string beforeJson, string afterJson)
		{
			var changed = new List<string>();
			SettingsAnalysis.CollectChangedLeaves(JObject.Parse(beforeJson), JObject.Parse(afterJson), changed);
			return changed;
		}

		[Test]
		public void IdenticalValues_NothingChanged()
		{
			const string json = @"{ ""general"": { ""debugMode"": false, ""tags"": [""a"", ""b""] } }";

			Assert.That(Diff(json, json), Is.Empty);
		}

		[Test]
		public void ArrayFoldoutToggle_ValueUnchanged_NothingChanged()
		{
			// Same array contents before and after: a foldout expand/collapse never touches JSON, so this
			// simulates the case that used to false-positive under a per-leaf BeginChangeCheck.
			const string before = @"{ ""general"": { ""tags"": [""a"", ""b""] } }";
			const string after = @"{ ""general"": { ""tags"": [""a"", ""b""] } }";

			Assert.That(Diff(before, after), Is.Empty);
		}

		[Test]
		public void NestedObjectFoldoutToggle_ValueUnchanged_NothingChanged()
		{
			const string before = @"{ ""general"": { ""debugMode"": true } }";
			const string after = @"{ ""general"": { ""debugMode"": true } }";

			Assert.That(Diff(before, after), Is.Empty);
		}

		[Test]
		public void ArrayElementChanged_MarksArrayLeafPath()
		{
			const string before = @"{ ""general"": { ""tags"": [""a"", ""b""] } }";
			const string after = @"{ ""general"": { ""tags"": [""a"", ""c""] } }";

			Assert.That(Diff(before, after), Is.EqualTo(new[] { "general.tags" }));
		}

		[Test]
		public void ArrayResized_MarksArrayLeafPath()
		{
			const string before = @"{ ""general"": { ""tags"": [""a""] } }";
			const string after = @"{ ""general"": { ""tags"": [""a"", ""b""] } }";

			Assert.That(Diff(before, after), Is.EqualTo(new[] { "general.tags" }));
		}

		[Test]
		public void NestedFieldChanged_MarksOnlyThatPath()
		{
			const string before = @"{ ""general"": { ""debugMode"": false, ""label"": ""x"" } }";
			const string after = @"{ ""general"": { ""debugMode"": true, ""label"": ""x"" } }";

			Assert.That(Diff(before, after), Is.EqualTo(new[] { "general.debugMode" }));
		}

		[Test]
		public void MultipleFieldsChanged_MarksEachPath()
		{
			const string before = @"{ ""a"": 1, ""b"": { ""c"": 2 } }";
			const string after = @"{ ""a"": 9, ""b"": { ""c"": 9 } }";

			Assert.That(Diff(before, after), Is.EquivalentTo(new[] { "a", "b.c" }));
		}

		[Test]
		public void LeafMissingFromBefore_CountsAsChanged()
		{
			const string before = @"{ ""general"": {} }";
			const string after = @"{ ""general"": { ""debugMode"": true } }";

			Assert.That(Diff(before, after), Is.EqualTo(new[] { "general.debugMode" }));
		}

		[Test]
		public void DeeplyNestedFieldChanged_MarksFullDottedPath()
		{
			const string before = @"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": false } } } }";
			const string after = @"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": true } } } }";

			Assert.That(Diff(before, after), Is.EqualTo(new[] { "app.display.window.fullscreen" }));
		}

		[Test]
		public void DeeplyNestedFieldChanged_UnchangedDeepSiblingsStayClean()
		{
			const string before = @"{
				""app"": {
					""label"": ""keep"",
					""display"": {
						""index"": 1,
						""window"": { ""fullscreen"": false, ""scale"": 1.0 }
					},
					""audio"": { ""mixer"": { ""volume"": 0.5 } }
				}
			}";
			const string after = @"{
				""app"": {
					""label"": ""keep"",
					""display"": {
						""index"": 1,
						""window"": { ""fullscreen"": true, ""scale"": 1.0 }
					},
					""audio"": { ""mixer"": { ""volume"": 0.5 } }
				}
			}";

			Assert.That(Diff(before, after), Is.EqualTo(new[] { "app.display.window.fullscreen" }));
		}

		[Test]
		public void DeeplyNestedArrayElementChanged_MarksArrayLeafPath()
		{
			const string before = @"{ ""app"": { ""display"": { ""window"": { ""tags"": [""a"", ""b""] } } } }";
			const string after = @"{ ""app"": { ""display"": { ""window"": { ""tags"": [""a"", ""c""] } } } }";

			Assert.That(Diff(before, after), Is.EqualTo(new[] { "app.display.window.tags" }));
		}

		[Test]
		public void DeeplyNestedObjectMissingFromBefore_MarksEachDeepLeaf()
		{
			const string before = @"{ ""app"": {} }";
			const string after = @"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": true, ""scale"": 2.0 } } } }";

			Assert.That(Diff(before, after), Is.EquivalentTo(new[] { "app.display.window.fullscreen", "app.display.window.scale" }));
		}

		[Test]
		public void MultipleDeepBranchesChanged_MarksEachPath()
		{
			const string before = @"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": false } }, ""audio"": { ""mixer"": { ""volume"": 0.5 } } } }";
			const string after = @"{ ""app"": { ""display"": { ""window"": { ""fullscreen"": true } }, ""audio"": { ""mixer"": { ""volume"": 0.9 } } } }";

			Assert.That(Diff(before, after), Is.EquivalentTo(new[] { "app.display.window.fullscreen", "app.audio.mixer.volume" }));
		}
	}
}
