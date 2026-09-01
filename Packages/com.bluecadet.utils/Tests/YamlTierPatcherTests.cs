using System;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Bluecadet.Utils.Editor;

namespace Bluecadet.Utils.Tests
{
	/// <summary>
	/// Full-output assertions for the comment-preserving YAML patcher. These double as probes of
	/// YamlDotNet's mark semantics (collapsed collection marks, empty-scalar spans, literal-scalar
	/// trailing newlines): if a library update shifts a mark, a splice lands somewhere visible here.
	/// </summary>
	[TestFixture]
	public class YamlTierPatcherTests
	{
		private static string Set(string yaml, string path, JToken value) =>
			YamlTierPatcher.SetPath(yaml, new SettingsPath(path), value);

		private static bool Remove(string yaml, string path, out string result) =>
			YamlTierPatcher.TryRemovePath(yaml, new SettingsPath(path), out result);

		// --- Replacing scalars --------------------------------------------------------------------

		[Test]
		public void SetPath_ReplaceScalar_PreservesSurroundingComments()
		{
			const string yaml =
				"# header comment\n" +
				"general:\n" +
				"  # above debugMode\n" +
				"  debugMode: false # trailing\n" +
				"  other: keep\n";

			string result = Set(yaml, "general.debugMode", true);

			Assert.That(result, Is.EqualTo(
				"# header comment\n" +
				"general:\n" +
				"  # above debugMode\n" +
				"  debugMode: true # trailing\n" +
				"  other: keep\n"));
		}

		[Test]
		public void SetPath_BareKey_InsertsValueAfterColon()
		{
			string result = Set("a:\nb: 2\n", "a", 5);

			Assert.That(result, Is.EqualTo("a: 5\nb: 2\n"));
		}

		[Test]
		public void SetPath_BareKeyWithTrailingComment_KeepsComment()
		{
			string result = Set("a: # note\nb: 2\n", "a", 5);

			Assert.That(result, Is.EqualTo("a: 5 # note\nb: 2\n"));
		}

		[Test]
		public void SetPath_ReplaceQuotedScalar_SplicesExactSpan()
		{
			string result = Set("a: \"old: value\" # keep\n", "a", "plainNew");

			Assert.That(result, Is.EqualTo("a: plainNew # keep\n"));
		}

		[Test]
		public void SetPath_ReplaceLiteralBlockScalar_WritesEscapedQuotedString()
		{
			const string yaml =
				"a: |\n" +
				"  line1\n" +
				"  line2\n" +
				"b: 2\n";

			string result = Set(yaml, "a", "one\ntwo");

			Assert.That(result, Is.EqualTo("a: \"one\\ntwo\"\nb: 2\n"));
		}

		// --- Scalar <-> block replacements --------------------------------------------------------

		[Test]
		public void SetPath_ScalarBecomesBlockMapping()
		{
			string result = Set("a: 5\nb: 2\n", "a", new JObject { ["x"] = 1 });

			Assert.That(result, Is.EqualTo("a:\n  x: 1\nb: 2\n"));
		}

		[Test]
		public void SetPath_BlockMappingBecomesScalar()
		{
			const string yaml =
				"a:\n" +
				"  x: 1\n" +
				"  y: 2\n" +
				"b: 3\n";

			string result = Set(yaml, "a", 7);

			Assert.That(result, Is.EqualTo("a: 7\nb: 3\n"));
		}

		[Test]
		public void SetPath_ReplaceNestedLeaf_KeepsCommentBetweenSubtreeAndNextKey()
		{
			const string yaml =
				"a:\n" +
				"  x: 1\n" +
				"# about b\n" +
				"b: 3\n";

			string result = Set(yaml, "a.x", 2);

			Assert.That(result, Is.EqualTo(
				"a:\n" +
				"  x: 2\n" +
				"# about b\n" +
				"b: 3\n"));
		}

		[Test]
		public void SetPath_ThroughEmptyFlowMapping_ReplacesItWithBlockChain()
		{
			string result = Set("a: {}\nb: 2\n", "a.x", 1);

			Assert.That(result, Is.EqualTo("a:\n  x: 1\nb: 2\n"));
		}

		// --- Inserting ----------------------------------------------------------------------------

