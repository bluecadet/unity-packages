using System.Threading;
using System.Threading.Tasks;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// The disk-facing half of the version lifecycle, as ContentManager sees
	/// it: turn a versionId (or null, meaning "whatever is current on disk")
	/// into a loaded, mapped snapshot. ContentStore&lt;T&gt; is the production
	/// implementation; tests substitute their own.
	/// </summary>
	public interface IContentStore<T>
	{
		/// <summary>
		/// Loads and maps a version off the main thread. A null versionId
		/// means the cold-boot path: resolve whatever version is already on
		/// disk. Throws DirectoryNotFoundException if nothing resolves, and
		/// is allowed to throw anything else on malformed content — callers
		/// treat every throw as a failed load.
		/// </summary>
		Task<LoadedVersion<T>> LoadVersionAsync(string versionId, CancellationToken ct);
	}
}
