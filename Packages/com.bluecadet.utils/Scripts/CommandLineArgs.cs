using System;
using System.Collections.Generic;
using UnityEngine;

namespace Bluecadet.Utils
{
	public class CommandLineArgs : Singleton<CommandLineArgs>
	{
		[SerializeField]
		[Tooltip("If true, this object persists when loading new scenes.")]
		private bool _persistAcrossScenes = true;

#if UNITY_EDITOR
		[SerializeField]
		[TextArea(3, 8)]
		[Tooltip("Editor-only: Simulate CLI flags. Write args exactly as you would on the command line (e.g. '--port 8080 --env=staging --verbose'). Newlines and extra spaces are treated as delimiters. Ignored in builds.")]
		private string _editorArgs = string.Empty;
#endif

		private readonly Dictionary<string, string> _parsedArgs = new();

		public IReadOnlyDictionary<string, string> ParsedArgs => _parsedArgs;

		private void Awake()
		{
			if (_persistAcrossScenes)
				DontDestroyOnLoad(gameObject);

#if UNITY_EDITOR
			ParseArgs(TokenizeString(_editorArgs));
#else
			ParseArgs(System.Environment.GetCommandLineArgs());
#endif
		}

		private void ParseArgs(string[] args)
		{
			_parsedArgs.Clear();

			for (int i = 0; i < args.Length; i++)
			{
				string arg = args[i];

				if (!arg.StartsWith("-"))
					continue;

				int equalsIndex = arg.IndexOf('=');
				if (equalsIndex >= 0)
				{
					string key = arg.Substring(0, equalsIndex);
					string value = arg.Substring(equalsIndex + 1);
					_parsedArgs[key] = value;
					continue;
				}

				if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
				{
					_parsedArgs[arg] = args[i + 1];
					i++;
				}
				else
				{
					_parsedArgs[arg] = string.Empty;
				}
			}
		}

		private static string[] TokenizeString(string input)
		{
			if (string.IsNullOrWhiteSpace(input))
				return Array.Empty<string>();

			return input.Split(new char[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
		}

		public bool HasFlag(string flag) => _parsedArgs.ContainsKey(flag);

		public string GetArg(string key, string defaultValue = null)
		{
			return _parsedArgs.TryGetValue(key, out string value) ? value : defaultValue;
		}

		public bool TryGetArg(string key, out string value)
		{
			return _parsedArgs.TryGetValue(key, out value);
		}
	}
}
