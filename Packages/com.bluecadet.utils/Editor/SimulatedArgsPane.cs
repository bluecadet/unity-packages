using System.IO;
using UnityEditor;
using UnityEngine;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// Contents of the "Tools/Bluecadet/Simulated Args" window: edits the simulated
	/// command-line args file consumed by <see cref="CommandLineArgs.FromProcess"/>
	/// while running in the editor.
	/// </summary>
	internal sealed class SimulatedArgsPane
	{
		private string _text = string.Empty;

		public void Activate()
		{
			string path = GetAbsolutePath();
			_text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
		}

		public void Draw()
		{
			EditorGUILayout.LabelField("Simulated Command-Line Args", EditorStyles.boldLabel);
			EditorGUILayout.HelpBox(
				$"Editor-only. Saved to '{CommandLineArgs.SimulatedArgsProjectPath}' (relative to the project root) " +
				"and read by CommandLineArgs.FromProcess() while running in the editor. Write arguments exactly as " +
				"you would on the command line, e.g. --env=staging --port 8080 --verbose.",
				MessageType.Info);

			EditorGUI.BeginChangeCheck();
			_text = EditorGUILayout.TextArea(_text, GUILayout.MinHeight(60));
			if (EditorGUI.EndChangeCheck())
				WriteFile();

			EditorGUILayout.LabelField("Parsed preview", EditorStyles.miniBoldLabel);
			using (new EditorGUI.DisabledScope(true))
			{
				CommandLineArgs parsed = CommandLineArgs.ParseText(_text);
				if (parsed.All.Count == 0)
				{
					EditorGUILayout.LabelField("(no arguments)");
				}
				else
				{
					foreach (var pair in parsed.All)
						EditorGUILayout.LabelField(pair.Key, string.IsNullOrEmpty(pair.Value) ? "(flag)" : pair.Value);
				}
			}
		}

		private static string GetAbsolutePath()
		{
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			return Path.Combine(projectRoot ?? string.Empty, CommandLineArgs.SimulatedArgsProjectPath);
		}

		private void WriteFile()
		{
			string path = GetAbsolutePath();
			string directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(path, _text ?? string.Empty);
		}
	}
}
