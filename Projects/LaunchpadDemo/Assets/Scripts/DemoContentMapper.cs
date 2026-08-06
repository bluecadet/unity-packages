using System;
using System.Collections.Generic;
using System.IO;
using Bluecadet.Launchpad;
using Newtonsoft.Json.Linq;

namespace LaunchpadDemo
{
	/// <summary>
	/// Owns all knowledge of this project's content shape, across every
	/// model it carries. One ContentManager&lt;Record&gt;, one mapper: Map
	/// dispatches per source file (not per parse call, or every model's
	/// tokens would merge into one stream with nothing left to say which
	/// model a token came from) and returns a single mixed, flat list of
	/// ContentItem&lt;Record&gt; — see "Multiple content models" in the
	/// com.bluecadet.launchpad README. Runs on a background thread; throws
	/// on malformed content — including a missing or duplicated "config"
	/// singleton — so a bad version is rejected whole (never partially
	/// applied) — that's the IContentMapper contract, so no try/catch here.
	/// </summary>
	public sealed class DemoContentMapper : IContentMapper<Record>
	{
		public IReadOnlyList<ContentItem<Record>> Map(ContentSourceContext context)
		{
			var items = new List<ContentItem<Record>>();
			ContentItem<Record> config = null;

			foreach (ContentSourceFolder source in context.Sources)
			{
				foreach (string file in source.Files)
				{
					if (file.EndsWith("slides.json", StringComparison.OrdinalIgnoreCase))
					{
						items.AddRange(MapSlides(file, source.FolderPath));
					}
					else if (file.EndsWith("sponsors.json", StringComparison.OrdinalIgnoreCase))
					{
						items.AddRange(MapSponsors(file));
					}
					else if (file.EndsWith("config.json", StringComparison.OrdinalIgnoreCase))
					{
						if (config != null)
						{
							throw new InvalidDataException($"Duplicate 'config' singleton found at '{file}'.");
						}

						config = MapConfig(file);
					}
				}
			}

			if (config == null)
			{
				throw new InvalidDataException("Content version has no 'config/config.json' singleton.");
			}

			items.Add(config);
			return items;
		}

		private static IEnumerable<ContentItem<Record>> MapSlides(string file, string folderPath)
		{
			foreach (JToken token in ContentJsonFiles.ParseItems(new[] { file }))
			{
				Slide slide = token.ToObject<Slide>();
				slide.imagePath = string.IsNullOrEmpty(slide.image)
					? string.Empty
					: Path.Combine(folderPath, slide.image);

				yield return new ContentItem<Record>
				{
					Id = $"slide:{slide.id}",
					// Canonicalized hash so republished-but-unchanged records
					// don't register as changes in the diff.
					ContentHash = ContentHashing.Hash(token),
					Data = slide
				};
			}
		}

		private static IEnumerable<ContentItem<Record>> MapSponsors(string file)
		{
			foreach (JToken token in ContentJsonFiles.ParseItems(new[] { file }))
			{
				Sponsor sponsor = token.ToObject<Sponsor>();
				yield return new ContentItem<Record>
				{
					Id = $"sponsor:{sponsor.id}",
					ContentHash = ContentHashing.Hash(token),
					Data = sponsor
				};
			}
		}

		// config.json is a bare JSON object, not an array, so
		// ContentJsonFiles.ParseItems deliberately skips it (no "data" array
		// to find elements in) — read it directly and emit exactly one item
		// with a well-known, fixed Id.
		private static ContentItem<Record> MapConfig(string file)
		{
			JObject config = JObject.Parse(File.ReadAllText(file));
			return new ContentItem<Record>
			{
				Id = "config:global",
				// exportedAt is a re-export timestamp, not a real change —
				// excluding it keeps an unchanged config from hashing as changed.
				ContentHash = ContentHashing.Hash(config, "exportedAt"),
				Data = config.ToObject<ShowConfig>()
			};
		}
	}
}
