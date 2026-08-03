using UnityEditor;
using UnityEngine;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// Project Settings pane ("Project/Bluecadet") composing the Bluecadet Utils editor
	/// workflows: <see cref="SimulatedArgsPane"/> for the simulated command-line args file,
	/// and <see cref="SettingsInspectorPane"/> for the <see cref="SettingsFile{T}"/> tier cascade.
	/// </summary>
	internal sealed class BluecadetSettingsProvider : SettingsProvider
	{
		private readonly SimulatedArgsPane _argsPane = new();
		private readonly SettingsInspectorPane _settingsPane = new();

		private bool _initialized;

		private BluecadetSettingsProvider(string path, SettingsScope scope) : base(path, scope) { }

		/// <summary>Registers the "Project/Bluecadet" settings pane.</summary>
		[SettingsProvider]
		public static SettingsProvider CreateSettingsProvider()
		{
			return new BluecadetSettingsProvider("Project/Bluecadet", SettingsScope.Project)
			{
				keywords = new[] { "Bluecadet", "command line", "args", "settings" }
			};
		}

		/// <inheritdoc />
		public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
		{
			_argsPane.Activate();
			_settingsPane.Activate();

			_initialized = true;
		}

		/// <inheritdoc />
		public override void OnGUI(string searchContext)
		{
			if (!_initialized)
				OnActivate(searchContext, null);

			_argsPane.Draw();

			EditorGUILayout.Space(16);
			GUILayout.Box(GUIContent.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
			EditorGUILayout.Space(8);

			_settingsPane.Draw();
		}
	}
}
