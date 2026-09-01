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

		/// <summary>The invariant behind every write: the reader must be able to load what was written.</summary>
		private static JObject Reload(string yaml) => YamlTokenReader.ToJObject(yaml);

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

		// --- Key-line comments across a rewritten key line ----------------------------------------

		[Test]
		public void SetPath_BlockMappingBecomesScalar_KeepsKeyLineComment()
		{
			string result = Set("a: # note\n  x: 1\nb: 2\n", "a", 9);

			Assert.That(result, Is.EqualTo("a: 9 # note\nb: 2\n"));
		}

		[Test]
		public void SetPath_BlockMappingBecomesOtherBlockMapping_KeepsKeyLineComment()
		{
			string result = Set("a: # note\n  x: 1\nb: 2\n", "a", new JObject { ["y"] = 2 });

			Assert.That(result, Is.EqualTo("a: # note\n  y: 2\nb: 2\n"));
		}

		[Test]
		public void SetPath_ScalarBecomesBlockMapping_KeepsCommentOnKeyLine()
		{
			// The comment described 'a', so it stays on a's line rather than following the first child.
			string result = Set("a: 5 # note", "a", new JObject { ["y"] = 2 });

			Assert.That(result, Is.EqualTo("a: # note\n  y: 2"));
		}

		[Test]
		public void SetPath_BareKeyBecomesBlockMapping_KeepsCommentOnKeyLine()
		{
			string result = Set("a: # note\nb: 2\n", "a", new JObject { ["y"] = 2 });

			Assert.That(result, Is.EqualTo("a: # note\n  y: 2\nb: 2\n"));
		}

		[Test]
		public void SetPath_ThroughCommentedEmptyFlowMapping_KeepsCommentOnKeyLine()
		{
			string result = Set("a: {} # note\nb: 2\n", "a.x", 1);

			Assert.That(result, Is.EqualTo("a: # note\n  x: 1\nb: 2\n"));
		}

		[Test]
		public void SetPath_CrlfKeyLineComment_KeepsOneCarriageReturn()
		{
			// The '\r' terminating the key line must not be duplicated into the re-emitted comment.
			string result = Set("a: # note\r\n  x: 1\r\nb: 2\r\n", "a", 9);

			Assert.That(result, Is.EqualTo("a: 9 # note\r\nb: 2\r\n"));
		}

		[Test]
		public void SetPath_CrlfBlockMappingBecomesScalar_KeepsCrlfEndings()
		{
			// Cutting the replaced subtree must leave a whole "\r\n" behind, not a bare "\n": half a
			// terminator turns the rewritten line into a mixed-ending line the file never had.
			Assert.That(Set("a:\r\n  x: 1\r\n  # c\r\nb: 2\r\n", "a", 9), Is.EqualTo("a: 9\r\nb: 2\r\n"));
			Assert.That(Set("a:\r\n  x: 1\r\n  # c\r\n", "a", 9), Is.EqualTo("a: 9\r\n"));
			Assert.That((int)Reload(Set("a:\r\n  x: 1\r\n  # c\r\nb: 2\r\n", "a", 9))["a"], Is.EqualTo(9));
		}

		[Test]
		public void SetPath_CrlfScalarBecomesBlockMapping_EmitsCrlfLines()
		{
			// Emitted block lines end the way the key line they follow does.
			Assert.That(Set("a: 1\r\nb: 2\r\n", "a", new JObject { ["k"] = 1 }),
				Is.EqualTo("a:\r\n  k: 1\r\nb: 2\r\n"));

			// Including when a key-line comment is re-emitted ahead of them.
			Assert.That(Set("a: 1 # keep\r\nb: 2\r\n", "a", new JObject { ["k"] = 1 }),
				Is.EqualTo("a: # keep\r\n  k: 1\r\nb: 2\r\n"));

			Assert.That(Set("a: 1\r\nb: 2\r\n", "a", new JObject { ["k"] = new JObject { ["j"] = 1 } }),
				Is.EqualTo("a:\r\n  k:\r\n    j: 1\r\nb: 2\r\n"));

			Assert.That((int)Reload(Set("a: 1 # keep\r\nb: 2\r\n", "a", new JObject { ["k"] = 1 }))["a"]["k"],
				Is.EqualTo(1));
		}

		[Test]
		public void SetPath_MixedLineEndings_KeepEachRewrittenLineOwnEnding()
		{
			// Endings are read per line, not per file, so neither ending spreads into the other's lines.
			Assert.That(Set("a: 1\nb: 2 # c\r\nc: 3\n", "b", new JObject { ["k"] = 1 }),
				Is.EqualTo("a: 1\nb: # c\r\n  k: 1\r\nc: 3\n"));

			Assert.That(Set("a: 1\r\nb: 2\nc: 3\r\n", "b", new JObject { ["k"] = 1 }),
				Is.EqualTo("a: 1\r\nb:\n  k: 1\nc: 3\r\n"));

			// An unterminated last line has no ending of its own, so the file's first one stands in.
			Assert.That(Set("a: 1 # keep\r\nb: 2", "b", new JObject { ["k"] = 1 }),
				Is.EqualTo("a: 1 # keep\r\nb:\r\n  k: 1"));
		}

		// --- Comments inside a replaced subtree ----------------------------------------------------

		[Test]
		public void SetPath_ReplaceSubtree_DropsCommentsIndentedInsideIt()
		{
			// The comment described the block that just went away, so leaving it would orphan it
			// under an unrelated scalar.
			Assert.That(Set("a:\n  x: 1\n  # tail\nb: 2\n", "a", 9), Is.EqualTo("a: 9\nb: 2\n"));
			Assert.That(Set("a:\n  x:\n    y: 1\n    # deep\nb: 2\n", "a", 9), Is.EqualTo("a: 9\nb: 2\n"));
			Assert.That(Set("a:\n  x: 1\n  # tail\n", "a", 9), Is.EqualTo("a: 9\n"));
		}

		[Test]
		public void SetPath_ReplaceSubtree_KeepsCommentAtOrOutsideTheKeyIndent()
		{
			string result = Set("a:\n  x: 1\n# about b\nb: 2\n", "a", 9);

			Assert.That(result, Is.EqualTo("a: 9\n# about b\nb: 2\n"));
		}

		[Test]
		public void SetPath_ReplaceSubtree_StopsSweepingCommentsAtABlankLine()
		{
			string result = Set("a:\n  x: 1\n  # tail\n\nb: 2\n", "a", 9);

			Assert.That(result, Is.EqualTo("a: 9\n\nb: 2\n"));
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
		public void SetPath_NewSiblingAfterCommentedSubtree_LandsBelowThatComment()
		{
			// "# deep" is indented inside x's subtree, so z goes after it. Inserting above it would
			// reparent the comment into z's block, where a later edit of z would sweep it away.
			const string yaml =
				"a:\n" +
				"  x:\n" +
				"    y: 1\n" +
				"    # deep\n" +
				"  # mid\n" +
				"b: 2\n";

			Assert.That(Set(yaml, "a.z", 9), Is.EqualTo(
				"a:\n" +
				"  x:\n" +
				"    y: 1\n" +
				"    # deep\n" +
				"  z: 9\n" +
				"  # mid\n" +
				"b: 2\n"));

			// "# mid" sits at z's own indent, so it reads as a comment about what follows and stays
			// below the new entry — the same rule that keeps a trailing file comment last.
			Assert.That(Set("a:\n  x: 1\n  # sib\nb: 2\n", "a.z", 9),
				Is.EqualTo("a:\n  x: 1\n  z: 9\n  # sib\nb: 2\n"));

			Assert.That((int)Reload(Set(yaml, "a.z", 9))["a"]["z"], Is.EqualTo(9));
			Assert.That((int)Reload(Set(yaml, "a.z", 9))["a"]["x"]["y"], Is.EqualTo(1));
		}

		[Test]
		public void SetPath_ThenTryRemovePath_IsByteForByteRoundTrip()
		{
			// Adding a key and taking it back out again must leave the file exactly as it was. This is
			// what pins the insert position against the subtree-comment sweep: if the two disagree
			// about which lines belong to the previous sibling, the sweep eats a user's comment.
			const string commented =
				"a:\n" +
				"  x:\n" +
				"    y: 1\n" +
				"    # deep\n" +
				"  # mid\n" +
				"b: 2\n";

			foreach (JToken value in new JToken[]
			{
				9,
				"str",
				new JObject { ["q"] = 1 },
				new JObject { ["q"] = new JObject { ["r"] = 2 } },
				new JArray(1, 2)
			})
			{
				string added = Set(commented, "a.z", value);
				Assert.That(Remove(added, "a.z", out string back), Is.True, value.ToString());
				Assert.That(back, Is.EqualTo(commented), value.ToString());
			}

			foreach (string yaml in new[]
			{
				commented,
				commented.Replace("\n", "\r\n"),
				"a:\n  x:\n    y: 1\n    # d1\n    # d2\n  # mid\nb: 2\n",
				"a:\n  x:\n    y: 1\n    # deep\n",
				"a:\n  x: 1\n  # sib\nb: 2\n",
				"a:\n  x: 1\nb: 2\n"
			})
			{
				string added = Set(yaml, "a.z", new JObject { ["q"] = 1 });
				Assert.That(Remove(added, "a.z", out string back), Is.True, yaml);
				Assert.That(back, Is.EqualTo(yaml), yaml);
			}

			// A whole chain added at the root comes back out the same way, trailing comment and all.
			string appended = Set("a: 1\n# tail\n", "c.d", 1);
			Assert.That(Remove(appended, "c.d", out string root), Is.True);
			Assert.That(root, Is.EqualTo("a: 1\n# tail\n"));
		}

		// --- Empty-tier roots ---------------------------------------------------------------------

		[Test]
		public void SetPath_NullLiteralRoot_ReplacesItWithTheEmittedMapping()
		{
			// The null scalar cannot stay above a mapping; appending after it would emit a document
			// the reader refuses to load.
			Assert.That(Set("~\n", "a", 1), Is.EqualTo("a: 1\n"));
			Assert.That(Set("null\n", "a", 1), Is.EqualTo("a: 1\n"));
			Assert.That(Set("NULL\n", "a", 1), Is.EqualTo("a: 1\n"));
			Assert.That(Set("~", "a", 1), Is.EqualTo("a: 1\n"));
			Assert.That(Set("~\n", "a.b", 1), Is.EqualTo("a:\n  b: 1\n"));
		}

		[Test]
		public void SetPath_NullLiteralRoot_OutputLoadsAsTheSetValue()
		{
			foreach (string tier in new[] { "~\n", "null\n", "Null\n", "NULL\n", "# c\n~\n", "\n~\n\n" })
				Assert.That((int)Reload(Set(tier, "a", 1))["a"], Is.EqualTo(1), tier);

			Assert.That((int)Reload(Set("~\n", "a.b", 1))["a"]["b"], Is.EqualTo(1));
		}

		[Test]
		public void SetPath_NullLiteralRoot_KeepsCommentsAroundIt()
		{
			Assert.That(Set("# c\n~\n", "a", 1), Is.EqualTo("# c\na: 1\n"));

			// A comment sharing the null literal's line survives on a line of its own.
			Assert.That(Set("~ # note\n", "a", 1), Is.EqualTo("# note\na: 1\n"));

			// Blank lines elsewhere are outside the replaced span.
			Assert.That(Set("\n~\n\n", "a", 1), Is.EqualTo("\na: 1\n\n"));
		}

		[Test]
		public void SetPath_EmptyFlowMappingRoot_ReplacesItWithBlockMapping()
		{
			// "{}" is an empty tier, the same as a nested "a: {}" the patcher already writes through.
			Assert.That(Set("{}\n", "a", 1), Is.EqualTo("a: 1\n"));
			Assert.That(Set("{}\n", "a.b", 1), Is.EqualTo("a:\n  b: 1\n"));
			Assert.That((int)Reload(Set("{}\n", "a", 1))["a"], Is.EqualTo(1));
		}

		[Test]
		public void SetPath_DocumentMarkerBeforeEmptyRoot_LeavesNoStrandedWhitespace()
		{
			// The placeholder goes, and so does the space that separated it from the "---".
			Assert.That(Set("--- {}\n", "a", 1), Is.EqualTo("---\na: 1\n"));
			Assert.That(Set("--- ~\n", "a", 1), Is.EqualTo("---\na: 1\n"));
			Assert.That(Set("---\n", "a", 1), Is.EqualTo("---\na: 1\n"));
			Assert.That((int)Reload(Set("--- {}\n", "a", 1))["a"], Is.EqualTo(1));
		}

		[Test]
		public void SetPath_EmptyDocumentSpellings_KeepAppending()
		{
			// Nothing to replace in these: there is no root node at all, so the leading text stays put.
			Assert.That(Set(string.Empty, "a", 1), Is.EqualTo("a: 1\n"));
			Assert.That(Set("\n", "a", 1), Is.EqualTo("\na: 1\n"));
			Assert.That(Set("# c\n", "a", 1), Is.EqualTo("# c\na: 1\n"));
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
		public void TryRemovePath_EmptyTierRoot_ReturnsFalseAndKeepsText()
		{
			// Nothing is present in an empty tier, so a removal is a no-op rather than a rewrite.
			Assert.That(Remove("~\n", "a", out string fromNull), Is.False);
			Assert.That(fromNull, Is.EqualTo("~\n"));

			Assert.That(Remove("{}\n", "a", out string fromFlow), Is.False);
			Assert.That(fromFlow, Is.EqualTo("{}\n"));
		}

		[Test]
		public void TryRemovePath_Subtree_DropsCommentsIndentedInsideIt()
		{
			Assert.That(Remove("a:\n  x: 1\n  # tail\nb: 2\n", "a", out string result), Is.True);
			Assert.That(result, Is.EqualTo("b: 2\n"));
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

		[Test]
		public void TryRemovePath_TrailingCommentSweepWouldEmptyFile_KeepsTheComment()
		{
			// Sweeping the comment would leave nothing, and the caller deletes a whitespace-only tier:
			// an orphaned comment is the lesser evil, so the sweep gives way here.
			Assert.That(Remove("a:\n  x: 1\n  # trailing note\n", "a.x", out string result), Is.True);
			Assert.That(result, Is.EqualTo("  # trailing note\n"));
			Assert.That(YamlTierPatcher.IsWhitespaceOnly(result), Is.False);
			Assert.That(Reload(result).Count, Is.EqualTo(0));

			Assert.That(Remove("a:\r\n  x: 1\r\n  # trailing note\r\n", "a.x", out string crlf), Is.True);
			Assert.That(crlf, Is.EqualTo("  # trailing note\r\n"));

			// With anything else left, the sweep still takes the comment.
			Assert.That(Remove("a:\n  x: 1\n  # trailing note\nb: 2\n", "a.x", out string sibling), Is.True);
			Assert.That(sibling, Is.EqualTo("b: 2\n"));
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
		public void SetPath_AnchoredEmptyRoot_Throws()
		{
			// The empty-tier placeholder is still a node an alias can point at, so replacing it goes
			// through the same anchor guard as every other edited span.
			Assert.That(() => Set("&anc {}\n", "a", 1), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => Set("&anc ~\n", "a", 1), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => Set("&anc {}\n", "a.b", 1), Throws.TypeOf<NotSupportedException>());

			// A removal rewrites nothing in an empty tier, so it stays a no-op rather than throwing.
			Assert.That(Remove("&anc ~\n", "a", out string result), Is.False);
			Assert.That(result, Is.EqualTo("&anc ~\n"));
		}

		[Test]
		public void SetPath_FlowRootWithEntries_Throws()
		{
			Assert.That(() => Set("{a: 1}\n", "a", 2), Throws.TypeOf<NotSupportedException>());
		}

		[Test]
		public void SetPath_SequenceRoot_Throws()
		{
			// A sequence root is not a settings tier at all, empty or not: the reader rejects it too.
			Assert.That(() => Set("[]\n", "a", 1), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => Set("- a\n", "a", 1), Throws.TypeOf<NotSupportedException>());
		}

		// --- Hostile inputs and invariants ---------------------------------------------------------

		[Test]
		public void SetPath_NonAsciiTextBeforeTarget_SplicesAtCorrectOffsets()
		{
			// Astral-plane characters are two UTF-16 units: if marks counted code points instead of
			// chars, every splice after this comment would land one unit short.
			const string yaml =
				"# target \U0001F3AF practice — ünïcode\n" +
				"a: 1\n" +
				"b: 2\n";

			string result = Set(yaml, "b", 5);

			Assert.That(result, Is.EqualTo(
				"# target \U0001F3AF practice — ünïcode\n" +
				"a: 1\n" +
				"b: 5\n"));
		}

		[Test]
		public void SetPath_CrlfFile_ReplacesAndInsertsCleanly()
		{
			Assert.That(Set("a: 1\r\nb: 2\r\n", "a", 5), Is.EqualTo("a: 5\r\nb: 2\r\n"));

			// An inserted line ends the way the line it follows does, so the file stays all-CRLF.
			Assert.That(Set("a: 1\r\nb: 2\r\n", "c", 3), Is.EqualTo("a: 1\r\nb: 2\r\nc: 3\r\n"));
		}

		[Test]
		public void SetPath_CrlfMapping_InsertedEntriesUseCrlf()
		{
			// A new sibling line follows an existing CRLF line, so it ends the same way.
			Assert.That(Set("a:\r\n  x: 1\r\nb: 2\r\n", "a.y", 2),
				Is.EqualTo("a:\r\n  x: 1\r\n  y: 2\r\nb: 2\r\n"));

			Assert.That(Set("top: 1\r\n", "a.b.c", true),
				Is.EqualTo("top: 1\r\na:\r\n  b:\r\n    c: true\r\n"));

			// The last line is unterminated, so there is nothing local to copy: the file's first
			// ending stands in, both for the newline that closes that line and for the new one.
			Assert.That(Set("a: 1\r\nb: 2", "c", 3), Is.EqualTo("a: 1\r\nb: 2\r\nc: 3\r\n"));

			Assert.That((int)Reload(Set("a: 1\r\nb: 2", "c", 3))["c"], Is.EqualTo(3));
		}

		[Test]
		public void SetPath_CrlfFileWithNoMapping_AppendedEntriesUseCrlf()
		{
			Assert.That(Set("# note\r\n", "a", 1), Is.EqualTo("# note\r\na: 1\r\n"));
			Assert.That(Set("# note\r\n", "a.b", 1), Is.EqualTo("# note\r\na:\r\n  b: 1\r\n"));
			Assert.That(Set("\r\n", "a", 1), Is.EqualTo("\r\na: 1\r\n"));
			Assert.That((int)Reload(Set("# note\r\n", "a", 1))["a"], Is.EqualTo(1));
		}

		[Test]
		public void SetPath_FileWithNoEndingToInfer_WritesLineFeed()
		{
			// Nothing in these files says CRLF, so the emitted lines fall back to \n.
			Assert.That(Set(string.Empty, "a.b", 1), Is.EqualTo("a:\n  b: 1\n"));
			Assert.That(Set("# note", "a", 1), Is.EqualTo("# note\na: 1\n"));
			Assert.That(Set("a: 1", "b", 2), Is.EqualTo("a: 1\nb: 2\n"));
		}

		[Test]
		public void SetPath_SequentialOps_BuildOnPatchedText()
		{
			// The tier editor re-parses after every operation; two inserts into the same new mapping
			// must compose.
			string once = Set(string.Empty, "a.b", 1);
			string twice = Set(once, "a.c", 2);

			Assert.That(twice, Is.EqualTo("a:\n  b: 1\n  c: 2\n"));
		}

		[Test]
		public void SetPath_SameScalarValue_IsByteStable()
		{
			const string yaml = "a: hello # note\nb: 2\n";

			Assert.That(Set(yaml, "a", "hello"), Is.EqualTo(yaml));
		}

		[Test]
		public void SetPath_KeySpelledLikeBool_IsQuotedOnEmit()
		{
			string result = Set(string.Empty, "true", 1);

			Assert.That(result, Is.EqualTo("\"true\": 1\n"));

			// And the quoted key is still found by a follow-up edit.
			Assert.That(Set(result, "true", 2), Is.EqualTo("\"true\": 2\n"));
		}

		[Test]
		public void SetPath_EmptyStringValue_IsQuoted()
		{
			Assert.That(Set(string.Empty, "a", string.Empty), Is.EqualTo("a: \"\"\n"));
		}

		[Test]
		public void SetPath_FourSpaceIndentedMapping_MatchesExistingIndent()
		{
			string result = Set("a:\n    x: 1\n", "a.y", 2);

			Assert.That(result, Is.EqualTo("a:\n    x: 1\n    y: 2\n"));
		}

		[Test]
		public void SetPath_CommentBetweenKeyAndBlockChild_IsPreserved()
		{
			string result = Set("a: # note\n  x: 1\nb: 2\n", "a.x", 2);

			Assert.That(result, Is.EqualTo("a: # note\n  x: 2\nb: 2\n"));
		}

		[Test]
		public void SetPath_ReplaceBlockSequenceOfMappings_WithScalar()
		{
			const string yaml =
				"items:\n" +
				"  - x: 1\n" +
				"    y: 2\n" +
				"  - x: 3\n" +
				"next: 4\n";

			string result = Set(yaml, "items", 9);

			Assert.That(result, Is.EqualTo("items: 9\nnext: 4\n"));
		}

		[Test]
		public void TryRemovePath_SubtreeContainingFlowSequence_RemovesCleanly()
		{
			// The flow sequence is inside the removed subtree, not on the edited path, so it is
			// allowed; its closing bracket has to be located by scanning the text.
			const string yaml =
				"a:\n" +
				"  tags: [x, y]\n" +
				"  b: 1\n" +
				"c: 2\n";

			Assert.That(Remove(yaml, "a", out string result), Is.True);
			Assert.That(result, Is.EqualTo("c: 2\n"));
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
