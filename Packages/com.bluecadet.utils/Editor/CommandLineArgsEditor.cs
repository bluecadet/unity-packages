using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Bluecadet.Utils;

[CustomEditor(typeof(CommandLineArgs))]
public class CommandLineArgsEditor : Editor
{
	private bool _foldout = true;

	public override void OnInspectorGUI()
	{
		DrawDefaultInspector();

		EditorGUILayout.Space();

		var args = (CommandLineArgs)target;
		Dictionary<string, string> parsed;

		if (Application.isPlaying)
		{
			parsed = new Dictionary<string, string>(args.ParsedArgs);
		}
		else
		{
			var field = typeof(CommandLineArgs).GetField("_editorArgs",
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			string editorArgsValue = field?.GetValue(args) as string ?? string.Empty;
			parsed = ParseArgs(editorArgsValue);
		}

		_foldout = EditorGUILayout.BeginFoldoutHeaderGroup(_foldout, "Parsed Args Preview");
		if (_foldout)
		{
			EditorGUI.indentLevel++;
			if (parsed.Count == 0)
			{
				EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
			}
			else
			{
				foreach (var kvp in parsed)
				{
					string display = string.IsNullOrEmpty(kvp.Value)
						? $"{kvp.Key}  →  (flag)"
						: $"{kvp.Key}  →  \"{kvp.Value}\"";
					EditorGUILayout.LabelField(display, EditorStyles.miniLabel);
				}
			}
			EditorGUI.indentLevel--;
		}
		EditorGUILayout.EndFoldoutHeaderGroup();
	}

	private static Dictionary<string, string> ParseArgs(string input)
	{
		var result = new Dictionary<string, string>();

		if (string.IsNullOrWhiteSpace(input))
			return result;

		string[] tokens = input.Split(new char[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

		for (int i = 0; i < tokens.Length; i++)
		{
			string token = tokens[i];

			if (!token.StartsWith("-"))
				continue;

			int equalsIndex = token.IndexOf('=');
			if (equalsIndex >= 0)
			{
				string key = token.Substring(0, equalsIndex);
				string value = token.Substring(equalsIndex + 1);
				result[key] = value;
				continue;
			}

			if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("-"))
			{
				result[token] = tokens[i + 1];
				i++;
			}
			else
			{
				result[token] = string.Empty;
			}
		}

		return result;
	}
}
