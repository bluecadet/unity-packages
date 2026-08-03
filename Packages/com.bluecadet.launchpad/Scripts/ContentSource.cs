using System.Collections.Generic;

namespace Bluecadet.Launchpad
{
	/// <summary>One configured source folder resolved for one content version.</summary>
	public sealed class ContentSourceFolder
	{
		/// <summary>The configured name, e.g. "demo-cms".</summary>
		public string SourceFolder;

		/// <summary>Absolute resolved directory.</summary>
		public string FolderPath;

		/// <summary>
		/// ALL files under this directory (any extension), recursive,
		/// ordinally sorted by full path (stable cross-platform order), with
		/// Launchpad's *.original.json backups excluded.
		/// </summary>
		public IReadOnlyList<string> Files;
	}

	/// <summary>Everything an IContentMapper needs to map one content version.</summary>
	public sealed class ContentSourceContext
	{
		/// <summary>The resolved version id.</summary>
		public string VersionId;

		/// <summary>Absolute path to the resolved version folder.</summary>
		public string VersionFolder;

		/// <summary>Same order as the configured sourceFolders.</summary>
		public IReadOnlyList<ContentSourceFolder> Sources;
	}
}
