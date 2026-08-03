using System;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Plain-data settings shared by Launchpad. This class has no knowledge
	/// of files, StreamingAssets, or how it gets loaded: apps subclass it to
	/// add their own project-specific fields and are responsible for loading
	/// it themselves (e.g. via a settings/config system of their choosing).
	/// </summary>
	[Serializable]
	public class LaunchpadConfig
	{
		public string controllerUrl = "http://127.0.0.1:8710";
		public string consumerId = "";
		public string contentRoot = string.Empty;
		public string[] sourceFolders = Array.Empty<string>();
		public float maxSwapDeferSeconds = 300f;
	}
}