		[Test]
		public void SetPath_NewKeyInExistingMapping_InsertsAfterLastEntry()
		{
			const string yaml =
				"general:\n" +
				"  a: 1\n" +
				"other: 2\n";

			string result = Set(yaml, "general.b", 2);

			Assert.That(result, Is.EqualTo(
				"general:\n" +
				"  a: 1\n" +
				"  b: 2\n" +
				"other: 2\n"));
		}

		[Test]
		public void SetPath_MissingChain_EmitsNestedBlockIntoDeepestExistingAncestor()
		{
			string result = Set("top: 1\n", "a.b.c", true);

			Assert.That(result, Is.EqualTo(
				"top: 1\n" +
				"a:\n" +
				"  b:\n" +
				"    c: true\n"));
		}

		[Test]
		public void SetPath_EmptyFile_WritesChainFromScratch()
		{
			Assert.That(Set(string.Empty, "a.b", 1), Is.EqualTo("a:\n  b: 1\n"));
		}

		[Test]
		public void SetPath_CommentsOnlyFile_AppendsBelowComments()
		{
			Assert.That(Set("# note\n", "a", 1), Is.EqualTo("# note\na: 1\n"));
		}

		[Test]
		public void SetPath_NewEntryLandsBeforeTrailingFileComment()
		{
			const string yaml =
				"a: 1\n" +
				"# trailing file comment\n";

			string result = Set(yaml, "b", 2);

			Assert.That(result, Is.EqualTo(
				"a: 1\n" +
				"b: 2\n" +
				"# trailing file comment\n"));
		}

		// --- Emission: strings and sequences ------------------------------------------------------

		[Test]
		public void SetPath_StringsThatWouldRetype_AreQuoted()
		{
			Assert.That(Set("", "a", "true"), Is.EqualTo("a: \"true\"\n"));
			Assert.That(Set("", "a", "5"), Is.EqualTo("a: \"5\"\n"));
			Assert.That(Set("", "a", "~"), Is.EqualTo("a: \"~\"\n"));
		}

		[Test]
		public void SetPath_StringsWithUnsafeConstructs_AreQuoted()
		{
			Assert.That(Set("", "a", "a: b"), Is.EqualTo("a: \"a: b\"\n"));
			Assert.That(Set("", "a", "# x"), Is.EqualTo("a: \"# x\"\n"));
			Assert.That(Set("", "a", "line1\nline2"), Is.EqualTo("a: \"line1\\nline2\"\n"));
		}

		[Test]
		public void SetPath_SafeStrings_StayPlain()
		{
			Assert.That(Set("", "a", "hello world"), Is.EqualTo("a: hello world\n"));
			Assert.That(Set("", "a", "yes"), Is.EqualTo("a: yes\n"));
		}

		[Test]
		public void SetPath_ExplicitNull_WritesNullScalar()
		{
			Assert.That(Set("", "a", JValue.CreateNull()), Is.EqualTo("a: null\n"));
		}

		[Test]
		public void SetPath_Array_EmitsBlockSequence()
		{
			string result = Set("a: 1\n", "tags", new JArray("a", "b"));

			Assert.That(result, Is.EqualTo(
				"a: 1\n" +
				"tags:\n" +
				"  - a\n" +
				"  - b\n"));
		}

		[Test]
		public void SetPath_ArrayOfObjects_EmitsNestedBlockItems()
		{
			string result = Set(string.Empty, "items", new JArray(new JObject { ["x"] = 1, ["y"] = 2 }));

			Assert.That(result, Is.EqualTo(
				"items:\n" +
				"  -\n" +
				"    x: 1\n" +
				"    y: 2\n"));
		}

		[Test]
		public void SetPath_EmptyCollections_EmitInline()
		{
			Assert.That(Set("", "a", new JObject()), Is.EqualTo("a: {}\n"));
			Assert.That(Set("", "a", new JArray()), Is.EqualTo("a: []\n"));
		}

		// --- Removing -----------------------------------------------------------------------------

		[Test]
		public void TryRemovePath_Leaf_RemovesItsLinesOnly()
		{
			Assert.That(Remove("a: 1\nb: 2\nc: 3\n", "b", out string result), Is.True);
			Assert.That(result, Is.EqualTo("a: 1\nc: 3\n"));
		}

