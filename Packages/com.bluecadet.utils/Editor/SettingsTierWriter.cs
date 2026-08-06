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
	/// Writes sparse, per-path edits into a <see cref="SettingsFile{T}"/> tier's backing JSON file,
	/// stripping stale shadows from other tiers and pruning files down to nothing when they end up
	/// empty. Ported from the pre-rework SettingsManagerEditor tier-write semantics.
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
		/// the new value. When <paramref name="target"/> is <see cref="SettingsTier.Local"/>, a leaf is only
		/// written if it differs from the effective Base+Machine value; otherwise any existing Local override
		/// at that path is removed, so Local never carries a redundant override.
		/// Dirty paths that <paramref name="fullValue"/> does not define at all are skipped and reported,
		/// never written as JSON null.
		/// </summary>
		public void SaveDirtyPaths(SettingsTier target, JObject fullValue, IEnumerable<string> dirtyPaths)
		{
			if (target == SettingsTier.Cli)
				throw new ArgumentException("Cannot write to the Cli tier; it has no backing file.", nameof(target));

			SettingsPath[] paths = FilterToSerializedPaths(fullValue, dirtyPaths ?? Array.Empty<string>());
			if (paths.Length == 0)
				return;

			var touchedPaths = new List<string>();

			if (target == SettingsTier.Local)
			{
				JObject effective = LoadEffectiveBaseAndMachine();
				string localPath = _cascade.PathFor(SettingsTier.Local);
				JObject local = LoadTierFile(localPath);

				foreach (SettingsPath path in paths)
				{
					JToken leaf = path.Resolve(fullValue);
					JToken effectiveValue = path.Resolve(effective);

					if (LeafEquals(leaf, effectiveValue))
						path.Remove(local);
					else
						path.Set(local, leaf);
				}

				SaveOrDelete(localPath, local, touchedPaths);
			}
			else
			{
				string targetPath = _cascade.PathFor(target);
				JObject targetObject = LoadTierFile(targetPath);

				foreach (SettingsPath path in paths)
					path.Set(targetObject, path.Resolve(fullValue));

				SaveOrDelete(targetPath, targetObject, touchedPaths);

				string localPath = _cascade.PathFor(SettingsTier.Local);
				JObject local = LoadTierFile(localPath);
				bool localChanged = false;

				foreach (SettingsPath path in paths)
				{
					if (path.Remove(local))
						localChanged = true;
				}

				if (localChanged)
					SaveOrDelete(localPath, local, touchedPaths);
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
			effective.Merge(LoadTierFile(_cascade.PathFor(SettingsTier.Base)), _mergeSettings);
			effective.Merge(LoadTierFile(_cascade.PathFor(SettingsTier.Machine)), _mergeSettings);
			return effective;
		}

		/// <summary>Parses the JSON file at <paramref name="path"/>, or returns an empty object if it doesn't exist.</summary>
		private static JObject LoadTierFile(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return new JObject();

			return JObject.Parse(File.ReadAllText(path));
		}

		/// <summary>Writes <paramref name="value"/> pretty-printed to <paramref name="path"/>, or deletes the file if it's empty.</summary>
		private void SaveOrDelete(string path, JObject value, List<string> touchedPaths)
		{
			if (value.HasValues)
			{
				string directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				File.WriteAllText(path, value.ToString(Formatting.Indented));
				touchedPaths.Add(path);
			}
			else
			{
				DeleteFile(path, touchedPaths);
			}
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
