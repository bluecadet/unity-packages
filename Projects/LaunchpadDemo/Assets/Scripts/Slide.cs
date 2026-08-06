using System;

namespace LaunchpadDemo
{
	/// <summary>
	/// The app's content model — what one CMS record maps to. Fields mirror
	/// the JSON shape the CMS exports into the "slides" source folder.
	/// </summary>
	[Serializable]
	public sealed class Slide : Record
	{
		public string id;
		public string title;
		public string body;

		/// <summary>Image path relative to the slides source folder, as exported by the CMS. May be empty.</summary>
		public string image;

		/// <summary>Absolute image path, resolved by DemoContentMapper against the version folder on disk. Empty if the slide has no image.</summary>
		public string imagePath;
	}
}
