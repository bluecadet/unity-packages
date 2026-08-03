using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Bluecadet.Utils
{
	/// <summary>
	/// Immutable, parsed view of command-line style arguments.
	/// In a build, parses <see cref="Environment.GetCommandLineArgs"/>.
	/// In the editor, reads a simulated-args text file so command-line
	/// behavior can be exercised without leaving the editor.
	/// </summary>
	public sealed class CommandLineArgs
	{
		/// <summary>
		/// Project path (relative to the project root, the parent of <see cref="Application.dataPath"/>)
		/// to the text file used to simulate command-line arguments while running in the editor.
		/// Shared with the editor assembly so it can read/write the same file.
		/// </summary>
		internal const string SimulatedArgsProjectPath = "ProjectSettings/EditorSimulatedArgs.txt";

		private readonly Dictionary<string, string> _values;
		private readonly List<KeyValuePair<string, string>> _occurrences;

		/// <summary>
		/// All parsed arguments, keyed by normalized (lower-case, dash-stripped) name.
		/// When a name repeats, the last occurrence wins.
		/// </summary>
		public IReadOnlyDictionary<string, string> All => _values;

		/// <summary>
		/// Every parsed (name, value) pair in the order it appeared, including repeats.
		/// Used by <see cref="SettingsFile{T}"/> to apply every repeatable <c>--set</c> occurrence.
		/// </summary>
		internal IReadOnlyList<KeyValuePair<string, string>> Occurrences => _occurrences;

		private CommandLineArgs(List<KeyValuePair<string, string>> occurrences)
		{
			_occurrences = occurrences;
			_values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var occurrence in occurrences)
				_values[occurrence.Key] = occurrence.Value;
		}

		/// <summary>
		/// Builds a <see cref="CommandLineArgs"/> from the current process.
		/// In builds this parses <see cref="Environment.GetCommandLineArgs"/>; in the editor
		/// it instead reads the simulated-args file at <see cref="SimulatedArgsProjectPath"/>
		/// (a missing file yields empty args).
		/// </summary>
		public static CommandLineArgs FromProcess()
		{
#if UNITY_EDITOR
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			string path = Path.Combine(projectRoot ?? string.Empty, SimulatedArgsProjectPath);
			string text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
			return ParseText(text);
#else
			return Parse(Environment.GetCommandLineArgs());
#endif
		}

		/// <summary>
		/// Parses an argv-style array of tokens (e.g. as returned by <see cref="Environment.GetCommandLineArgs"/>).
		/// Supports <c>--flag value</c>, <c>--key=value</c>, and bare <c>--flag</c> (which maps to an empty string).
		/// Names are case-insensitive and leading dashes are normalized; the last occurrence of a
		/// repeated name wins in <see cref="All"/>.
		/// </summary>
		public static CommandLineArgs Parse(params string[] argv)
		{
			var occurrences = new List<KeyValuePair<string, string>>();
			if (argv == null)
				return new CommandLineArgs(occurrences);

			for (int i = 0; i < argv.Length; i++)
			{
				string token = argv[i];
				if (string.IsNullOrEmpty(token) || !token.StartsWith("-", StringComparison.Ordinal))
					continue;

				int equalsIndex = token.IndexOf('=');
				if (equalsIndex >= 0)
				{
					string name = NormalizeName(token.Substring(0, equalsIndex));
					string value = token.Substring(equalsIndex + 1);
					occurrences.Add(new KeyValuePair<string, string>(name, value));
					continue;
				}

				string flagName = NormalizeName(token);
				bool nextIsValue = i + 1 < argv.Length
					&& !string.IsNullOrEmpty(argv[i + 1])
					&& !argv[i + 1].StartsWith("-", StringComparison.Ordinal);

				if (nextIsValue)
				{
					occurrences.Add(new KeyValuePair<string, string>(flagName, argv[i + 1]));
					i++;
				}
				else
				{
					occurrences.Add(new KeyValuePair<string, string>(flagName, string.Empty));
				}
			}

			return new CommandLineArgs(occurrences);
		}

		/// <summary>
		/// Tokenizes <paramref name="text"/> on whitespace, honoring double-quoted strings
		/// (so a quoted value may contain spaces), then parses the resulting tokens.
		/// </summary>
		public static CommandLineArgs ParseText(string text)
		{
			return Parse(Tokenize(text));
		}

		/// <summary>Returns true if a flag with the given name was parsed.</summary>
		public bool HasFlag(string name) => _values.ContainsKey(NormalizeName(name));

		/// <summary>Returns the value for the given flag name, or <paramref name="fallback"/> if not present.</summary>
		public string Get(string name, string fallback = null) =>
			_values.TryGetValue(NormalizeName(name), out string value) ? value : fallback;

		/// <summary>Attempts to get the value for the given flag name.</summary>
		public bool TryGet(string name, out string value) => _values.TryGetValue(NormalizeName(name), out value);

		private static string NormalizeName(string name) => (name ?? string.Empty).TrimStart('-').ToLowerInvariant();

		private static string[] Tokenize(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return Array.Empty<string>();

			var tokens = new List<string>();
			var current = new StringBuilder();
			bool inQuotes = false;
			bool hasToken = false;

			foreach (char c in text)
			{
				if (c == '"')
				{
					inQuotes = !inQuotes;
					hasToken = true;
					continue;
				}

				if (!inQuotes && char.IsWhiteSpace(c))
				{
					if (hasToken)
					{
						tokens.Add(current.ToString());
						current.Clear();
						hasToken = false;
					}
					continue;
				}

				current.Append(c);
				hasToken = true;
			}

			if (hasToken)
				tokens.Add(current.ToString());

			return tokens.ToArray();
		}
	}
}
