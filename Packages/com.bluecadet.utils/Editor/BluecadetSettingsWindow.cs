using UnityEditor;
using UnityEngine;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// Dockable window ("Tools/Bluecadet/Settings") hosting <see cref="SettingsEditorPane"/>, the typed
	/// editor for the <see cref="SettingsFile{T}"/> tier cascade.
	/// </summary>
	internal sealed class BluecadetSettingsWindow : EditorWindow
	{
		[SerializeField] private SettingsEditorPane _pane = new();
		[SerializeField] private Vector2 _scroll;

		[MenuItem("Tools/Bluecadet/Settings")]
		private static void Open()
		{
			GetWindow<BluecadetSettingsWindow>().Show();
		}

		private void OnEnable()
		{
			titleContent = new GUIContent("Bluecadet Settings");

			_pane ??= new SettingsEditorPane();
			_pane.Activate();
		}

		private void OnDisable()
		{
			_pane?.Deactivate();
		}

		private void OnGUI()
		{
			_scroll = EditorGUILayout.BeginScrollView(_scroll);
			try
			{
				_pane.Draw();
			}
			finally
			{
				// An exception mid-draw would otherwise leave IMGUI's layout stack unbalanced for every
				// subsequent repaint, burying the real error under "EndLayoutGroup" spam.
				EditorGUILayout.EndScrollView();
			}
		}
	}
}
