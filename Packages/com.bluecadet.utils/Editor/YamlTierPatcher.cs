using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// Surgical, comment-preserving edits into YAML tier text. No YAML library round-trips comments
	/// through load-modify-save, so every operation here re-parses the current text, locates the
	/// affected span through YamlDotNet's parser marks, and splices the raw string: everything outside
	/// that span (comments included) survives byte for byte. A comment on the edited key's own line
	/// is carried across to the rewritten key line; comment-only lines indented inside a replaced or
	/// removed subtree go with that subtree. Only block-style YAML is editable: an empty flow
	/// collection is an empty tier and gets replaced, but a flow collection with entries, an anchor,
	/// or an alias anywhere on (or inside) the edited span throws <see cref="NotSupportedException"/>
	/// rather than risk corrupting the document.
	/// </summary>
	/// <remarks>
	/// Mark semantics this relies on (pinned by <c>YamlTierPatcherTests</c>): scalar marks span the
	/// scalar exactly (quotes included; literal/folded scalars end past their final newline), an empty
	/// scalar has <c>Start == End</c> just after its key's colon, and collection nodes carry collapsed
	/// marks (<c>Start == End</c> at their first content), so collection extents are always derived
	/// from key marks and the deepest descendant scalar ends instead.
	/// </remarks>
	internal static class YamlTierPatcher
	{
		/// <summary>Emitted children indent this far past their parent key.</summary>
		private const int _indentStep = 2;

		/// <summary>
		/// Returns <paramref name="yamlText"/> with <paramref name="path"/> set to <paramref name="value"/>,
		/// mirroring <see cref="SettingsPath.Set"/>: intermediate mappings are created as needed and a
		/// non-mapping intermediate is replaced by the rest of the chain.
		/// </summary>
		internal static string SetPath(string yamlText, SettingsPath path, JToken value)
		{
			string text = yamlText ?? string.Empty;
			string[] segments = path.Segments;
			YamlMappingNode root = ParseRoot(text, path, out YamlNode emptyRoot);

			if (root == null)
			{
				List<string> lines = EmitEntryLines(segments[0], WrapInChain(segments, 1, value), 0);

				return emptyRoot == null ? AppendLines(text, lines) : ReplaceEmptyRoot(text, lines, emptyRoot, path);
			}

			YamlMappingNode current = root;
			for (int i = 0; i < segments.Length - 1; i++)
			{
				KeyValuePair<YamlNode, YamlNode>? pair = Lookup(current, segments[i]);

				if (pair == null)
					return InsertEntry(text, current, EmitEntryLines(segments[i], WrapInChain(segments, i + 1, value), IndentOf(current)));

				if (pair.Value.Value is YamlMappingNode childMapping && childMapping.Children.Count > 0)
				{
					RequireEditableMapping(childMapping, path);
					current = childMapping;
					continue;
				}

				// A scalar, sequence or empty mapping along the way: the rest of the chain replaces it,
				// the same way SettingsPath.Set overwrites a non-object intermediate.
				return ReplaceValue(text, pair.Value.Key, pair.Value.Value, WrapInChain(segments, i + 1, value), path);
			}

			KeyValuePair<YamlNode, YamlNode>? leaf = Lookup(current, segments[segments.Length - 1]);

			return leaf == null
				? InsertEntry(text, current, EmitEntryLines(segments[segments.Length - 1], value, IndentOf(current)))
				: ReplaceValue(text, leaf.Value.Key, leaf.Value.Value, value, path);
		}

		/// <summary>
		/// Removes <paramref name="path"/>'s entry from <paramref name="yamlText"/>, pruning the same
		/// ancestors <see cref="SettingsPath.Remove"/> would: the highest ancestor entry the removal
		/// leaves empty goes with it, but never the root. Comment-only lines indented inside the
		/// removed subtree go with it, unless dropping them is what would empty the file — a removal
		/// never turns a file that still has content into one the caller would delete. Returns false
		/// (with the text unchanged) when the path isn't present, an empty tier (<c>~</c>, <c>{}</c>,
		/// comments only) included.
		/// </summary>
		internal static bool TryRemovePath(string yamlText, SettingsPath path, out string result)
		{
			string text = yamlText ?? string.Empty;
			result = text;

			string[] segments = path.Segments;
			YamlMappingNode root = ParseRoot(text, path, out YamlNode _);

			if (root == null)
				return false;

			var mappings = new List<YamlMappingNode> { root };
			for (int i = 0; i < segments.Length - 1; i++)
			{
				KeyValuePair<YamlNode, YamlNode>? pair = Lookup(mappings[i], segments[i]);

				if (pair == null || !(pair.Value.Value is YamlMappingNode childMapping) || childMapping.Children.Count == 0)
					return false;

				RequireEditableMapping(childMapping, path);
				mappings.Add(childMapping);
			}

			if (Lookup(mappings[mappings.Count - 1], segments[segments.Length - 1]) == null)
				return false;

			// Removing the only entry of a mapping empties it: remove the highest such ancestor's
			// entry instead, so no trail of newly empty objects is left behind. Never the root.
			int depth = segments.Length - 1;
			while (depth > 0 && mappings[depth].Children.Count == 1)
				depth--;

			KeyValuePair<YamlNode, YamlNode> entry = Lookup(mappings[depth], segments[depth]).Value;
			DemandNoAnchors(entry.Value, path);

			int spanStart = LineStart(text, (int)entry.Key.Start.Index);
			int spanEnd = LineEndInclusive(text, AdjustedEnd(text, entry.Value, path));
			string entryOnly = text.Remove(spanStart, spanEnd - spanStart);

			// Comment-only lines indented inside the removed subtree go with it, except when sweeping
			// them is what would leave the file with nothing: the caller deletes a whitespace-only
			// tier, and losing a settings file over a trailing comment is worse than orphaning one.
			int sweptEnd = SweepSubtreeComments(text, spanEnd, (int)entry.Key.Start.Column - 1);
			string swept = text.Remove(spanStart, sweptEnd - spanStart);

			result = IsWhitespaceOnly(swept) && !IsWhitespaceOnly(entryOnly) ? entryOnly : swept;
			return true;
		}

		/// <summary>
		/// True when nothing but whitespace is left, i.e. the caller should delete the file. A
		/// comments-only file is NOT whitespace-only: it parses as an empty tier and is kept.
		/// </summary>
		internal static bool IsWhitespaceOnly(string text) => string.IsNullOrWhiteSpace(text);

		// --- Parsing and walking ------------------------------------------------------------------

		/// <summary>
		/// Parses the document's root mapping, or null when the document holds no mapping to edit.
		/// <paramref name="emptyRoot"/> is then the node an empty tier is spelled with — a null
		/// literal (<c>~</c>, <c>null</c>) or an empty flow mapping (<c>{}</c>) — which a set has to
		/// replace, since a document cannot hold both that node and a mapping; it is null when the
		/// document has no content at all (empty, whitespace or comments only) and a set can append.
		/// </summary>
		private static YamlMappingNode ParseRoot(string text, SettingsPath path, out YamlNode emptyRoot)
		{
			emptyRoot = null;

			var stream = new YamlStream();
			using (var reader = new StringReader(text))
				stream.Load(reader);

			if (stream.Documents.Count == 0)
				return null;

			YamlNode root = stream.Documents[0].RootNode;

			if (root is YamlScalarNode scalar && scalar.Style == ScalarStyle.Plain && YamlTokenReader.IsNullLiteral(scalar.Value ?? string.Empty))
			{
				emptyRoot = scalar;
				return null;
			}

			if (!(root is YamlMappingNode mapping))
				throw new NotSupportedException($"Cannot edit '{path}': the YAML document's root is not a mapping.");

			if (mapping.Style != MappingStyle.Block)
			{
				// "{}" is how an empty tier round-trips through a flow writer, so it is an empty tier
				// here too; only a flow mapping that actually holds entries is out of reach.
				if (mapping.Children.Count == 0)
				{
					emptyRoot = mapping;
					return null;
				}

				throw new NotSupportedException($"Cannot edit '{path}': the YAML document's root mapping is flow-style ({{...}}). Rewrite it in block style to edit it here.");
			}

			return mapping;
		}

		private static KeyValuePair<YamlNode, YamlNode>? Lookup(YamlMappingNode mapping, string key)
		{
			foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
			{
				if (pair.Key is YamlScalarNode scalarKey && string.Equals(scalarKey.Value, key, StringComparison.Ordinal))
					return pair;
			}

			return null;
		}

		/// <summary>Column of the mapping's first key, as the indent every sibling entry is written at.</summary>
		private static int IndentOf(YamlMappingNode mapping)
		{
			foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
				return (int)pair.Key.Start.Column - 1;

			return 0;
		}

		private static void RequireEditableMapping(YamlMappingNode mapping, SettingsPath path)
		{
			if (mapping.Style != MappingStyle.Block)
				throw new NotSupportedException($"Cannot edit '{path}': a mapping on the path is flow-style ({{...}}). Rewrite it in block style to edit it here.");

			if (!mapping.Anchor.IsEmpty)
				throw new NotSupportedException($"Cannot edit '{path}': a mapping on the path carries the anchor '&{mapping.Anchor}', and editing it could break aliases elsewhere in the file.");
		}

		/// <summary>Rejects anchors anywhere in a subtree about to be replaced or removed: an alias elsewhere may depend on it.</summary>
		private static void DemandNoAnchors(YamlNode node, SettingsPath path)
		{
			if (!node.Anchor.IsEmpty)
				throw new NotSupportedException($"Cannot edit '{path}': the affected YAML contains the anchor '&{node.Anchor}', and rewriting it could break aliases elsewhere in the file.");

			switch (node)
			{
				case YamlMappingNode mapping:
					foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
						DemandNoAnchors(pair.Value, path);
					break;

				case YamlSequenceNode sequence:
					foreach (YamlNode item in sequence.Children)
						DemandNoAnchors(item, path);
					break;
			}
		}

		// --- Span arithmetic ----------------------------------------------------------------------

		/// <summary>
		/// The exclusive end index of a node's actual content. Scalar marks are exact (modulo the
		/// trailing newlines a literal/folded span includes); collection marks are collapsed, so a
		/// block collection's end is its last entry's end, and a flow collection's is its closing
		/// bracket, scanned forward in the text.
		/// </summary>
		private static int AdjustedEnd(string text, YamlNode node, SettingsPath path)
		{
			switch (node)
			{
				case YamlScalarNode scalar:
					return TrimTrailingNewlines(text, (int)scalar.Start.Index, (int)scalar.End.Index);

				case YamlMappingNode mapping:
				{
					if (mapping.Style == MappingStyle.Flow)
						return FlowCloseEnd(text, mapping, '}', path);

					KeyValuePair<YamlNode, YamlNode>? last = null;
					foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
						last = pair;

					return AdjustedEnd(text, last.Value.Value, path);
				}

				case YamlSequenceNode sequence:
				{
					if (sequence.Style == SequenceStyle.Flow)
						return FlowCloseEnd(text, sequence, ']', path);

					return AdjustedEnd(text, sequence.Children[sequence.Children.Count - 1], path);
				}

				default:
					throw new NotSupportedException($"Cannot edit '{path}': unsupported YAML node type '{node.GetType().Name}'.");
			}
		}

		/// <summary>Index just past a flow collection's closing bracket, scanned from its last entry's end.</summary>
		private static int FlowCloseEnd(string text, YamlNode collection, char close, SettingsPath path)
		{
			int scanFrom = (int)collection.Start.Index;

			if (collection is YamlMappingNode mapping)
			{
				foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
					scanFrom = AdjustedEnd(text, pair.Value, path);
			}
			else if (collection is YamlSequenceNode sequence && sequence.Children.Count > 0)
			{
				scanFrom = AdjustedEnd(text, sequence.Children[sequence.Children.Count - 1], path);
			}

			int closeIndex = text.IndexOf(close, scanFrom);
			if (closeIndex < 0)
				throw new NotSupportedException($"Cannot edit '{path}': could not locate the '{close}' closing a flow collection.");

			return closeIndex + 1;
		}

		private static int TrimTrailingNewlines(string text, int start, int end)
		{
			while (end > start && (text[end - 1] == '\n' || text[end - 1] == '\r'))
				end--;

			return end;
		}

		/// <summary>Index of the first character of the line containing <paramref name="index"/>.</summary>
		private static int LineStart(string text, int index)
		{
			int newline = text.LastIndexOf('\n', Math.Max(0, Math.Min(index, text.Length) - 1));
			return newline < 0 ? 0 : newline + 1;
		}

		/// <summary>Index just past the newline ending the line that contains <paramref name="index"/> (or the text's end).</summary>
		private static int LineEndInclusive(string text, int index)
		{
			int newline = text.IndexOf('\n', Math.Min(index, text.Length));
			return newline < 0 ? text.Length : newline + 1;
		}

		/// <summary>
		/// Where the line terminator that ends just before <paramref name="pastNewline"/> starts, so a
		/// cut there leaves that terminator — <c>\r\n</c> included — whole and behind.
		/// </summary>
		private static int TerminatorStart(string text, int pastNewline)
		{
			if (pastNewline <= 0 || pastNewline > text.Length || text[pastNewline - 1] != '\n')
				return pastNewline;

			return pastNewline > 1 && text[pastNewline - 2] == '\r' ? pastNewline - 2 : pastNewline - 1;
		}

		/// <summary>
		/// The terminator at <paramref name="terminatorStart"/>, as the ending every line the patcher
		/// writes into that spot must use. Endings are read one line at a time rather than once per
		/// file, so a file with mixed endings keeps each line's own. An unterminated last line has no
		/// ending to copy, so the file's first one stands in (and <c>\n</c> when it has none either).
		/// </summary>
		private static string LineEnding(string text, int terminatorStart)
		{
			if (terminatorStart >= 0 && terminatorStart < text.Length)
				return text[terminatorStart] == '\r' ? "\r\n" : "\n";

			int newline = text.IndexOf('\n');

			return newline > 0 && text[newline - 1] == '\r' ? "\r\n" : "\n";
		}

		// --- Splicing -----------------------------------------------------------------------------

		/// <summary>
		/// Replaces the existing value of <paramref name="keyNode"/> with <paramref name="newValue"/>.
		/// Replacing one inline scalar with another splices into the old scalar's exact span, so the
		/// rest of its line is untouched. Every other shape rewrites the key line, and then a comment
		/// on it is re-emitted on the rewritten key line: after the new inline value, or after the
		/// bare <c>key:</c> that introduces an emitted block. Comment-only lines indented past the key
		/// are part of the replaced subtree and go with it. Rewritten and emitted lines end with the
		/// key line's own terminator, so a CRLF file stays CRLF and a mixed-ending file keeps each
		/// line's ending.
		/// </summary>
		private static string ReplaceValue(string text, YamlNode keyNode, YamlNode oldValue, JToken newValue, SettingsPath path)
		{
			if (oldValue is YamlMappingNode oldMapping && oldMapping.Style == MappingStyle.Flow && oldMapping.Children.Count > 0)
				throw new NotSupportedException($"Cannot edit '{path}': the current value is a flow-style mapping ({{...}}). Rewrite it in block style to edit it here.");

			if (oldValue is YamlSequenceNode oldSequence && oldSequence.Style == SequenceStyle.Flow && oldSequence.Children.Count > 0)
				throw new NotSupportedException($"Cannot edit '{path}': the current value is a flow-style sequence ([...]). Rewrite it in block style to edit it here.");

			DemandNoAnchors(oldValue, path);

			int keyIndent = (int)keyNode.Start.Column - 1;
			int colonIndex = text.IndexOf(':', (int)keyNode.End.Index);
			if (colonIndex < 0)
				throw new NotSupportedException($"Cannot edit '{path}': could not locate the ':' after its key.");

			bool oldIsScalar = oldValue is YamlScalarNode;
			bool newIsInline = IsInline(newValue);
			int oldEnd = AdjustedEnd(text, oldValue, path);

			if (oldIsScalar && newIsInline)
			{
				int scalarStart = (int)oldValue.Start.Index;
				string inline = EmitInline(newValue);

				// A bare "key:" has an empty scalar with Start == End right after the colon, so there
				// may be no space to reuse; every other case splices into the old scalar's own span,
				// leaving the rest of the line — a trailing comment included — exactly where it was.
				string spliced = scalarStart == oldEnd ? " " + inline : inline;

				return text.Substring(0, scalarStart) + spliced + text.Substring(oldEnd);
			}

			// End of the key line's content: a CRLF file's '\r' stays outside it, so it is never
			// duplicated into a re-emitted comment and still terminates whatever line ends up there.
			int keyLineEnd = text.IndexOf('\n', colonIndex);
			if (keyLineEnd < 0)
				keyLineEnd = text.Length;
			if (keyLineEnd > colonIndex && text[keyLineEnd - 1] == '\r')
				keyLineEnd--;

			// Every cut below lands on a terminator's first character, never between a '\r' and its
			// '\n', so the ending that survives the splice is one the file already had.
			string newline = LineEnding(text, keyLineEnd);
			int cutEnd = oldEnd;

			// Comment-only lines indented past the key sat inside the block being replaced, so they go
			// with it rather than dangling under the new value. Exactly one terminator stays behind:
			// the one that ended the old value's own last line.
			if (oldEnd > keyLineEnd)
			{
				int firstTailLine = LineEndInclusive(text, oldEnd);
				int pastTail = SweepSubtreeComments(text, firstTailLine, keyIndent);

				if (pastTail != firstTailLine)
					cutEnd = TerminatorStart(text, pastTail);
			}

			// A comment on the key line: after the old value when that value fits on the key line,
			// between the colon and the newline when the value's block starts below it.
			string comment = FindKeyLineComment(text, oldEnd <= keyLineEnd ? oldEnd : colonIndex + 1, keyLineEnd);
			if (comment != null)
				cutEnd = Math.Max(cutEnd, keyLineEnd);

			string suffix = comment == null ? string.Empty : " " + comment;
			string replacement = newIsInline
				? " " + EmitInline(newValue) + suffix
				: suffix + newline + string.Join(newline, EmitBlockLines(newValue, keyIndent + _indentStep));

			return text.Substring(0, colonIndex + 1) + replacement + text.Substring(cutEnd);
		}

		/// <summary>
		/// The comment occupying the rest of a key's line, from its '#' through
		/// <paramref name="lineEnd"/>, or null when only whitespace follows <paramref name="from"/>.
		/// The caller ends the key line before its terminator, so the returned text carries no
		/// newline (a CRLF file's '\r' included) and the splice re-terminates it instead.
		/// </summary>
		private static string FindKeyLineComment(string text, int from, int lineEnd)
		{
			int at = from;
			while (at < lineEnd && (text[at] == ' ' || text[at] == '\t'))
				at++;

			return at < lineEnd && text[at] == '#' ? text.Substring(at, lineEnd - at) : null;
		}

		/// <summary>
		/// Start of the first line at <paramref name="lineStart"/> or past it that does not hold a
		/// comment indented deeper than <paramref name="keyIndent"/>. Those lines belonged to the
		/// subtree under that key; a blank line or anything at the key's own indent ends the sweep.
		/// </summary>
		private static int SweepSubtreeComments(string text, int lineStart, int keyIndent)
		{
			int at = lineStart;

			while (at < text.Length)
			{
				int nextLine = LineEndInclusive(text, at);
				int content = at;
				while (content < nextLine && (text[content] == ' ' || text[content] == '\t'))
					content++;

				if (content >= nextLine || text[content] != '#' || content - at <= keyIndent)
					break;

				at = nextLine;
			}

			return at;
		}

		/// <summary>
		/// Inserts a new entry's lines after the parent mapping's last entry (and its line's trailing
		/// comment, if any), ending them the way the line they follow ends. Comment-only lines
		/// indented deeper than the new key still belong to that last entry's subtree, so the entry
		/// lands after them rather than adopting them: the same rule
		/// <see cref="SweepSubtreeComments"/> applies when a subtree is replaced or removed, which is
		/// what keeps an insert and a later removal of the same key a byte-for-byte round trip.
		/// </summary>
		private static string InsertEntry(string text, YamlMappingNode parent, List<string> lines)
		{
			KeyValuePair<YamlNode, YamlNode>? last = null;
			foreach (KeyValuePair<YamlNode, YamlNode> pair in parent.Children)
				last = pair;

			int insertAt = LineEndInclusive(text, AdjustedEnd(text, last.Value.Value, SettingsPath.Root));
			insertAt = SweepSubtreeComments(text, insertAt, IndentOf(parent));
			string newline = LineEnding(text, TerminatorStart(text, insertAt));
			string entryText = string.Join(newline, lines) + newline;

			if (insertAt >= text.Length && text.Length > 0 && text[text.Length - 1] != '\n')
				entryText = newline + entryText;

			return text.Substring(0, insertAt) + entryText + text.Substring(Math.Min(insertAt, text.Length));
		}

		/// <summary>
		/// Appends entry lines to a document with no mapping yet, keeping any leading comments and
		/// ending the new lines the way the last existing line ends.
		/// </summary>
		private static string AppendLines(string text, List<string> lines)
		{
			string newline = LineEnding(text, TerminatorStart(text, text.Length));
			string entryText = string.Join(newline, lines) + newline;

			if (text.Length == 0)
				return entryText;

			return text[text.Length - 1] == '\n' ? text + entryText : text + newline + entryText;
		}

		/// <summary>
		/// Writes the first entry of a tier whose document is an empty-tier placeholder (<c>~</c>,
		/// <c>null</c>, <c>{}</c>): the emitted mapping takes the placeholder's place, because YAML
		/// cannot hold a scalar or flow collection above a block mapping. The placeholder's line goes
		/// with it unless the line holds something else too — a comment, a <c>---</c> marker — which
		/// stays on its own line above the mapping, stripped of the whitespace the placeholder left
		/// behind; every other line, comments and blanks included, is untouched. An anchor on the
		/// placeholder is rejected like any other anchor on an edited span.
		/// </summary>
		private static string ReplaceEmptyRoot(string text, List<string> lines, YamlNode placeholder, SettingsPath path)
		{
			DemandNoAnchors(placeholder, path);

			// An empty scalar at the very end of the text (a lone "---") marks one past the last
			// character, so both ends are clamped before they index the string.
			int start = Math.Min((int)placeholder.Start.Index, text.Length);
			int end = Math.Min(Math.Max(AdjustedEnd(text, placeholder, path), start), text.Length);
			int lineStart = LineStart(text, start);
			int lineEnd = LineEndInclusive(text, end);
			string newline = LineEnding(text, TerminatorStart(text, lineEnd));
			string entryText = string.Join(newline, lines) + newline;

			string keptLine = (text.Substring(lineStart, start - lineStart) + text.Substring(end, lineEnd - end))
				.Trim(' ', '\t', '\r', '\n');

			if (keptLine.Length == 0)
				return text.Substring(0, lineStart) + entryText + text.Substring(lineEnd);

			return text.Substring(0, lineStart) + keptLine + newline + entryText + text.Substring(lineEnd);
		}

		// --- Emission -----------------------------------------------------------------------------

		/// <summary>Nests <paramref name="value"/> under the path segments from <paramref name="fromIndex"/> on.</summary>
		private static JToken WrapInChain(string[] segments, int fromIndex, JToken value)
		{
			JToken result = value ?? JValue.CreateNull();

			for (int i = segments.Length - 1; i >= fromIndex; i--)
				result = new JObject { [segments[i]] = result };

			return result;
		}

		/// <summary>True for values emitted on the key's own line: scalars and empty collections.</summary>
		private static bool IsInline(JToken value)
		{
			if (value == null || value is JValue)
				return true;

			return value is JContainer container && !container.HasValues;
		}

		private static string EmitInline(JToken value)
		{
			if (value == null)
				return "null";

			switch (value.Type)
			{
				case JTokenType.Object:
					return "{}";

				case JTokenType.Array:
					return "[]";

				case JTokenType.String:
					return EmitString((string)value);

				case JTokenType.Float:
				{
					// JSON has no infinity/NaN literals; the 1.2 Core spellings read back correctly.
					double number = (double)value;
					if (double.IsPositiveInfinity(number))
						return ".inf";
					if (double.IsNegativeInfinity(number))
						return "-.inf";
					if (double.IsNaN(number))
						return ".nan";

					return value.ToString(Formatting.None);
				}

				default:
					// null, bool, integer, etc.: their JSON text is a valid YAML 1.2 Core scalar of the
					// same type, and matches what SettingsTierWriter.LeafEquals round-trips through.
					return value.ToString(Formatting.None);
			}
		}

		/// <summary>
		/// A string is written plain only when it is conservatively safe: it must not start with an
		/// indicator, contain a construct that changes parsing, or be re-typed by the read schema
		/// (<c>true</c>, <c>5</c>, <c>~</c>, ...). Everything else is written as a JSON double-quoted
		/// string, which is valid YAML; a multiline string therefore becomes one escaped line.
		/// </summary>
		private static string EmitString(string value)
		{
			return IsPlainSafe(value) ? value : JsonConvert.ToString(value);
		}

		private const string _indicators = "-?:,[]{}#&*!|>'\"%@`";

		private static bool IsPlainSafe(string value)
		{
			if (string.IsNullOrEmpty(value))
				return false;

			if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
				return false;

			if (_indicators.IndexOf(value[0]) >= 0)
				return false;

			foreach (char c in value)
			{
				if (c < ' ' || c == '\u007f')
					return false;
			}

			if (value.Contains(": ") || value[value.Length - 1] == ':' || value.Contains(" #"))
				return false;

			return !YamlTokenReader.WouldRetypeAsPlainScalar(value);
		}

		/// <summary>One entry (<c>key: value</c> or <c>key:</c> plus a nested block) as lines without trailing newlines.</summary>
		private static List<string> EmitEntryLines(string key, JToken value, int indent)
		{
			var lines = new List<string>();
			EmitEntry(lines, key, value, indent);
			return lines;
		}

		private static void EmitEntry(List<string> lines, string key, JToken value, int indent)
		{
			string prefix = new string(' ', indent) + EmitString(key) + ":";

			if (IsInline(value))
			{
				lines.Add(prefix + " " + EmitInline(value));
			}
			else
			{
				lines.Add(prefix);
				lines.AddRange(EmitBlockLines(value, indent + _indentStep));
			}
		}

		/// <summary>A non-empty object or array as block-style lines at <paramref name="indent"/>, without trailing newlines.</summary>
		private static List<string> EmitBlockLines(JToken value, int indent)
		{
			var lines = new List<string>();

			if (value is JObject obj)
			{
				foreach (JProperty property in obj.Properties())
					EmitEntry(lines, property.Name, property.Value, indent);
			}
			else if (value is JArray array)
			{
				string prefix = new string(' ', indent) + "-";

				foreach (JToken item in array)
				{
					if (IsInline(item))
					{
						lines.Add(prefix + " " + EmitInline(item));
					}
					else
					{
						lines.Add(prefix);
						lines.AddRange(EmitBlockLines(item, indent + _indentStep));
					}
				}
			}
			else
			{
				throw new InvalidOperationException($"EmitBlockLines called with an inline token of type {value?.Type.ToString() ?? "null"}.");
			}

			return lines;
		}
	}
}
