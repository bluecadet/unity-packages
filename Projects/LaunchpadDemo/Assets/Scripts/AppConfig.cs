using System;
using Bluecadet.Launchpad;
using Bluecadet.Utils;

namespace LaunchpadDemo
{
	/// <summary>
	/// The app's settings shape: Launchpad's fields (controllerUrl,
	/// consumerId, contentRoot, sourceFolders, maxSwapDeferSeconds) come from
	/// the LaunchpadConfig base class, and app-specific fields are added
	/// here. LaunchpadConfig deliberately knows nothing about files — this
	/// app loads it through Utils' SettingsFile cascade
	/// (settings.json → settings.<machineId>.json → settings.local.json
	/// → --set CLI overrides), so every field is overridable per machine.
	/// </summary>
	[Serializable]
	[SettingsClass]
	public sealed class AppConfig : LaunchpadConfig, ISettingsValidator
	{
		/// <summary>Seconds without input before the app counts as idle (and content may hot-swap).</summary>
		public float idleAfterSeconds = 20f;

		/// <summary>
		/// Flags values the app cannot run with. Only the Bluecadet settings editor calls this; loading
		/// never does, so a bad file still boots (and fails loudly at the point of use).
		/// </summary>
		public void Validate(SettingsValidationErrors errors)
		{
			if (!Uri.TryCreate(controllerUrl, UriKind.Absolute, out Uri url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
				errors.Add(nameof(controllerUrl), "Must be an absolute http(s) URL, e.g. http://127.0.0.1:8710.");

			if (maxSwapDeferSeconds < 0f)
				errors.Add(nameof(maxSwapDeferSeconds), "Must be zero or more seconds.");
		}
	}
}
