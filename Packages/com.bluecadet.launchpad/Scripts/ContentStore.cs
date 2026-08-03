using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Bluecadet.Launchpad
{
	/// <summary>Result of loading and mapping one content version from disk.</summary>
	public sealed class LoadedVersion<T>
	{
		public string VersionId;
		public string VersionFolder;
		public IReadOnlyList<ContentItem<T>> Items;
	}

	/// <summary>
	/// Resolves a downloaded Launchpad content version on disk and hands its
	/// configured source directories off to an IContentMapper.
	/// <para>
	/// Disk contract — exactly what Launchpad's downloader writes, and the
	/// only shape this class resolves:
	/// </para>
	/// <code>
	/// contentRoot/
	///	  manifest.json			 declares the current versionId
	///	  versions/
	///		&lt;versionId&gt;/	   folder name IS the version id
	///		  &lt;sourceFolder&gt;/ one per configured sourceFolders entry
	/// </code>
	/// <para>
	/// A version folder is a direct child of contentRoot or of one of its
	/// children (so both <c>contentRoot/&lt;versionId&gt;</c> and Launchpad's
	/// own <c>contentRoot/versions/&lt;versionId&gt;</c> resolve), and each
	/// configured source folder sits directly inside it. Nothing is matched
	/// by substring and no directory tree is crawled: a layout that doesn't
	/// match this shape simply doesn't resolve.
	/// </para>
	/// <para>
	/// Fully shape/format-agnostic: all parsing, joins, and item ordering
	/// live in the mapper; this class only finds a version folder, its
	/// source folders, and lists files.
	/// </para>
	/// </summary>
	public sealed class ContentStore<T> : IContentStore<T>
	{
		private const string ManifestFileName = "manifest.json";

		private readonly string _contentRoot;
		private readonly IReadOnlyList<string> _sourceFolders;
		private readonly IContentMapper<T> _mapper;

		public ContentStore(string contentRoot, IReadOnlyList<string> sourceFolders, IContentMapper<T> mapper)
		{
			_contentRoot = contentRoot;
			_sourceFolders = sourceFolders ?? Array.Empty<string>();
			_mapper = mapper;
		}

		/// <summary>
		/// Loads and maps the given versionId's content off the main thread.
		/// A non-null versionId resolves to the version folder of exactly
		/// that name and nothing else, so a promotion racing ahead of the
		/// disk write fails the load instead of silently handing back a
		/// stale version. versionId == null is the cold-boot path: the
		/// version manifest.json declares as current, or — with no usable
		/// manifest — the most recently written folder that has all
		/// configured source folders. Throws DirectoryNotFoundException if
		/// nothing resolves or a source folder is missing, IOException if a
		/// source folder can't be enumerated, or lets the mapper's own
		/// exception propagate on malformed/fatal content; callers are
		/// expected to catch and handle all three as a failed load.
		/// </summary>
		public Task<LoadedVersion<T>> LoadVersionAsync(string versionId, CancellationToken ct)
		{
			return Task.Run(() => LoadVersion(versionId, ct), ct);
		}

		private LoadedVersion<T> LoadVersion(string versionId, CancellationToken ct)
		{
			if (string.IsNullOrEmpty(_contentRoot))
			{
				throw new DirectoryNotFoundException("contentRoot is null or empty.");
			}

			if (!Directory.Exists(_contentRoot))
			{
				throw new DirectoryNotFoundException($"Content root not found: '{_contentRoot}'.");
			}

			string versionFolder = ResolveVersionFolder(versionId);
			if (versionFolder == null)
			{
				throw new DirectoryNotFoundException(
					$"Could not resolve a content version folder for versionId '{versionId ?? "(null, cold boot)"}' under '{_contentRoot}'.");
			}

			string resolvedVersionId = FolderName(versionFolder);

			var sources = new List<ContentSourceFolder>(_sourceFolders.Count);
			foreach (var sourceFolder in _sourceFolders)
			{
				ct.ThrowIfCancellationRequested();

				string sourceDir = Path.Combine(versionFolder, sourceFolder);
				if (!Directory.Exists(sourceDir))
				{
					throw new DirectoryNotFoundException(
						$"Content version '{resolvedVersionId}' has no '{sourceFolder}' directory at '{sourceDir}'.");
				}

				sources.Add(new ContentSourceFolder
				{
					SourceFolder = sourceFolder,
					FolderPath = sourceDir,
					Files = ListFiles(sourceDir)
				});
			}

			var context = new ContentSourceContext
			{
				VersionId = resolvedVersionId,
				VersionFolder = versionFolder,
				Sources = sources
			};

			// Mapper owns parsing/joins/ordering; a throw here is fatal for
			// this version load and is expected to propagate to the caller.
			IReadOnlyList<ContentItem<T>> items = _mapper.Map(context) ?? Array.Empty<ContentItem<T>>();

			return new LoadedVersion<T>
			{
				VersionId = resolvedVersionId,
				VersionFolder = versionFolder,
				Items = items
			};
		}

		private static List<string> ListFiles(string sourceDir)
		{
			string[] allFiles;
			try
			{
				allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
			}
			catch (Exception ex)
			{
				throw new IOException($"Failed to enumerate files under '{sourceDir}': {ex.Message}", ex);
			}

			var files = new List<string>(allFiles.Length);
			foreach (var file in allFiles)
			{
				// Launchpad's mediaDownloader writes a pre-transform backup
				// next to each transformed JSON file; including both would
				// duplicate content for mappers that read every file.
				if (file.EndsWith(".original.json", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				files.Add(file);
			}

			// Ordinal sort for a stable, cross-platform-identical order,
			// since directory enumeration order is otherwise OS-dependent.
			files.Sort(StringComparer.Ordinal);
			return files;
		}

		/// <summary>
		/// Finds the folder for the requested version, or for the current
		/// version when none is requested. Returns null if nothing resolves.
		/// </summary>
		private string ResolveVersionFolder(string versionId)
		{
			// An explicit versionId (from a promotion) must be that exact
			// folder. Resolving to anything else would load stale content
			// under a promoted version's name.
			if (!string.IsNullOrEmpty(versionId))
			{
				return FindVersionFolderNamed(versionId);
			}

			// Cold boot: whatever the downloader last declared current...
			string manifestVersionId = TryReadManifestVersionId();
			if (!string.IsNullOrEmpty(manifestVersionId))
			{
				string declared = FindVersionFolderNamed(manifestVersionId);
				if (declared != null)
				{
					return declared;
				}
			}

			// ...or, with no usable manifest, the newest folder that
			// actually holds content (hand-copied content, or a manifest
			// that never landed).
			string newest = null;
			DateTime newestWriteUtc = DateTime.MinValue;
			foreach (var candidate in EnumerateVersionFolderCandidates())
			{
				if (!HasAllSourceFolders(candidate))
				{
					continue;
				}

				DateTime writeUtc;
				try
				{
					writeUtc = Directory.GetLastWriteTimeUtc(candidate);
				}
				catch
				{
					continue;
				}

				if (newest == null || writeUtc > newestWriteUtc)
				{
					newest = candidate;
					newestWriteUtc = writeUtc;
				}
			}

			return newest;
		}

		private string FindVersionFolderNamed(string versionId)
		{
			foreach (var candidate in EnumerateVersionFolderCandidates())
			{
				if (string.Equals(FolderName(candidate), versionId, StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}
			}

			return null;
		}

		/// <summary>
		/// Every directory that could be a version folder under the disk
		/// contract: contentRoot's children, then theirs (Launchpad's own
		/// layout nests versions under contentRoot/versions).
		/// </summary>
		private IEnumerable<string> EnumerateVersionFolderCandidates()
		{
			foreach (var dir in SafeGetDirectories(_contentRoot))
			{
				yield return dir;
			}

			foreach (var dir in SafeGetDirectories(_contentRoot))
			{
				foreach (var subDir in SafeGetDirectories(dir))
				{
					yield return subDir;
				}
			}
		}

		private bool HasAllSourceFolders(string versionFolder)
		{
			foreach (var sourceFolder in _sourceFolders)
			{
				if (!Directory.Exists(Path.Combine(versionFolder, sourceFolder)))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Best-effort read of contentRoot/manifest.json's declared version
		/// id. Returns null (never throws) if the manifest doesn't exist,
		/// fails to parse, or doesn't declare a version id.
		/// </summary>
		private string TryReadManifestVersionId()
		{
			string manifestPath = Path.Combine(_contentRoot, ManifestFileName);
			if (!File.Exists(manifestPath))
			{
				return null;
			}

			try
			{
				JToken manifestRoot = JToken.Parse(File.ReadAllText(manifestPath));
				return LaunchpadJson.ExtractVersionId(manifestRoot);
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[ContentStore] Failed to parse '{manifestPath}': {ex.Message}");
				return null;
			}
		}

		private static string FolderName(string path)
		{
			return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
		}

		private static IEnumerable<string> SafeGetDirectories(string path)
		{
			try
			{
				return Directory.GetDirectories(path);
			}
			catch
			{
				return Array.Empty<string>();
			}
		}
	}
}
