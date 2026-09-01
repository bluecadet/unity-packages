using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Utils
{
	/// <summary>The on-disk format of a settings tier file, decided by its file extension.</summary>
	internal enum SettingsFormat
	{
		Json,
		Yaml
	}

	/// <summary>
	/// Format probing and parsing for settings tier files. Only the <c>.yaml</c> extension is
	/// recognized as YAML (<c>.yml</c> is not); every other extension parses as JSON.
	/// </summary>
	internal static class SettingsFormatIO
	{
		internal static SettingsFormat FormatFor(string path) =>
			string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase)
				? SettingsFormat.Yaml
				: SettingsFormat.Json;

		/// <summary>
		/// Parses the tier file at <paramref name="path"/> in the format its extension names.
		/// A missing or empty file is an empty object; malformed content throws.
		/// </summary>
		internal static JObject Parse(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return new JObject();

			return ParseText(File.ReadAllText(path), FormatFor(path));
		}

		/// <summary>Parses settings text in <paramref name="format"/>; blank text is an empty object.</summary>
		internal static JObject ParseText(string text, SettingsFormat format)
		{
			if (string.IsNullOrWhiteSpace(text))
				return new JObject();

			return format == SettingsFormat.Yaml ? YamlTokenReader.ToJObject(text) : JObject.Parse(text);
		}
	}
}
