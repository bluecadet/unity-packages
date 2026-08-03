using System.Threading;
using System.Threading.Tasks;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Format-agnostic load/refcount seam ContentManager drives instead of
	/// depending on TextureCache directly, so a project can supply a cache
	/// for any asset kind (textures, audio clips, video files, ...).
	/// </summary>
	public interface IAssetCache
	{
		/// <summary>
		/// Loads absolutePath if it isn't cached yet and claims one reference
		/// to it, returning true if the caller now owns that reference (and
		/// therefore owes exactly one Release). Returns false if the asset
		/// could not be loaded, in which case nothing was retained.
		///
		/// Loading and claiming must be a single step: an implementation that
		/// exposes a window where the asset is cached at zero references is
		/// racing every EvictUnreferenced that happens to interleave.
		/// </summary>
		Task<bool> RetainAsync(string absolutePath, CancellationToken ct);

		/// <summary>Drops one reference previously claimed by RetainAsync.</summary>
		void Release(string absolutePath);

		/// <summary>Frees any cached entry with no outstanding retains.</summary>
		void EvictUnreferenced();
	}
}
