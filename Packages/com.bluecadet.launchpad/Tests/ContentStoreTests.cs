using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Launchpad.Tests
{
	/// <summary>Never returns any items; used for tests about folder resolution, where the mapper itself is not under test.</summary>
	internal sealed class EmptyMapper : IContentMapper<string>
	{
		public IReadOnlyList<ContentItem<string>> Map(ContentSourceContext context)
		{
			return Array.Empty<ContentItem<string>>();
		}
	}

	/// <summary>
	/// Records the ContentSourceContext ContentStore built so a test can inspect
	/// exactly which files and folders the store resolved. The assertions this
	/// backs are about ContentStore's own file-listing contract (exclusion,
	/// sort order), not about whether Map was invoked.
	/// </summary>
	internal sealed class ContextCapturingMapper : IContentMapper<string>
	{
		public ContentSourceContext LastContext;

		public IReadOnlyList<ContentItem<string>> Map(ContentSourceContext context)
		{
			LastContext = context;
			return Array.Empty<ContentItem<string>>();
		}
	}

	/// <summary>
	/// A real mapper: parses every *.json file handed to it via the
	/// ContentJsonFiles helper and uppercases each item's "value". Proves
	/// ContentStore hands raw on-disk content to the mapper and returns
	/// whatever the mapper actually produced, rather than the store doing
	/// any shape-aware work itself.
	/// </summary>
	internal sealed class UppercaseJsonMapper : IContentMapper<string>
	{
		public IReadOnlyList<ContentItem<string>> Map(ContentSourceContext context)
		{
			var items = new List<ContentItem<string>>();
			foreach (var source in context.Sources)
			{
				foreach (var token in ContentJsonFiles.ParseItems(source.Files))
				{
					string id = (string)token["id"];
					string value = (string)token["value"];
					items.Add(new ContentItem<string> { Id = id, ContentHash = 0, Data = value.ToUpperInvariant() });
				}
			}

			return items;
		}
	}

	/// <summary>
	/// A real cross-source-folder join: later-configured source folders win
	/// on id collisions, matching the "overrides" pattern IContentMapper's
	/// doc comment calls out as the mapper's job, not ContentStore's.
	/// </summary>
	internal sealed class JoinMapper : IContentMapper<string>
	{
		public IReadOnlyList<ContentItem<string>> Map(ContentSourceContext context)
		{
			var order = new List<string>();
			var byId = new Dictionary<string, string>();

			foreach (var source in context.Sources)
			{
				foreach (var token in ContentJsonFiles.ParseItems(source.Files))
				{
					string id = (string)token["id"];
					string value = (string)token["value"];
					if (!byId.ContainsKey(id))
					{
						order.Add(id);
					}

					byId[id] = value;
				}
			}

			var items = new List<ContentItem<string>>();
			foreach (var id in order)
			{
				items.Add(new ContentItem<string> { Id = id, ContentHash = 0, Data = byId[id] });
			}

			return items;
		}
	}

	/// <summary>Returns parsed items in reverse of file order, to prove ContentStore preserves whatever order the mapper decides.</summary>
	internal sealed class ReverseOrderMapper : IContentMapper<string>
	{
		public IReadOnlyList<ContentItem<string>> Map(ContentSourceContext context)
		{
			var items = new List<ContentItem<string>>();
			foreach (var source in context.Sources)
			{
				foreach (var token in ContentJsonFiles.ParseItems(source.Files))
				{
					items.Add(new ContentItem<string> { Id = (string)token["id"], ContentHash = 0, Data = (string)token["id"] });
				}
			}

			items.Reverse();
			return items;
		}
	}

	/// <summary>Always throws, matching IContentMapper's documented "throw aborts the version entirely" contract for malformed content.</summary>
	internal sealed class ThrowingMapper : IContentMapper<string>
	{
		public IReadOnlyList<ContentItem<string>> Map(ContentSourceContext context)
		{
			throw new InvalidDataException("Mapper: fatal malformed content.");
		}
	}

	/// <summary>Returns null, exercising ContentStore's `?? Array.Empty(...)` fallback for a mapper that maps nothing.</summary>
	internal sealed class NullReturningMapper : IContentMapper<string>
	{
		public IReadOnlyList<ContentItem<string>> Map(ContentSourceContext context)
		{
			return null;
		}
	}

	[TestFixture]
	public class ContentStoreTests
	{
		private string _contentRoot;

		[SetUp]
		public void SetUp()
		{
			_contentRoot = Path.Combine(Path.GetTempPath(), "LaunchpadStoreTests_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_contentRoot);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_contentRoot))
			{
				Directory.Delete(_contentRoot, recursive: true);
			}
		}

		private string CreateVersionFolder(string versionId, params string[] sourceFolders)
		{
			string versionPath = Path.Combine(_contentRoot, versionId);
			Directory.CreateDirectory(versionPath);
			foreach (var sourceFolder in sourceFolders)
			{
				Directory.CreateDirectory(Path.Combine(versionPath, sourceFolder));
			}

			return versionPath;
		}

		private static void WriteFile(string path, string contents)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, contents);
		}

		private void WriteManifest(string versionId)
		{
			WriteFile(Path.Combine(_contentRoot, "manifest.json"), "{\"versionId\":\"" + versionId + "\"}");
		}

		/// <summary>
		/// Awaits a load and hands back whatever it threw, or null if it
		/// succeeded. Deliberately not NUnit's Assert.ThrowsAsync: that blocks
		/// the calling thread until the task completes, and ContentStore loads
		/// on a worker whose continuation is posted back to the editor's
		/// single main thread — the same thread Assert.ThrowsAsync just
		/// blocked, so the load never resumes and the run wedges forever.
		/// Awaiting leaves the main thread free to pump that continuation.
		/// </summary>
		private static async Task<Exception> CaptureThrowAsync(Func<Task> load)
		{
			try
			{
				await load();
			}
			catch (Exception ex)
			{
				return ex;
			}

			return null;
		}

		[Test]
		public async Task NullOrEmptyContentRoot_Throws()
		{
			var nullRootStore = new ContentStore<string>(null, Array.Empty<string>(), new EmptyMapper());
			var emptyRootStore = new ContentStore<string>(string.Empty, Array.Empty<string>(), new EmptyMapper());

			Assert.That(
				await CaptureThrowAsync(() => nullRootStore.LoadVersionAsync(null, CancellationToken.None)),
				Is.InstanceOf<DirectoryNotFoundException>());
			Assert.That(
				await CaptureThrowAsync(() => emptyRootStore.LoadVersionAsync(null, CancellationToken.None)),
				Is.InstanceOf<DirectoryNotFoundException>());
		}

		[Test]
		public async Task MissingContentRoot_Throws()
		{
			string missingRoot = Path.Combine(_contentRoot, "does-not-exist");
			var store = new ContentStore<string>(missingRoot, Array.Empty<string>(), new EmptyMapper());

			Assert.That(
				await CaptureThrowAsync(() => store.LoadVersionAsync(null, CancellationToken.None)),
				Is.InstanceOf<DirectoryNotFoundException>());
		}

		[Test]
		public async Task ExplicitVersionId_NotFound_Throws()
		{
			CreateVersionFolder("v1", "cms");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new EmptyMapper());

			Assert.That(
				await CaptureThrowAsync(() => store.LoadVersionAsync("v2", CancellationToken.None)),
				Is.InstanceOf<DirectoryNotFoundException>(),
				"A promoted version that isn't on disk yet must fail rather than resolve to some other folder.");
		}

		[Test]
		public async Task MissingSourceFolder_Throws()
		{
			CreateVersionFolder("v1");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new EmptyMapper());

			Assert.That(
				await CaptureThrowAsync(() => store.LoadVersionAsync("v1", CancellationToken.None)),
				Is.InstanceOf<DirectoryNotFoundException>());
		}

		[Test]
		public async Task ColdBoot_NothingResolves_Throws()
		{
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new EmptyMapper());

			Assert.That(
				await CaptureThrowAsync(() => store.LoadVersionAsync(null, CancellationToken.None)),
				Is.InstanceOf<DirectoryNotFoundException>());
		}

		[Test]
		public async Task ColdBoot_UsesManifestDeclaredVersion()
		{
			CreateVersionFolder("v1", "cms");
			CreateVersionFolder("v2", "cms");
			WriteManifest("v2");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new EmptyMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync(null, CancellationToken.None);

			Assert.That(result.VersionId, Is.EqualTo("v2"));
		}

		[Test]
		public async Task ColdBoot_MalformedManifest_FallsBackToNewestFolder()
		{
			LogAssert.Expect(LogType.Warning, new Regex("Failed to parse"));

			WriteFile(Path.Combine(_contentRoot, "manifest.json"), "{ not valid json ");
			CreateVersionFolder("v1", "cms");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new EmptyMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync(null, CancellationToken.None);

			Assert.That(result.VersionId, Is.EqualTo("v1"));
		}

		[Test]
		public async Task ColdBoot_NoManifest_PicksNewestFolderWithAllSourceFolders()
		{
			string older = CreateVersionFolder("v-old", "cms");
			string newer = CreateVersionFolder("v-new", "cms");
			// Explicit timestamps rather than a real sleep between writes:
			// deterministic, and avoids the timing-flakiness this repo's ADRs
			// steer away from.
			Directory.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			Directory.SetLastWriteTimeUtc(newer, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new EmptyMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync(null, CancellationToken.None);

			Assert.That(result.VersionId, Is.EqualTo("v-new"));
		}

		[Test]
		public async Task NestedVersionsFolder_Resolves()
		{
			string nested = Path.Combine(_contentRoot, "versions", "v1", "cms");
			Directory.CreateDirectory(nested);
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new EmptyMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync("v1", CancellationToken.None);

			Assert.That(result.VersionId, Is.EqualTo("v1"));
			Assert.That(result.VersionFolder, Is.EqualTo(Path.Combine(_contentRoot, "versions", "v1")));
		}

		[Test]
		public async Task OriginalJsonBackups_AreExcludedFromFilesPassedToMapper()
		{
			CreateVersionFolder("v1", "cms");
			WriteFile(Path.Combine(_contentRoot, "v1", "cms", "a.json"), "[]");
			WriteFile(Path.Combine(_contentRoot, "v1", "cms", "a.original.json"), "[]");
			var mapper = new ContextCapturingMapper();
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, mapper);

			await store.LoadVersionAsync("v1", CancellationToken.None);

			var files = mapper.LastContext.Sources[0].Files;
			Assert.That(files.Count, Is.EqualTo(1));
			Assert.That(files[0], Does.EndWith("a.json"));
		}

		[Test]
		public async Task FilesPassedToMapper_AreSortedOrdinally()
		{
			CreateVersionFolder("v1", "cms");
			WriteFile(Path.Combine(_contentRoot, "v1", "cms", "b.json"), "[]");
			WriteFile(Path.Combine(_contentRoot, "v1", "cms", "a.json"), "[]");
			WriteFile(Path.Combine(_contentRoot, "v1", "cms", "c.json"), "[]");
			var mapper = new ContextCapturingMapper();
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, mapper);

			await store.LoadVersionAsync("v1", CancellationToken.None);

			var files = mapper.LastContext.Sources[0].Files;
			Assert.That(files[0], Does.EndWith("a.json"));
			Assert.That(files[1], Does.EndWith("b.json"));
			Assert.That(files[2], Does.EndWith("c.json"));
		}

		[Test]
		public async Task Map_ReceivesRawFilesAndReturnsWhateverItActuallyParsed()
		{
			CreateVersionFolder("v1", "cms");
			WriteFile(Path.Combine(_contentRoot, "v1", "cms", "content.json"),
				"[{\"id\":\"a\",\"value\":\"hello\"},{\"id\":\"b\",\"value\":\"world\"}]");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new UppercaseJsonMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync("v1", CancellationToken.None);

			Assert.That(result.Items.Count, Is.EqualTo(2));
			Assert.That(result.Items[0].Id, Is.EqualTo("a"));
			Assert.That(result.Items[0].Data, Is.EqualTo("HELLO"), "The uppercase transform is the mapper's, not the store's; the store must hand the raw value through untouched.");
			Assert.That(result.Items[1].Data, Is.EqualTo("WORLD"));
		}

		[Test]
		public async Task Map_JoinsAcrossMultipleConfiguredSourceFolders()
		{
			CreateVersionFolder("v1", "base", "overrides");
			WriteFile(Path.Combine(_contentRoot, "v1", "base", "content.json"),
				"[{\"id\":\"a\",\"value\":\"base-a\"},{\"id\":\"b\",\"value\":\"base-b\"}]");
			WriteFile(Path.Combine(_contentRoot, "v1", "overrides", "content.json"),
				"[{\"id\":\"b\",\"value\":\"override-b\"}]");
			var store = new ContentStore<string>(_contentRoot, new[] { "base", "overrides" }, new JoinMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync("v1", CancellationToken.None);

			Assert.That(result.Items.Count, Is.EqualTo(2), "The store must supply both source folders' files, in configured order, for the mapper to join.");
			Assert.That(result.Items[0].Id, Is.EqualTo("a"));
			Assert.That(result.Items[0].Data, Is.EqualTo("base-a"));
			Assert.That(result.Items[1].Id, Is.EqualTo("b"));
			Assert.That(result.Items[1].Data, Is.EqualTo("override-b"), "'overrides' is configured after 'base' and must win on a colliding id.");
		}

		[Test]
		public async Task Map_ControlsTheFinalItemOrder()
		{
			CreateVersionFolder("v1", "cms");
			WriteFile(Path.Combine(_contentRoot, "v1", "cms", "content.json"),
				"[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"c\"}]");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new ReverseOrderMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync("v1", CancellationToken.None);

			Assert.That(result.Items[0].Id, Is.EqualTo("c"));
			Assert.That(result.Items[1].Id, Is.EqualTo("b"));
			Assert.That(result.Items[2].Id, Is.EqualTo("a"));
		}

		[Test]
		public async Task MapperThrow_PropagatesToTheCaller()
		{
			CreateVersionFolder("v1", "cms");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new ThrowingMapper());

			Assert.That(
				await CaptureThrowAsync(() => store.LoadVersionAsync("v1", CancellationToken.None)),
				Is.InstanceOf<InvalidDataException>(),
				"A mapper throw is fatal for the version and must reach the caller unwrapped.");
		}

		[Test]
		public async Task MapperReturningNull_ProducesAnEmptyItemListRatherThanNull()
		{
			CreateVersionFolder("v1", "cms");
			var store = new ContentStore<string>(_contentRoot, new[] { "cms" }, new NullReturningMapper());

			LoadedVersion<string> result = await store.LoadVersionAsync("v1", CancellationToken.None);

			Assert.That(result.Items, Is.Not.Null);
			Assert.That(result.Items, Is.Empty);
		}
	}
}
