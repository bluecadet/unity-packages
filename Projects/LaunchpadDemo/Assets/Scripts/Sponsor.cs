using System;

namespace LaunchpadDemo
{
	/// <summary>
	/// A sponsor record — text-only, no media to preload. Fields mirror the
	/// JSON shape the CMS exports into the "sponsors" source folder.
	/// </summary>
	[Serializable]
	public sealed class Sponsor : Record
	{
		public string id;
		public string name;
		public string tier;
		public string url;
	}
}
