using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// Section of the "Project/Bluecadet" settings pane for inspecting the
	/// <see cref="SettingsFile{T}"/> tier cascade for a given settings base name,
	/// and editing one file tier at a time.
	/// </summary>
	internal sealed class SettingsInspectorPane
	{
		private const string _baseNameEditorPrefsKey = "Bluecadet.Utils.SettingsBaseName";
		private const string _defaultBaseName = "settings";

		/// <summary>Descriptive stand-in shown for <see cref="SettingsTier.Cli"/>, which has no backing file.</summary>
		private const string _cliTierLabel = "(--set command-line arguments)";

		private static readonly string[] _fileTierNames = SettingsCascade.FileTiers.Select(tier => tier.ToString()).ToArray();

		private string _baseName = _defaultBaseName;
		private SettingsCascade _cascade;
		private string _mergedJsonText = "{}";
		private readonly List<(string Path, JToken Value, SettingsTier? Tier)> _provenance = new();

		private SettingsTier _editTier = SettingsTier.Base;
		private string _editText = "{}";
		private string _parseError;

		private Vector2 _mergedJsonScroll;
		private Vector2 _provenanceScroll;
		private Vector2 _editScroll;

		public void Activate()
		{
			_baseName = EditorPrefs.GetString(_baseNameEditorPrefsKey, _defaultBaseName);
			Reload();
		}

		public void Draw()
		{
			EditorGUILayout.LabelField("Settings File Inspector", EditorStyles.boldLabel);

			EditorGUI.BeginChangeCheck();
			string newBaseName = EditorGUILayout.TextField("Base Name", _baseName);
			if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(newBaseName))
			{
				_baseName = newBaseName;
				EditorPrefs.SetString(_baseNameEditorPrefsKey, _baseName);
				Reload();
			}

			if (GUILayout.Button("Reload", GUILayout.Width(100)))
				Reload();

			foreach (string warning in _cascade.Warnings)
				EditorGUILayout.HelpBox(warning, MessageType.Warning);

			DrawTierPaths();

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Provenance", EditorStyles.miniBoldLabel);
			DrawProvenance();

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Merged Settings (read-only)", EditorStyles.miniBoldLabel);
			_mergedJsonScroll = EditorGUILayout.BeginScrollView(_mergedJsonScroll, GUILayout.MinHeight(120));
			using (new EditorGUI.DisabledScope(true))
				EditorGUILayout.TextArea(_mergedJsonText, GUILayout.ExpandHeight(true));
			EditorGUILayout.EndScrollView();

			EditorGUILayout.Space(8);
			DrawTierEditor();
		}

		private void DrawTierPaths()
		{
			foreach (SettingsTier tier in SettingsCascade.FileTiers)
			{
				string path = _cascade.PathFor(tier);
				EditorGUILayout.LabelField(tier.ToString(), File.Exists(path) ? path : $"{path} (missing)");
			}

			EditorGUILayout.LabelField(SettingsTier.Cli.ToString(), _cliTierLabel);
		}

		private void DrawProvenance()
		{
			_provenanceScroll = EditorGUILayout.BeginScrollView(_provenanceScroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(200));

			if (_provenance.Count == 0)
			{
				EditorGUILayout.LabelField("(no settings)");
			}
			else
			{
				foreach (var entry in _provenance)
				{
					string tierLabel = entry.Tier.HasValue ? entry.Tier.Value.ToString() : "none";
					EditorGUILayout.LabelField(entry.Path, $"{entry.Value} [{tierLabel}]");
				}
			}

			EditorGUILayout.EndScrollView();
		}

		private void DrawTierEditor()
		{
			EditorGUILayout.LabelField("Edit Tier", EditorStyles.miniBoldLabel);

			EditorGUI.BeginChangeCheck();
			int selected = EditorGUILayout.Popup("Tier", Array.IndexOf(SettingsCascade.FileTiers, _editTier), _fileTierNames);
			if (EditorGUI.EndChangeCheck())
			{
				_editTier = SettingsCascade.FileTiers[selected];
				LoadEditTier();
			}

			_editScroll = EditorGUILayout.BeginScrollView(_editScroll, GUILayout.MinHeight(160));
			_editText = EditorGUILayout.TextArea(_editText, GUILayout.ExpandHeight(true));
			EditorGUILayout.EndScrollView();

			if (!string.IsNullOrEmpty(_parseError))
				EditorGUILayout.HelpBox(_parseError, MessageType.Error);

			using (new EditorGUILayout.HorizontalScope())
			{
				if (GUILayout.Button($"Save {_editTier}"))
					SaveEditTier();

				using (new EditorGUI.DisabledScope(!File.Exists(_cascade.PathFor(_editTier))))
				{
					if (GUILayout.Button($"Delete {_editTier}"))
						DeleteEditTier();
				}
			}
		}

		private void Reload()
		{
			_cascade = new SettingsCascade(AppEnvironment.Current, _baseName);
			_mergedJsonText = _cascade.Merged.ToString(Formatting.Indented);

			RebuildProvenance();
			LoadEditTier();
		}

		private void RebuildProvenance()
		{
			_provenance.Clear();
			CollectProvenance(_cascade.Merged, string.Empty);
		}

		private void CollectProvenance(JObject obj, string prefix)
		{
			foreach (JProperty property in obj.Properties())
			{
				string path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";

				if (property.Value is JObject child && child.Properties().Any())
					CollectProvenance(child, path);
				else
					_provenance.Add((path, property.Value, _cascade.TierFor(path)));
			}
		}

		private void LoadEditTier()
		{
			string path = _cascade.PathFor(_editTier);
			_editText = File.Exists(path) ? File.ReadAllText(path) : "{}";
			_parseError = null;
		}

		private void SaveEditTier()
		{
			JObject edited;
			try
			{
				edited = JObject.Parse(_editText);
				_parseError = null;
			}
			catch (Exception ex)
			{
				_parseError = $"{_editTier} settings JSON is not valid: {ex.Message}";
				return;
			}

			string path = _cascade.PathFor(_editTier);
			string directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(path, edited.ToString(Formatting.Indented));
			RefreshAssetsIfInsideProject(path);

			Reload();
		}

		private void DeleteEditTier()
		{
			string path = _cascade.PathFor(_editTier);

			if (!EditorUtility.DisplayDialog("Delete settings file", $"Delete '{path}'?", "Delete", "Cancel"))
				return;

			if (File.Exists(path))
				File.Delete(path);

			if (File.Exists(path + ".meta"))
				File.Delete(path + ".meta");

			RefreshAssetsIfInsideProject(path);

			Reload();
		}

		/// <summary>
		/// Settings files often live under <c>Assets/StreamingAssets</c>, where the asset database
		/// keeps its own view of the directory and needs to be told the file changed.
		/// </summary>
		private static void RefreshAssetsIfInsideProject(string path)
		{
			string assetsRoot = Path.GetFullPath(Application.dataPath);
			if (Path.GetFullPath(path).StartsWith(assetsRoot, StringComparison.Ordinal))
				AssetDatabase.Refresh();
		}
	}
}
