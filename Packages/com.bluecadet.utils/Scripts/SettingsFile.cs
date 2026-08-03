using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Bluecadet.Utils
{
	/// <summary>
	/// Identifies a layer in the <see cref="SettingsFile{T}"/> cascade, from lowest to highest precedence.
	/// </summary>
	public enum SettingsTier
	{
		/// <summary>The shared base settings file (<c>&lt;name&gt;.json</c>).</summary>
		Base,

		/// <summary>The per-machine override file (<c>&lt;name&gt;.&lt;machineId&gt;.json</c>).</summary>
		Machine,

		/// <summary>The local override file (<c>&lt;name&gt;.local.json</c>), typically machine-specific and git-ignored.</summary>
		Local,

		/// <summary>Overrides supplied via repeated <c>--set key.path=value</c> command-line arguments.</summary>
		Cli
	}

	/// <summary>
	/// Loads and merges a cascade of JSON settings files, plus CLI <c>--set</c> overrides,
	/// into a plain object of type <typeparamref name="T"/>. Construct via <see cref="AppEnvironment.SettingsFile{T}"/>.
	/// </summary>
	public sealed class SettingsFile<T> where T : class, new()
	{
		/// <summary>
		/// Reports the effective value at a dotted path within a <see cref="SettingsFile{T}"/>,
		/// and which tier produced it.
		/// </summary>
		public readonly struct Explanation
		{
			/// <summary>The effective value at the requested path, or null if no tier sets it.</summary>
			public JToken Value { get; }

			/// <summary>The tier that produced <see cref="Value"/>, or null if no tier sets it.</summary>
			public SettingsTier? Tier { get; }

			internal Explanation(JToken value, SettingsTier? tier)
			{
				Value = value;
				Tier = tier;
			}
		}

		private static readonly JsonMergeSettings MergeSettings = new JsonMergeSettings
		{
			MergeArrayHandling = MergeArrayHandling.Replace
		};

		private static readonly SettingsTier[] FileTiers = { SettingsTier.Base, SettingsTier.Machine, SettingsTier.Local };

		private readonly AppEnvironment _environment;
		private readonly string _baseName;
		private readonly Dictionary<SettingsTier, JObject> _tierObjects = new();
		private readonly List<string> _loadedPaths = new();

		private T _value;

		/// <summary>Raised after <see cref="Reload"/> completes, with the newly loaded value.</summary>
		public event Action<T> OnReloaded;

		internal SettingsFile(AppEnvironment environment, string baseName)
		{
			_environment = environment;
			_baseName = baseName;
			Load();
		}

		/// <summary>
		/// The merged, memoized settings value. Never null: a missing tier is silently
		/// skipped, a malformed tier logs a warning and is skipped, and if every tier is
		/// unusable this is <c>new T()</c>.
		/// </summary>
		public T Value => _value;

		/// <summary>
		/// The file tiers (<see cref="SettingsTier.Base"/>, <see cref="SettingsTier.Machine"/>,
		/// <see cref="SettingsTier.Local"/>) that actually existed and loaded successfully, in cascade order.
		/// </summary>
		public IReadOnlyList<string> LoadedPaths => _loadedPaths;

		/// <summary>
		/// Returns the on-disk path for a file tier. There is no file for <see cref="SettingsTier.Cli"/>;
		/// a descriptive pseudo-path is returned instead.
		/// </summary>
		public string PathFor(SettingsTier tier)
		{
			if (tier == SettingsTier.Cli)
				return "(--set command-line arguments)";

			return GetFilePath(tier);
		}

		/// <summary>Re-reads and re-merges every tier, then raises <see cref="OnReloaded"/>.</summary>
		public void Reload()
		{
			Load();
			OnReloaded?.Invoke(_value);
		}

		/// <summary>
		/// Returns the effective value at <paramref name="dottedPath"/> (e.g. <c>"general.debugMode"</c>)
		/// and the tier that produced it, checking tiers from highest to lowest precedence.
		/// </summary>
		public Explanation Explain(string dottedPath)
		{
			string[] parts = (dottedPath ?? string.Empty).Split('.');

			for (int i = FileTiers.Length; i >= 0; i--)
			{
				SettingsTier tier = i == FileTiers.Length ? SettingsTier.Cli : FileTiers[i];
				if (_tierObjects.TryGetValue(tier, out JObject tierObject) && TryGetNested(tierObject, parts, out JToken value))
					return new Explanation(value, tier);
			}

			return new Explanation(null, null);
		}

		private void Load()
		{
			_tierObjects.Clear();
			_loadedPaths.Clear();

			var merged = new JObject();

			foreach (var tier in FileTiers)
			{
				string path = GetFilePath(tier);
				JObject tierObject = LoadTierFile(path, tier);
				if (tierObject == null)
					continue;

				_tierObjects[tier] = tierObject;
				_loadedPaths.Add(path);
				merged.Merge(tierObject, MergeSettings);
			}

			JObject cliObject = BuildCliTier();
			if (cliObject.Count > 0)
			{
				_tierObjects[SettingsTier.Cli] = cliObject;
				merged.Merge(cliObject, MergeSettings);
			}

			try
			{
				_value = merged.ToObject<T>() ?? new T();
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[SettingsFile<{typeof(T).Name}>] Failed to build {typeof(T).Name} from merged settings: {ex.Message}");
				_value = new T();
			}
		}

		private string GetFilePath(SettingsTier tier)
		{
			switch (tier)
			{
				case SettingsTier.Base:
					return _environment.ResolvePath($"{_baseName}.json");
				case SettingsTier.Machine:
					return _environment.ResolvePath($"{_baseName}.{_environment.MachineId}.json");
				case SettingsTier.Local:
					return _environment.ResolvePath($"{_baseName}.local.json");
				default:
					return null;
			}
		}

		private static JObject LoadTierFile(string path, SettingsTier tier)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return null;

			try
			{
				return JObject.Parse(File.ReadAllText(path));
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[SettingsFile<{typeof(T).Name}>] Malformed {tier} settings file at '{path}': {ex.Message}");
				return null;
			}
		}

		private JObject BuildCliTier()
		{
			var result = new JObject();

			foreach (var occurrence in _environment.Args.Occurrences)
			{
				if (!string.Equals(occurrence.Key, "set", StringComparison.OrdinalIgnoreCase))
					continue;

				int equalsIndex = occurrence.Value.IndexOf('=');
				if (equalsIndex < 0)
					continue;

				string path = occurrence.Value.Substring(0, equalsIndex);
				string rawValue = occurrence.Value.Substring(equalsIndex + 1);

				SetNestedValue(result, path.Split('.'), ParseCliValue(rawValue));
			}

			return result;
		}

		private static JToken ParseCliValue(string rawValue)
		{
			try
			{
				return JToken.Parse(rawValue);
			}
			catch
			{
				return new JValue(rawValue);
			}
		}

		private static void SetNestedValue(JObject target, string[] parts, JToken value)
		{
			JObject current = target;
			for (int i = 0; i < parts.Length - 1; i++)
			{
				string key = parts[i];
				if (!(current[key] is JObject child))
				{
					child = new JObject();
					current[key] = child;
				}
				current = child;
			}

			current[parts[^1]] = value;
		}

		private static bool TryGetNested(JObject obj, string[] parts, out JToken value)
		{
			JToken current = obj;
			foreach (var part in parts)
			{
				if (current is JObject jObj && jObj.TryGetValue(part, out JToken next))
					current = next;
				else
				{
					value = null;
					return false;
				}
			}

			value = current;
			return true;
		}
	}
}
