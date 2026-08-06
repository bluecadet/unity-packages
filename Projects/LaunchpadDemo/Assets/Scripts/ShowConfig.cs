using System;

namespace LaunchpadDemo
{
	/// <summary>
	/// Global presentation config — the singleton model. Its source file
	/// ("config/config.json") is a bare JSON object, not an array, which is
	/// what makes it the singleton: DemoContentMapper reads it directly and
	/// emits exactly one ContentItem with the fixed id "config:global".
	/// </summary>
	[Serializable]
	public sealed class ShowConfig : Record
	{
		public string title;
		public string accentColor;
		public float slideDurationSeconds;
	}
}
