using UnityEditor;
using UnityEngine;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// Dockable window ("Tools/Bluecadet/Simulated Args") hosting <see cref="SimulatedArgsPane"/>, the
	/// editor for the simulated command-line args file read by <see cref="CommandLineArgs.FromProcess"/>.
	/// </summary>
	internal sealed class SimulatedArgsWindow : EditorWindow
	{
		private readonly SimulatedArgsPane _pane = new();

		[SerializeField] private Vector2 _scroll;

		[MenuItem("Tools/Bluecadet/Simulated Args")]
		private static void Open()
		{
			GetWindow<SimulatedArgsWindow>().Show();
		}

		private void OnEnable()
		{
			titleContent = new GUIContent("Simulated Args");

			_pane.Activate();
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
