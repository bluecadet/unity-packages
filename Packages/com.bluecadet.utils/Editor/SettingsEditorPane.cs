using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// Typed editor for the <see cref="SettingsFile{T}"/> tier cascade of a single settings base name:
	/// hydrates the merged cascade into the <c>[SettingsClass]</c> type registered for that base name,
	/// draws it with Unity's own property drawers, tracks which dotted paths were edited, and writes
	/// only those paths into the tier the user picks. Falls back to a read-only cascade view when no
	/// settings type is tagged for the current base name.
	/// </summary>
	[Serializable]
	internal sealed class SettingsEditorPane
	{
		private const string _baseNameEditorPrefsKey = "Bluecadet.Utils.SettingsBaseName";
		private const string _settingsTypeEditorPrefsKeyPrefix = "Bluecadet.Utils.SettingsType.";
		private const string _defaultBaseName = "settings";

		/// <summary>Name of the <see cref="SettingsWrapper"/> field the drawn properties hang off of.</summary>
		private const string _wrapperFieldName = "settings";

		/// <summary>Descriptive stand-in shown for <see cref="SettingsTier.Cli"/>, which has no backing file.</summary>
		private const string _cliTierLabel = "(--set command-line arguments)";

		private static readonly Color _dirtyTint = new Color(1f, 0.9f, 0.6f, 1f);
		private static readonly Color _localTint = new Color(0.5f, 0.85f, 1f, 1f);
		private static readonly Color _machineTint = new Color(0.6f, 1f, 0.7f, 1f);
		private static readonly Color _cliTint = new Color(0.75f, 0.75f, 0.75f, 1f);

		[SerializeField] private string _baseName;

		/// <summary>Dotted JSON paths edited since the last load or save. Serialized so edits survive a domain reload.</summary>
		[SerializeField] private List<string> _dirtyPaths = new();

		/// <summary>The in-progress settings instance as JSON, or null when it matches the merged cascade.</summary>
		[SerializeField] private string _editedJson;

		[SerializeField] private bool _showMergedJson;
		[SerializeField] private bool _showTierFiles;

		private SettingsCascade _cascade;
		private SettingsTierWriter _writer;
		private Type[] _candidateTypes = Array.Empty<Type>();
		private Type _settingsType;
		private object _instance;
		private SettingsWrapper _wrapper;
		private SerializedObject _wrapperObject;
		private string _mergedJsonText = "{}";
		private string _editorWarning;
		private SettingsTier? _pendingSaveTier;
		private SettingsTier? _pendingDeleteTier;
		private bool _pendingReload;
		private readonly List<(string Path, JToken Value, SettingsTier? Tier)> _provenance = new();
		private readonly HashSet<SettingsTier> _activeTiers = new();

		/// <summary>Throwaway host object that gives the settings instance a <see cref="SerializedObject"/> to draw through.</summary>
		private sealed class SettingsWrapper : ScriptableObject
		{
			[SerializeReference] public object settings;
		}

		public void Activate()
		{
			_dirtyPaths ??= new List<string>();

			if (string.IsNullOrEmpty(_baseName))
				_baseName = EditorPrefs.GetString(_baseNameEditorPrefsKey, _defaultBaseName);

			Reload(keepEdits: true);
		}

		public void Deactivate()
		{
			_wrapperObject?.Dispose();
			_wrapperObject = null;

			if (_wrapper != null)
				UnityEngine.Object.DestroyImmediate(_wrapper);

			_wrapper = null;
		}

		public void Draw()
		{
			if (_cascade == null)
				Activate();

			DrawToolbar();

			if (_settingsType != null)
				DrawTypedEditor();
			else
				DrawUntypedFallback();

			EditorGUILayout.Space(8);
			DrawFooter();

			ProcessPendingAction();
		}

		/// <summary>
		/// Runs whatever a button press asked for, after every control has been laid out: reloading or
		/// saving mid-draw would change the control count between the layout and repaint passes.
		/// </summary>
		private void ProcessPendingAction()
		{
			if (_pendingSaveTier.HasValue)
			{
				SettingsTier tier = _pendingSaveTier.Value;
				_pendingSaveTier = null;
				Save(tier);
			}
			else if (_pendingDeleteTier.HasValue)
			{
				SettingsTier tier = _pendingDeleteTier.Value;
				_pendingDeleteTier = null;
				DeleteTier(tier);
			}
			else if (_pendingReload)
			{
				_pendingReload = false;
				Reload(keepEdits: false);
			}
		}

		private void DrawToolbar()
		{
			using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
			{
				GUILayout.Label("Base Name", EditorStyles.miniLabel, GUILayout.Width(64));

				// Delayed: an eager field would reload (and so discard the edits) on every keystroke.
				string newBaseName = EditorGUILayout.DelayedTextField(_baseName, EditorStyles.toolbarTextField, GUILayout.Width(160));
				if (!string.IsNullOrEmpty(newBaseName) && !string.Equals(newBaseName, _baseName, StringComparison.Ordinal))
				{
					if (ConfirmDiscardingEdits($"Switching to '{newBaseName}' will discard unsaved settings edits."))
					{
						_baseName = newBaseName;
						EditorPrefs.SetString(_baseNameEditorPrefsKey, _baseName);
						_pendingReload = true;
					}
					else
					{
						// Drop focus so the field redraws with the restored name instead of the typed one.
						GUIUtility.keyboardControl = 0;
					}
				}

				GUILayout.FlexibleSpace();

				if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60)))
					_pendingReload = true;
			}

			DrawTypeSelector();
		}

		private void DrawTypeSelector()
		{
			if (_candidateTypes.Length < 2)
				return;

			var labels = new string[_candidateTypes.Length];
			for (int i = 0; i < _candidateTypes.Length; i++)
				labels[i] = _candidateTypes[i].FullName;

			EditorGUI.BeginChangeCheck();
			int selected = EditorGUILayout.Popup("Settings Class", Math.Max(0, Array.IndexOf(_candidateTypes, _settingsType)), labels);
			if (EditorGUI.EndChangeCheck())
			{
				EditorPrefs.SetString(SettingsTypeEditorPrefsKey, _candidateTypes[selected].AssemblyQualifiedName);
				_pendingReload = true;
			}
		}

		private void DrawTypedEditor()
		{
			if (_wrapperObject == null)
				return;

			if (!string.IsNullOrEmpty(_editorWarning))
				EditorGUILayout.HelpBox(_editorWarning, MessageType.Warning);

			_wrapperObject.Update();

			SerializedProperty root = _wrapperObject.FindProperty(_wrapperFieldName);
			if (root != null && root.hasVisibleChildren)
				DrawChildrenRecursive(root);
			else
				EditorGUILayout.LabelField("(no serializable fields)");

			if (_wrapperObject.hasModifiedProperties)
			{
				// hasModifiedProperties (unlike per-leaf EditorGUI.BeginChangeCheck) ignores foldout
				// expand/collapse, but it's an object-wide flag: diff the merged JSON before and after
				// applying to find exactly which leaf(s) actually changed value.
				JObject before = _instance != null ? JObject.FromObject(_instance, SettingsJson.Serializer) : new JObject();

				_wrapperObject.ApplyModifiedPropertiesWithoutUndo();
				_instance = _wrapper.settings;

				JObject after = _instance != null ? JObject.FromObject(_instance, SettingsJson.Serializer) : new JObject();
				var changedPaths = new List<string>();
				CollectChangedLeaves(before, after, string.Empty, changedPaths);
				foreach (string path in changedPaths)
					MarkDirty(path);

				CaptureEdits();
			}

			DrawLegend();

			EditorGUILayout.Space(4);
			DrawSaveButtons();
		}

		private void DrawChildrenRecursive(SerializedProperty parent)
		{
			SerializedProperty iterator = parent.Copy();
			SerializedProperty end = parent.GetEndProperty();

			if (!iterator.NextVisible(true))
				return;

			while (!SerializedProperty.EqualContents(iterator, end))
			{
				string jsonPath = ToJsonPath(iterator.propertyPath);
				bool isLeaf = iterator.propertyType != SerializedPropertyType.Generic || iterator.isArray || !iterator.hasVisibleChildren;
				Color previousBackground = GUI.backgroundColor;

				if (isLeaf)
					DrawLeaf(iterator, jsonPath);
				else
					DrawContainer(iterator, jsonPath);

				GUI.backgroundColor = previousBackground;

				if (!iterator.NextVisible(false))
					break;
			}
		}

		private void DrawLeaf(SerializedProperty property, string jsonPath)
		{
			SettingsTier? tier = _cascade.TierFor(jsonPath);
			bool isDirty = IsDirty(jsonPath);

			if (TryGetTint(isDirty, tier, out Color tint))
				GUI.backgroundColor = tint;

			using (new EditorGUI.DisabledScope(tier == SettingsTier.Cli))
			{
				// No EditorGUI.BeginChangeCheck/EndChangeCheck here: expanding a foldout on an array
				// field sets GUI.changed even though no value changed. Dirtiness is instead decided in
				// DrawTypedEditor by diffing the serialized instance before and after the whole draw.
				EditorGUILayout.PropertyField(property, true);
			}
		}

		private void DrawContainer(SerializedProperty property, string jsonPath)
		{
			Color ambientBackground = GUI.backgroundColor;

			if (TryGetContainerTint(jsonPath, out Color tint))
				GUI.backgroundColor = tint;

			property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, property.displayName, true);
			GUI.backgroundColor = ambientBackground;

			if (!property.isExpanded)
				return;

			EditorGUI.indentLevel++;
			DrawChildrenRecursive(property);
			EditorGUI.indentLevel--;
		}

		private void DrawLegend()
		{
			if (_dirtyPaths.Count == 0 && _activeTiers.Count == 0)
				return;

			EditorGUILayout.Space(2);
			Color previousBackground = GUI.backgroundColor;

			if (_dirtyPaths.Count > 0)
			{
				GUI.backgroundColor = _dirtyTint;
				EditorGUILayout.HelpBox("Yellow = unsaved change", MessageType.None);
			}

			if (_activeTiers.Contains(SettingsTier.Local))
			{
				GUI.backgroundColor = _localTint;
				EditorGUILayout.HelpBox("Blue = value comes from the Local tier", MessageType.None);
			}

			if (_activeTiers.Contains(SettingsTier.Machine))
			{
				GUI.backgroundColor = _machineTint;
				EditorGUILayout.HelpBox("Green = value comes from the Machine tier", MessageType.None);
			}

			if (_activeTiers.Contains(SettingsTier.Cli))
			{
				GUI.backgroundColor = _cliTint;
				EditorGUILayout.HelpBox("Gray = a --set command-line override wins for this value, so it is read-only here", MessageType.None);
			}

			GUI.backgroundColor = previousBackground;
		}

		private void DrawSaveButtons()
		{
			using (new EditorGUI.DisabledScope(_dirtyPaths.Count == 0))
			using (new EditorGUILayout.HorizontalScope())
			{
				foreach (SettingsTier tier in SettingsCascade.FileTiers)
				{
					if (GUILayout.Button($"Save to {tier}"))
						_pendingSaveTier = tier;
				}
			}

			if (GUILayout.Button("Revert"))
				_pendingReload = true;
		}

		private void DrawUntypedFallback()
		{
			EditorGUILayout.HelpBox(
				$"No class is tagged [SettingsClass(\"{_baseName}\")]. Tag your settings class with that attribute " +
				"to get a typed editor with per-field tier highlighting and sparse saving. Until then this pane is read-only.",
				MessageType.Info);

			EditorGUILayout.LabelField("Provenance", EditorStyles.miniBoldLabel);

			if (_provenance.Count == 0)
			{
				EditorGUILayout.LabelField("(no settings)");
				return;
			}

			foreach (var entry in _provenance)
			{
				string tierLabel = entry.Tier.HasValue ? entry.Tier.Value.ToString() : "none";
				EditorGUILayout.LabelField(entry.Path, $"{entry.Value} [{tierLabel}]");
			}
		}

		private void DrawFooter()
		{
			foreach (string warning in _cascade.Warnings)
				EditorGUILayout.HelpBox(warning, MessageType.Warning);

			_showMergedJson = EditorGUILayout.Foldout(_showMergedJson, "Merged JSON (read-only)", true);
			if (_showMergedJson)
			{
				using (new EditorGUI.DisabledScope(true))
					EditorGUILayout.TextArea(_mergedJsonText);
			}

			_showTierFiles = EditorGUILayout.Foldout(_showTierFiles, "Tier Files", true);
			if (_showTierFiles)
				DrawTierFiles();
		}

		private void DrawTierFiles()
		{
			foreach (SettingsTier tier in SettingsCascade.FileTiers)
			{
				string path = _cascade.PathFor(tier);
				bool exists = File.Exists(path);

				using (new EditorGUILayout.HorizontalScope())
				{
					EditorGUILayout.LabelField(tier.ToString(), exists ? path : $"{path} (missing)");

					using (new EditorGUI.DisabledScope(!exists))
					{
						if (GUILayout.Button("Reveal", GUILayout.Width(60)))
							EditorUtility.RevealInFinder(path);

						if (GUILayout.Button("Delete", GUILayout.Width(60)))
							_pendingDeleteTier = tier;
					}
				}
			}

			EditorGUILayout.LabelField(SettingsTier.Cli.ToString(), _cliTierLabel);
		}

		/// <summary>Rebuilds the cascade, the type candidates and the drawn instance, optionally keeping in-progress edits.</summary>
		private void Reload(bool keepEdits)
		{
			// Not AppEnvironment.Current: it is memoized per domain load, so --set arguments edited in the
			// Simulated Args window would not show up here until the next recompile.
			_cascade = new SettingsCascade(AppEnvironment.Build(), _baseName);
			_writer = new SettingsTierWriter(_cascade);
			_mergedJsonText = _cascade.Merged.ToString(Formatting.Indented);

			RebuildProvenance();
			ResolveSettingsType();

			if (!keepEdits)
			{
				_dirtyPaths.Clear();
				_editedJson = null;
			}

			Hydrate(keepEdits ? _editedJson : null);
			SeedDirtyPaths();
		}

		private void RebuildProvenance()
		{
			_provenance.Clear();
			_activeTiers.Clear();
			CollectProvenance(_cascade.Merged, string.Empty);
		}

		private void CollectProvenance(JObject obj, string prefix)
		{
			foreach (JProperty property in obj.Properties())
			{
				string path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";

				if (property.Value is JObject child && child.HasValues)
				{
					CollectProvenance(child, path);
					continue;
				}

				SettingsTier? tier = _cascade.TierFor(path);
				_provenance.Add((path, property.Value, tier));

				if (tier.HasValue)
					_activeTiers.Add(tier.Value);
			}
		}

		/// <summary>Finds every class tagged for the current base name and picks the one to draw.</summary>
		private void ResolveSettingsType()
		{
			var matches = new List<Type>();

			foreach (Type type in TypeCache.GetTypesWithAttribute<SettingsClassAttribute>())
			{
				var attribute = (SettingsClassAttribute)Attribute.GetCustomAttribute(type, typeof(SettingsClassAttribute), false);
				if (attribute != null && string.Equals(attribute.BaseName, _baseName, StringComparison.Ordinal))
					matches.Add(type);
			}

			matches.Sort((left, right) => string.CompareOrdinal(left.FullName, right.FullName));
			_candidateTypes = matches.ToArray();

			if (_candidateTypes.Length == 0)
			{
				_settingsType = null;
				return;
			}

			if (_candidateTypes.Length == 1)
			{
				_settingsType = _candidateTypes[0];
				return;
			}

			string stored = EditorPrefs.GetString(SettingsTypeEditorPrefsKey, string.Empty);
			_settingsType = Array.Find(_candidateTypes, type => type.AssemblyQualifiedName == stored) ?? _candidateTypes[0];
		}

		/// <summary>
		/// Rebuilds the drawn instance from <paramref name="json"/> when in-progress edits are being restored,
		/// otherwise from the merged cascade, and rebinds the <see cref="SerializedObject"/> to it.
		/// </summary>
		private void Hydrate(string json)
		{
			_editorWarning = null;

			_wrapperObject?.Dispose();
			_wrapperObject = null;
			_instance = null;

			if (_settingsType == null)
				return;

			try
			{
				_instance = string.IsNullOrEmpty(json)
					? _cascade.Merged.ToObject(_settingsType, SettingsJson.Serializer)
					: JsonConvert.DeserializeObject(json, _settingsType, SettingsJson.Settings);
			}
			catch (Exception ex)
			{
				_editorWarning = $"Failed to build {_settingsType.Name} from the merged settings, showing defaults instead: {ex.Message}";
			}

			_instance ??= TryCreateDefault();

			if (_wrapper == null)
			{
				_wrapper = ScriptableObject.CreateInstance<SettingsWrapper>();
				_wrapper.hideFlags = HideFlags.DontSave;
			}

			_wrapper.settings = _instance;
			_wrapperObject = new SerializedObject(_wrapper);
		}

		/// <summary>Serializes the edited instance so it can be restored after a domain reload.</summary>
		private void CaptureEdits()
		{
			try
			{
				_editedJson = JsonConvert.SerializeObject(_instance, SettingsJson.Settings);
			}
			catch (Exception ex)
			{
				_editedJson = null;
				_editorWarning = $"Could not snapshot in-progress edits: {ex.Message}";
			}
		}

		/// <summary>
		/// Marks every default leaf path that no file tier persists as dirty, so fields added since the
		/// files were written show up as needing a save. Paths a <c>--set</c> argument owns are skipped:
		/// they are read-only here and must never be baked into a file.
		/// </summary>
		private void SeedDirtyPaths()
		{
			if (_settingsType == null)
				return;

			object defaults = TryCreateDefault();
			if (defaults == null)
				return;

			var defaultPaths = new HashSet<string>();
			try
			{
				FlattenPaths(JObject.FromObject(defaults, SettingsJson.Serializer), string.Empty, defaultPaths);
			}
			catch (Exception ex)
			{
				_editorWarning = $"Could not inspect {_settingsType.Name} defaults: {ex.Message}";
				return;
			}

			var persistedPaths = new HashSet<string>();
			foreach (SettingsTier tier in SettingsCascade.FileTiers)
			{
				string path = _cascade.PathFor(tier);
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
					continue;

				try
				{
					FlattenPaths(JObject.Parse(File.ReadAllText(path)), string.Empty, persistedPaths);
				}
				catch
				{
					// Malformed tier files are already reported through SettingsCascade.Warnings.
				}
			}

			foreach (string path in defaultPaths)
			{
				if (persistedPaths.Contains(path) || _cascade.TierFor(path) == SettingsTier.Cli)
					continue;

				MarkDirty(path);
			}
		}

		private object TryCreateDefault()
		{
			try
			{
				return Activator.CreateInstance(_settingsType);
			}
			catch (Exception ex)
			{
				_editorWarning = $"{_settingsType.Name} has no parameterless constructor: {ex.Message}";
				return null;
			}
		}

		private void Save(SettingsTier tier)
		{
			try
			{
				var fullValue = (JObject)JToken.FromObject(_instance, SettingsJson.Serializer);
				_writer.SaveDirtyPaths(tier, fullValue, WritablePaths());
			}
			catch (Exception ex)
			{
				_editorWarning = $"Could not save {_settingsType.Name} to the {tier} tier: {ex.Message}";
				return;
			}

			Reload(keepEdits: false);
		}

		/// <summary>
		/// The dirty paths that may be written to a file: a <c>--set</c> argument's value is not the user's
		/// edit and persisting it would silently make the override permanent. Seeding already skips these,
		/// but a dirty entry can outlive the reload that made its path CLI-owned.
		/// </summary>
		private List<string> WritablePaths()
		{
			var paths = new List<string>(_dirtyPaths.Count);

			foreach (string path in _dirtyPaths)
			{
				if (_cascade.TierFor(path) != SettingsTier.Cli)
					paths.Add(path);
			}

			return paths;
		}

		private void DeleteTier(SettingsTier tier)
		{
			string path = _cascade.PathFor(tier);

			if (!EditorUtility.DisplayDialog("Delete settings file", $"Delete '{path}'?", "Delete", "Cancel"))
				return;

			try
			{
				_writer.DeleteTier(tier);
			}
			catch (Exception ex)
			{
				_editorWarning = $"Could not delete the {tier} settings file at '{path}': {ex.Message}";
				return;
			}

			Reload(keepEdits: false);
		}

		/// <summary>Asks before throwing away in-progress edits; always true when there are none.</summary>
		private bool ConfirmDiscardingEdits(string reason)
		{
			if (_dirtyPaths.Count == 0)
				return true;

			return EditorUtility.DisplayDialog("Discard unsaved settings edits?", $"{reason} Discard them?", "Discard", "Cancel");
		}

		private string SettingsTypeEditorPrefsKey => _settingsTypeEditorPrefsKeyPrefix + _baseName;

		private bool IsDirty(string jsonPath) => _dirtyPaths.Contains(jsonPath);

		private void MarkDirty(string jsonPath)
		{
			if (!_dirtyPaths.Contains(jsonPath))
				_dirtyPaths.Add(jsonPath);
		}

		private bool TryGetTint(bool isDirty, SettingsTier? tier, out Color tint)
		{
			if (isDirty)
			{
				tint = _dirtyTint;
				return true;
			}

			switch (tier)
			{
				case SettingsTier.Cli:
					tint = _cliTint;
					return true;
				case SettingsTier.Local:
					tint = _localTint;
					return true;
				case SettingsTier.Machine:
					tint = _machineTint;
					return true;
				default:
					tint = Color.white;
					return false;
			}
		}

		/// <summary>Tints a foldout with the highest-priority tint any leaf below it qualifies for.</summary>
		private bool TryGetContainerTint(string jsonPath, out Color tint)
		{
			string prefix = jsonPath + ".";

			foreach (string dirtyPath in _dirtyPaths)
			{
				if (dirtyPath == jsonPath || dirtyPath.StartsWith(prefix, StringComparison.Ordinal))
				{
					tint = _dirtyTint;
					return true;
				}
			}

			if (HasDescendantTier(jsonPath, prefix, SettingsTier.Cli))
			{
				tint = _cliTint;
				return true;
			}

			if (HasDescendantTier(jsonPath, prefix, SettingsTier.Local))
			{
				tint = _localTint;
				return true;
			}

			if (HasDescendantTier(jsonPath, prefix, SettingsTier.Machine))
			{
				tint = _machineTint;
				return true;
			}

			tint = Color.white;
			return false;
		}

		private bool HasDescendantTier(string jsonPath, string prefix, SettingsTier tier)
		{
			foreach (var entry in _provenance)
			{
				if (entry.Tier == tier && (entry.Path == jsonPath || entry.Path.StartsWith(prefix, StringComparison.Ordinal)))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Converts a <see cref="SerializedProperty.propertyPath"/> like <c>"settings.general.debugMode"</c>
		/// into the dotted JSON path <c>"general.debugMode"</c> by stripping the wrapper field prefix.
		/// </summary>
		private static string ToJsonPath(string propertyPath)
		{
			const string prefix = _wrapperFieldName + ".";
			return propertyPath.StartsWith(prefix, StringComparison.Ordinal) ? propertyPath.Substring(prefix.Length) : propertyPath;
		}

		/// <summary>Collects every dotted leaf path in <paramref name="obj"/>; arrays count as leaves.</summary>
		private static void FlattenPaths(JObject obj, string prefix, HashSet<string> paths)
		{
			foreach (JProperty property in obj.Properties())
			{
				string path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";

				if (property.Value is JObject child && child.HasValues)
					FlattenPaths(child, path, paths);
				else
					paths.Add(path);
			}
		}

		/// <summary>
		/// Walks <paramref name="after"/> and adds the dotted path of every leaf (arrays count as leaves,
		/// same as <see cref="FlattenPaths"/>) whose value differs from the corresponding leaf in
		/// <paramref name="before"/> to <paramref name="changed"/>. A leaf missing from <paramref name="before"/>
		/// counts as changed. Internal (rather than private) so it's testable without a GUI: it's pure JSON
		/// diffing, unrelated to how <see cref="DrawTypedEditor"/> gathers the before/after snapshots.
		/// </summary>
		internal static void CollectChangedLeaves(JObject before, JObject after, string prefix, List<string> changed)
		{
			foreach (JProperty property in after.Properties())
			{
				string path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
				JToken beforeValue = before?[property.Name];

				if (property.Value is JObject child && child.HasValues)
				{
					CollectChangedLeaves(beforeValue as JObject ?? new JObject(), child, path, changed);
					continue;
				}

				if (!JToken.DeepEquals(beforeValue, property.Value))
					changed.Add(path);
			}
		}
	}
}