		[Test]
		public void TryRemovePath_MissingPath_ReturnsFalseAndKeepsText()
		{
			Assert.That(Remove("a: 1\n", "missing", out string result), Is.False);
			Assert.That(result, Is.EqualTo("a: 1\n"));
		}

		[Test]
		public void TryRemovePath_PrunesAncestorsTheRemovalEmpties()
		{
			const string yaml =
				"a:\n" +
				"  b:\n" +
				"    c: 1\n" +
				"d: 2\n";

			Assert.That(Remove(yaml, "a.b.c", out string result), Is.True);
			Assert.That(result, Is.EqualTo("d: 2\n"));
		}

		[Test]
		public void TryRemovePath_KeepsSiblingsAndCommentsOutsideRemovedSpan()
		{
			const string yaml =
				"a:\n" +
				"  x: 1\n" +
				"# about b\n" +
				"b: 3\n";

			Assert.That(Remove(yaml, "a.x", out string result), Is.True);
			Assert.That(result, Is.EqualTo("# about b\nb: 3\n"));
		}

		[Test]
		public void TryRemovePath_BlockSubtree_RemovesAllItsLines()
		{
			const string yaml =
				"a:\n" +
				"  x: 1\n" +
				"  y: 2\n" +
				"b: 3\n";

			Assert.That(Remove(yaml, "a", out string result), Is.True);
			Assert.That(result, Is.EqualTo("b: 3\n"));
		}

		[Test]
		public void TryRemovePath_LastEntry_LeavesCommentsOnlyText()
		{
			Assert.That(Remove("# keep me\na: 1\n", "a", out string result), Is.True);
			Assert.That(result, Is.EqualTo("# keep me\n"));
			Assert.That(YamlTierPatcher.IsWhitespaceOnly(result), Is.False);
		}

		[Test]
		public void TryRemovePath_LastEntry_LeavesWhitespaceOnlyText()
		{
			Assert.That(Remove("a: 1\n", "a", out string result), Is.True);
			Assert.That(YamlTierPatcher.IsWhitespaceOnly(result), Is.True);
		}

		// --- Rejections ---------------------------------------------------------------------------

		[Test]
		public void SetPath_FlowMappingOnPath_Throws()
		{
			Assert.That(() => Set("a: {x: 1}\n", "a.x", 2), Throws.TypeOf<NotSupportedException>());
		}

		[Test]
		public void SetPath_FlowSequenceTarget_Throws()
		{
			Assert.That(() => Set("tags: [a, b]\n", "tags", new JArray("c")), Throws.TypeOf<NotSupportedException>());
		}

		[Test]
		public void SetPath_AnchoredMappingOnPath_Throws()
		{
			const string yaml =
				"a: &anc\n" +
				"  x: 1\n" +
				"b: *anc\n";

			Assert.That(() => Set(yaml, "a.x", 2), Throws.TypeOf<NotSupportedException>());
		}

		[Test]
		public void SetPath_AliasedValueOnPath_Throws()
		{
			const string yaml =
				"a: &anc\n" +
				"  x: 1\n" +
				"b: *anc\n";

			// b's value IS the anchored node, so editing through it is rejected the same way.
			Assert.That(() => Set(yaml, "b.x", 2), Throws.TypeOf<NotSupportedException>());
		}

		[Test]
		public void SetPath_AnchoredScalarTarget_Throws()
		{
			Assert.That(() => Set("a: &v 5\nb: *v\n", "a", 6), Throws.TypeOf<NotSupportedException>());
		}

		[Test]
		public void SetPath_FlowRoot_Throws()
		{
			Assert.That(() => Set("{a: 1}\n", "a", 2), Throws.TypeOf<NotSupportedException>());
		}

		// --- Trailing-newline invariants ----------------------------------------------------------

		[Test]
		public void SetPath_FileWithoutTrailingNewline_InsertGetsOwnLine()
		{
			Assert.That(Set("a: 1", "b", 2), Is.EqualTo("a: 1\nb: 2\n"));
		}

		[Test]
		public void SetPath_ReplaceInFileWithoutTrailingNewline_DoesNotAddOne()
		{
			Assert.That(Set("a: 1", "a", 2), Is.EqualTo("a: 2"));
		}
	}
}
