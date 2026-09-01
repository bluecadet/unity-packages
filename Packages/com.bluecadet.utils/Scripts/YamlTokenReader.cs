using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Bluecadet.Utils
{
	/// <summary>
	/// Converts YAML settings text into the same <see cref="JObject"/> shape the JSON tiers parse to,
	/// so everything downstream of tier loading (merging, dotted paths, typed hydration) stays
	/// format-agnostic. Plain (unquoted) scalars are typed per the YAML 1.2 Core schema; quoted,
	/// literal and folded scalars are always strings. Notably, <c>yes</c>/<c>no</c>/<c>on</c>/<c>off</c>
	/// are plain strings in 1.2, not booleans. Merge keys (<c>&lt;&lt;</c>) are not interpreted: they
	/// come through as a literal <c>"&lt;&lt;"</c> key.
	/// </summary>
	internal static class YamlTokenReader
	{
		private static readonly Regex _intPattern = new Regex(@"^[-+]?[0-9]+$", RegexOptions.Compiled);
		private static readonly Regex _hexPattern = new Regex(@"^0x[0-9a-fA-F]+$", RegexOptions.Compiled);
		private static readonly Regex _octalPattern = new Regex(@"^0o[0-7]+$", RegexOptions.Compiled);
		private static readonly Regex _floatPattern = new Regex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.Compiled);
		private static readonly Regex _infinityPattern = new Regex(@"^[-+]?\.(inf|Inf|INF)$", RegexOptions.Compiled);
		private static readonly Regex _nanPattern = new Regex(@"^\.(nan|NaN|NAN)$", RegexOptions.Compiled);

		/// <summary>
		/// Parses <paramref name="text"/> as a single YAML document with a mapping root. An empty or
		/// comments-only document is an empty object; a non-mapping root, a duplicate key, or a cyclic
		/// alias throws.
		/// </summary>
		internal static JObject ToJObject(string text)
		{
			var stream = new YamlStream();
			using (var reader = new StringReader(text ?? string.Empty))
				stream.Load(reader);

			if (stream.Documents.Count == 0)
				return new JObject();

			YamlNode root = stream.Documents[0].RootNode;

			if (root is YamlScalarNode scalar && scalar.Style == ScalarStyle.Plain && IsNullLiteral(scalar.Value ?? string.Empty))
				return new JObject();

			if (!(root is YamlMappingNode mapping))
				throw new FormatException("The root of a YAML settings document must be a mapping (key: value pairs).");

			// The ancestor set turns the alias-induced object graph back into a tree walk: a shared
			// (non-cyclic) alias is converted once per use site, while a self-referencing alias throws
			// instead of recursing forever.
			return (JObject)ConvertNode(mapping, new HashSet<YamlNode>());
		}

		private static JToken ConvertNode(YamlNode node, HashSet<YamlNode> ancestors)
		{
			if (!ancestors.Add(node))
				throw new FormatException("The YAML document aliases a node into itself, which cannot be represented as settings.");

			try
			{
				switch (node)
				{
					case YamlScalarNode scalar:
						return ConvertScalar(scalar);

					case YamlMappingNode mapping:
					{
						var result = new JObject();
						foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
						{
							if (!(pair.Key is YamlScalarNode key))
								throw new FormatException("YAML mapping keys in settings must be scalars.");

							result[key.Value ?? string.Empty] = ConvertNode(pair.Value, ancestors);
						}

						return result;
					}

					case YamlSequenceNode sequence:
					{
						var result = new JArray();
						foreach (YamlNode item in sequence.Children)
							result.Add(ConvertNode(item, ancestors));

						return result;
					}

					default:
						throw new FormatException($"Unsupported YAML node type '{node.GetType().Name}' in settings.");
				}
			}
			finally
			{
				ancestors.Remove(node);
			}
		}

		private static JToken ConvertScalar(YamlScalarNode scalar)
		{
			string value = scalar.Value ?? string.Empty;

			if (scalar.Style != ScalarStyle.Plain)
				return new JValue(value);

			if (IsNullLiteral(value))
				return JValue.CreateNull();

			if (value == "true" || value == "True" || value == "TRUE")
				return new JValue(true);

			if (value == "false" || value == "False" || value == "FALSE")
				return new JValue(false);

			if (_intPattern.IsMatch(value))
			{
				if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
					return new JValue(longValue);

				// Too large for a long: widen to double rather than silently becoming a string.
				if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double widened))
					return new JValue(widened);

				return new JValue(value);
			}

			if (_hexPattern.IsMatch(value) && TryParseRadix(value.Substring(2), 16, out long hexValue))
				return new JValue(hexValue);

			if (_octalPattern.IsMatch(value) && TryParseRadix(value.Substring(2), 8, out long octalValue))
				return new JValue(octalValue);

			if (_floatPattern.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
				return new JValue(doubleValue);

			if (_infinityPattern.IsMatch(value))
				return new JValue(value[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity);

			if (_nanPattern.IsMatch(value))
				return new JValue(double.NaN);

			return new JValue(value);
		}

		/// <summary>True for the plain scalars the 1.2 Core schema reads as null: empty, <c>~</c>, <c>null</c>/<c>Null</c>/<c>NULL</c>.</summary>
		internal static bool IsNullLiteral(string value) =>
			value.Length == 0 || value == "~" || value == "null" || value == "Null" || value == "NULL";

		private static bool TryParseRadix(string digits, int radix, out long result)
		{
			try
			{
				result = Convert.ToInt64(digits, radix);
				return true;
			}
			catch (OverflowException)
			{
				result = 0;
				return false;
			}
		}
	}
}
