using System.Collections.Generic;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Owns all content-shape knowledge for one project/CMS/format: parsing
	/// (any format, not just JSON), cross-file/cross-source joins, and the
	/// FINAL item order — ContentDiffer's OrderChanged and every view honor
	/// whatever order Map returns. ContentStore itself is fully
	/// shape-agnostic; it only resolves the version folder and its
	/// configured source directories and hands the file listing here.
	/// </summary>
	public interface IContentMapper<T>
	{
		/// <summary>
		/// Called on a background thread, once per version load. Throw on
		/// fatal/malformed input: a throw aborts the version entirely (no
		/// stage, no ack) and leaves the current version's CurrentVersionId
		/// unchanged, so the fallback poll retries later. This is the
		/// documented failure mode for partial/corrupt content — do not
		/// attempt to partially map and continue.
		/// </summary>
		IReadOnlyList<ContentItem<T>> Map(ContentSourceContext context);
	}
}
