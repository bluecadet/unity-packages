using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Utils
{
	/// <summary>
	/// The untyped tier cascade behind <see cref="SettingsFile{T}"/>: reads each file tier, builds the
	/// CLI <c>--set</c> tier, merges them in precedence order, and reports which tier produced a given
	/// path. Shared with tooling (e.g. the editor settings pane) that needs the same cascade without
	/// depending on a concrete settings type.
	/// </summary>
	internal sealed class SettingsCascade
	{
		/// <summary>Every tier in cascade order, lowest to highest precedence, as declared by <see cref="SettingsTier"/>.</summary>
		internal static readonly SettingsTier[] AllTiers = (SettingsTier[])Enum.GetValues(typeof(SettingsTier));

		/// <summary>The file-backed tiers in cascade order: every tier except <see cref="SettingsTier.Cli"/>.</summary>
		internal static readonly SettingsTier[] FileTiers = Array.FindAll(AllTiers, tier => tier != SettingsTier.Cli);

		/// <summary>Merge settings shared by every tier: arrays replace rather than concatenate.</summary>
		private static readonly JsonMergeSettings _mergeSettings = new JsonMergeSettings
		{
			MergeArrayHandling = MergeArrayHandling.Replace
		};

		private readonly AppEnvironment _environment;
		private readonly string _baseName;
		private readonly Dictionary<SettingsTier, JObject> _tierObjects = new();
		private readonly List<string> _loadedPaths = new();
		private readonly List<string> _warnings = new();

		internal SettingsCascade(AppEnvironment environment, string baseName)
		{
			_environment = environment;
			_baseName = baseName;
			Load();
		}

		/// <summary>The merged result of every tier that loaded, lowest to highest precedence.</summary>
		internal JObject Merged { get; private set; }

		/// <summary>The file tiers that existed and parsed successfully, in cascade order.</summary>
		internal IReadOnlyList<string> LoadedPaths => _loadedPaths;

		/// <summary>One message per tier file that exists but could not be parsed.</summary>
		internal IReadOnlyList<string> Warnings => _warnings;

		/// <summary>Re-reads every tier file, rebuilds the CLI tier, and re-merges.</summary>
		internal void Load()
		{
			_tierObjects.Clear();
			_loadedPaths.Clear();
			_warnings.Clear();

			foreach (SettingsTier tier in FileTiers)
			{
				string path = PathFor(tier);

				if (TryLoadTierFile(path, out JObject tierObject, out Exception error))
				{
					_tierObjects[tier] = tierObject;
					_loadedPaths.Add(path);
				}
				else if (error != null)
				{
					_warnings.Add($"Malformed {tier} settings file at '{path}': {error.Message}");
				}
			}

			JObject cliObject = BuildCliTier(_environment.Args);
			if (cliObject.Count > 0)
				_tierObjects[SettingsTier.Cli] = cliObject;

			var merged = new JObject();
			foreach (SettingsTier tier in AllTiers)
			{
				if (_tierObjects.TryGetValue(tier, out JObject tierObject))
					merged.Merge(tierObject, _mergeSettings);
			}

			Merged = merged;
		}

		/// <summary>
		/// Returns the on-disk path for a file tier of <c>baseName</c> under the environment's data path,
		/// or null for <see cref="SettingsTier.Cli"/>, which comes from arguments and has no backing file.
		/// </summary>
		internal string PathFor(SettingsTier tier) => tier switch
		{
			SettingsTier.Base => _environment.ResolvePath($"{_baseName}.json"),
			SettingsTier.Machine => _environment.ResolvePath($"{_baseName}.{_environment.MachineId}.json"),
			SettingsTier.Local => _environment.ResolvePath($"{_baseName}.local.json"),
			_ => null
		};

		/// <summary>
		/// Returns the tier that produced the effective value at <paramref name="dottedPath"/>
		/// (e.g. <c>"general.debugMode"</c>), checking tiers from highest to lowest precedence,
		/// or null if no tier sets it.
		/// </summary>
		internal SettingsTier? TierFor(string dottedPath)
		{
			var path = new SettingsPath(dottedPath);

			for (int i = AllTiers.Length - 1; i >= 0; i--)
			{
				SettingsTier tier = AllTiers[i];
				if (_tierObjects.TryGetValue(tier, out JObject tierObject) && path.Resolve(tierObject) != null)
					return tier;
			}

			return null;
		}

		/// <summary>
		/// Attempts to parse the JSON file at <paramref name="path"/> into a <see cref="JObject"/>.
		/// Returns false if the file is missing (with <paramref name="error"/> left null) or malformed
		/// (with <paramref name="error"/> set to the parse exception).
		/// </summary>
		private static bool TryLoadTierFile(string path, out JObject result, out Exception error)
		{
			result = null;
			error = null;

			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return false;

			try
			{
				result = JObject.Parse(File.ReadAllText(path));
				return true;
			}
			catch (Exception ex)
			{
				error = ex;
				return false;
			}
		}

		/// <summary>
		/// Builds the CLI override tier as a nested <see cref="JObject"/> from every repeatable
		/// <c>--set key.path=value</c> occurrence in <paramref name="args"/>.
		/// </summary>
		private static JObject BuildCliTier(CommandLineArgs args)
		{
			var result = new JObject();

			foreach (var occurrence in args.Occurrences)
			{
				if (!string.Equals(occurrence.Key, "set", StringComparison.OrdinalIgnoreCase))
					continue;

				int equalsIndex = occurrence.Value.IndexOf('=');
				if (equalsIndex < 0)
					continue;

				string path = occurrence.Value.Substring(0, equalsIndex);
				string rawValue = occurrence.Value.Substring(equalsIndex + 1);

				new SettingsPath(path).Set(result, ParseCliValue(rawValue));
			}

			return result;
		}

		/// <summary>Parses a raw <c>--set</c> value as a JSON literal, falling back to a plain string.</summary>
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
	}
}
