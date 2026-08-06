using System;
using System.Collections.Generic;
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
		private readonly SettingsCascade _cascade;

		private T _value;

		/// <summary>Raised after <see cref="Reload"/> completes, with the newly loaded value.</summary>
		public event Action<T> OnReloaded;

		internal SettingsFile(AppEnvironment environment, string baseName)
		{
			_cascade = new SettingsCascade(environment, baseName);
			Build();
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
		public IReadOnlyList<string> LoadedPaths => _cascade.LoadedPaths;

		/// <summary>
		/// Returns the on-disk path for a file tier, or null for <see cref="SettingsTier.Cli"/>,
		/// which comes from command-line arguments and has no backing file.
		/// </summary>
		public string PathFor(SettingsTier tier) => _cascade.PathFor(tier);

		/// <summary>
		/// Returns the tier that produced the effective value at <paramref name="dottedPath"/>
		/// (e.g. <c>"general.debugMode"</c>), or null if no tier sets it.
		/// </summary>
		public SettingsTier? TierFor(string dottedPath) => _cascade.TierFor(dottedPath);

		/// <summary>Re-reads and re-merges every tier, then raises <see cref="OnReloaded"/>.</summary>
		public void Reload()
		{
			_cascade.Load();
			Build();
			OnReloaded?.Invoke(_value);
		}

		private void Build()
		{
			foreach (string warning in _cascade.Warnings)
				Debug.LogWarning($"[SettingsFile<{typeof(T).Name}>] {warning}");

			try
			{
				_value = _cascade.Merged.ToObject<T>(SettingsJson.Serializer) ?? new T();
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[SettingsFile<{typeof(T).Name}>] Failed to build {typeof(T).Name} from merged settings: {ex.Message}");
				_value = new T();
			}
		}
	}
}
