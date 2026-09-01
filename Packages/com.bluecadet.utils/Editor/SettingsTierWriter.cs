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
	/// Writes sparse, per-path edits into a <see cref="SettingsFile{T}"/> tier's backing file,
	/// stripping stale shadows from other tiers and pruning files down to nothing when they end up
	/// empty. JSON tiers are rewritten wholesale from a <see cref="JObject"/>; YAML tiers are patched
	/// as text so their comments survive. Ported from the pre-rework SettingsManagerEditor
	/// tier-write semantics.
	/// </summary>
	internal sealed class SettingsTierWriter
	{
		private static readonly JsonMergeSettings _mergeSettings = new JsonMergeSettings
		{
			MergeArrayHandling = MergeArrayHandling.Replace
		};

		private readonly SettingsCascade _cascade;

		internal SettingsTierWriter(SettingsCascade cascade)
		{
			_cascade = cascade;
		}

		/// <summary>
		/// Writes only <paramref name="dirtyPaths"/> (dotted paths into <paramref name="fullValue"/>) into
		/// <paramref name="target"/>'s backing file, leaving every other key in that file untouched.
		/// When <paramref name="target"/> is <see cref="SettingsTier.Base"/> or <see cref="SettingsTier.Machine"/>,
		/// the same paths are also stripped out of the <see cref="SettingsTier.Local"/> file so it can't shadow
		/// the new value; that strip is attempted in memory before anything reaches disk, so an unwritable
		/// Local file fails the whole save and leaves every file untouched. When <paramref name="target"/> is
		/// <see cref="SettingsTier.Local"/>, a leaf is only written if it differs from the effective
		/// Base+Machine value; otherwise any existing Local override at that path is removed, so Local never
		/// carries a redundant override.
		/// Dirty paths that <paramref name="fullValue"/> does not define at all are skipped and reported,
		/// never written as JSON null.
		/// </summary>
		/// <exception cref="InvalidOperationException">
		/// A tier file could not be edited (malformed, or YAML the patcher cannot rewrite). The message names
		/// that file's path on disk and says nothing was written; the underlying failure is the inner exception.
		/// </exception>
		public void SaveDirtyPaths(SettingsTier target, JObject fullValue, IEnumerable<string> dirtyPaths)
		{
			if (target == SettingsTier.Cli)
				throw new ArgumentException("Cannot write to the Cli tier; it has no backing file.", nameof(target));

			SettingsPath[] paths = FilterToSerializedPaths(fullValue, dirtyPaths ?? Array.Empty<string>());
			if (paths.Length == 0)
				return;

			var touchedPaths = new List<string>();

			string localPath = _cascade.PathFor(SettingsTier.Local);

			if (target == SettingsTier.Local)
			{
				JObject effective = LoadEffectiveBaseAndMachine();
				ITierFileEditor local;

				try
				{
					local = OpenTier(localPath);

					foreach (SettingsPath path in paths)
					{
						JToken leaf = path.Resolve(fullValue);
						JToken effectiveValue = path.Resolve(effective);

						if (LeafEquals(leaf, effectiveValue))
							local.Remove(path);
						else
							local.Set(path, leaf);
					}
				}
				catch (Exception ex)
				{
					throw CouldNotEdit(SettingsTier.Local, localPath, null, ex);
				}

				local.Save(touchedPaths);
			}
			else
			{
				string targetPath = _cascade.PathFor(target);
				ITierFileEditor targetEditor;

				try
				{
					targetEditor = OpenTier(targetPath);

					foreach (SettingsPath path in paths)
						targetEditor.Set(path, path.Resolve(fullValue));
				}
				catch (Exception ex)
				{
					throw CouldNotEdit(target, targetPath, null, ex);
				}

				// Strip the Local shadows in memory before anything reaches disk. A Local file the editor
				// cannot rewrite (malformed YAML, or flow-style/anchored YAML) throws here, and a target write
				// that had already landed would leave the value saved but still shadowed. Both editors only
				// touch disk in Save, so failing here leaves every file exactly as it was.
				ITierFileEditor local;
				bool localChanged = false;

				try
				{
					local = OpenTier(localPath);

					foreach (SettingsPath path in paths)
					{
						if (local.Remove(path))
							localChanged = true;
					}
				}
				catch (Exception ex)
				{
					throw CouldNotEdit(
						SettingsTier.Local,
						localPath,
						$"while dropping the override that would shadow the new {target} value",
						ex);
				}

				targetEditor.Save(touchedPaths);

				if (localChanged)
					local.Save(touchedPaths);
			}

			RefreshAssetsIfNeeded(touchedPaths);
		}

		/// <summary>Deletes <paramref name="tier"/>'s backing file and its <c>.meta</c>, if present.</summary>
		public void DeleteTier(SettingsTier tier)
		{
			if (tier == SettingsTier.Cli)
				throw new ArgumentException("Cannot delete the Cli tier; it has no backing file.", nameof(tier));

			var touchedPaths = new List<string>();
			DeleteFile(_cascade.PathFor(tier), touchedPaths);
			RefreshAssetsIfNeeded(touchedPaths);
		}

		/// <summary>
		/// Drops the dirty paths <paramref name="fullValue"/> has no value for at all and warns about them
		/// once. A private <c>[SerializeField]</c> field, for instance, is drawn by Unity but skipped by
		/// Json.NET, so writing it out would put a junk null into the settings file. An explicit JSON null
		/// is a real value and is kept.
		/// </summary>
		private static SettingsPath[] FilterToSerializedPaths(JObject fullValue, IEnumerable<string> dottedPaths)
		{
			var writable = new List<SettingsPath>();
			var skipped = new List<string>();

			foreach (string dottedPath in dottedPaths)
			{
				var path = new SettingsPath(dottedPath);

				if (path.Resolve(fullValue) is null)
					skipped.Add(dottedPath);
				else
					writable.Add(path);
			}

			if (skipped.Count > 0)
			{
				Debug.LogWarning(
					"[SettingsTierWriter] Skipped settings path(s) with no serialized value (a field Unity draws but " +
					$"Json.NET does not serialize, e.g. a private field without [JsonProperty]): {string.Join(", ", skipped)}");
			}

			return writable.ToArray();
		}

		/// <summary>
		/// Compares two leaves through their JSON text, so that values that only differ in how they were
		/// produced compare equal: a boxed <c>float</c> of 0.1f widens to the double 0.10000000149011612,
		/// which <see cref="JToken.DeepEquals"/> would not match against the 0.1 parsed from a tier file.
		/// </summary>
		private static bool LeafEquals(JToken left, JToken right)
		{
			if (left is null || right is null)
				return left is null && right is null;

			return JToken.DeepEquals(RoundTrip(left), RoundTrip(right));
		}

		/// <summary>Re-parses a token from its own JSON text, normalizing how its numbers are typed.</summary>
		private static JToken RoundTrip(JToken token)
		{
			try
			{
				return JToken.Parse(token.ToString(Formatting.None));
			}
			catch
			{
				return token;
			}
		}

		/// <summary>Merges the Base and Machine tier files (Machine winning), the same way <see cref="SettingsCascade"/> does.</summary>
		private JObject LoadEffectiveBaseAndMachine()
		{
			var effective = new JObject();
			effective.Merge(SettingsFormatIO.Parse(_cascade.PathFor(SettingsTier.Base)), _mergeSettings);
			effective.Merge(SettingsFormatIO.Parse(_cascade.PathFor(SettingsTier.Machine)), _mergeSettings);
			return effective;
		}

		/// <summary>
		/// Per-format editing of one tier file: the same sparse set/remove operations, saved however
		/// the format demands. Nothing touches disk before <see cref="Save"/>, so a failed edit leaves the
		/// file as it was. When that failure surfaces differs by format: a JSON tier is parsed on open, so
		/// malformed content throws in the constructor, while a YAML tier is read as raw text and only parsed
		/// inside <see cref="Set"/> and <see cref="Remove"/>, so malformed or unrewritable content throws
		/// there instead.
		/// </summary>
		private interface ITierFileEditor
		{
			void Set(SettingsPath path, JToken value);
			bool Remove(SettingsPath path);
			void Save(List<string> touchedPaths);
		}

		/// <summary>
		/// Rewrites an in-memory tier-edit failure so the surfaced message names the file that could not be
		/// edited. The underlying messages name only the dotted settings path, which sends a user saving Base
		/// off to debug the wrong file when it was their Local file that could not be stripped.
		/// <paramref name="why"/> spells out what the edit was for, so a failure in the target tier itself is
		/// not mislabelled as a Local problem.
		/// </summary>
		private static InvalidOperationException CouldNotEdit(SettingsTier tier, string path, string why, Exception inner)
		{
			string reason = string.IsNullOrEmpty(why) ? string.Empty : " " + why;

			return new InvalidOperationException(
				$"The {tier} settings file at '{path}' could not be edited{reason}. " +
				$"Nothing was written and every settings file is unchanged. {inner.Message}",
				inner);
		}

		private static ITierFileEditor OpenTier(string path) =>
			SettingsFormatIO.FormatFor(path) == SettingsFormat.Yaml
				? (ITierFileEditor)new YamlTierEditor(path)
				: new JsonTierEditor(path);

		/// <summary>Edits a JSON tier as a <see cref="JObject"/> and rewrites the whole file pretty-printed.</summary>
		private sealed class JsonTierEditor : ITierFileEditor
		{
			private readonly string _path;
			private readonly JObject _value;

			public JsonTierEditor(string path)
			{
				_path = path;
				_value = SettingsFormatIO.Parse(path);
			}

			public void Set(SettingsPath path, JToken value) => path.Set(_value, value);

			public bool Remove(SettingsPath path) => path.Remove(_value);

			public void Save(List<string> touchedPaths)
			{
				if (_value.HasValues)
					WriteTierFile(_path, _value.ToString(Formatting.Indented), touchedPaths);
				else
					DeleteFile(_path, touchedPaths);
			}
		}

		/// <summary>
		/// Edits a YAML tier as raw text through <see cref="YamlTierPatcher"/>, so comments outside the
		/// edited spans survive. A file left with nothing but whitespace is deleted; one holding only
		/// comments is kept (it parses as an empty tier).
		/// </summary>
		private sealed class YamlTierEditor : ITierFileEditor
		{
			private readonly string _path;
			private string _text;

			public YamlTierEditor(string path)
			{
				_path = path;
				_text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
			}

			public void Set(SettingsPath path, JToken value) => _text = YamlTierPatcher.SetPath(_text, path, value);

			public bool Remove(SettingsPath path)
			{
				bool removed = YamlTierPatcher.TryRemovePath(_text, path, out string result);
				_text = result;
				return removed;
			}

			public void Save(List<string> touchedPaths)
			{
				if (YamlTierPatcher.IsWhitespaceOnly(_text))
					DeleteFile(_path, touchedPaths);
				else
					WriteTierFile(_path, _text, touchedPaths);
			}
		}

		private static void WriteTierFile(string path, string contents, List<string> touchedPaths)
		{
			string directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(path, contents);
			touchedPaths.Add(path);
		}

		/// <summary>Deletes <paramref name="path"/> and its <c>.meta</c> sibling, if present.</summary>
		private static void DeleteFile(string path, List<string> touchedPaths)
		{
			if (string.IsNullOrEmpty(path))
				return;

			if (File.Exists(path))
			{
				File.Delete(path);
				touchedPaths.Add(path);
			}

			string metaPath = path + ".meta";
			if (File.Exists(metaPath))
			{
				File.Delete(metaPath);
				touchedPaths.Add(metaPath);
			}
		}

		/// <summary>
		/// Settings files often live under <c>Assets/StreamingAssets</c>, where the asset database
		/// keeps its own view of the directory and needs to be told the files changed.
		/// </summary>
		private static void RefreshAssetsIfNeeded(List<string> touchedPaths)
		{
			if (touchedPaths.Count == 0)
				return;

			string assetsRoot = Path.GetFullPath(Application.dataPath);
			string streamingAssetsRoot = Path.GetFullPath(Application.streamingAssetsPath);

			bool insideProject = touchedPaths.Any(path =>
			{
				string full = Path.GetFullPath(path);
				return full.StartsWith(assetsRoot, StringComparison.Ordinal) || full.StartsWith(streamingAssetsRoot, StringComparison.Ordinal);
			});

			if (insideProject)
				AssetDatabase.Refresh();
		}
	}
}
