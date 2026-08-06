using System.Collections.Generic;
using System.IO;
using Bluecadet.Launchpad;
using Newtonsoft.Json.Linq;

namespace LaunchpadDemo
{
	/// <summary>
	/// Owns all knowledge of this project's content shape: parses the JSON
	/// files in the "slides" source folder, resolves relative media paths to
	/// absolute ones, and returns items in display order. Runs on a
	/// background thread; throws on malformed content so a bad version is
	/// rejected whole (never partially applied) — that's the IContentMapper
	/// contract, so no try/catch here.
	/// </summary>
	public sealed class SlideMapper : IContentMapper<Slide>
	{
		public IReadOnlyList<ContentItem<Slide>> Map(ContentSourceContext context)
		{
			// Sources arrive in configured sourceFolders order; this app has one.
			ContentSourceFolder source = context.Sources[0];

			var items = new List<ContentItem<Slide>>();
			foreach (JToken token in ContentJsonFiles.ParseItems(source.Files))
			{
				Slide slide = token.ToObject<Slide>();
				slide.imagePath = string.IsNullOrEmpty(slide.image)
					? string.Empty
					: Path.Combine(source.FolderPath, slide.image);

				items.Add(new ContentItem<Slide>
				{
					Id = slide.id,
					// Canonicalized hash so republished-but-unchanged records
					// don't register as changes in the diff.
					ContentHash = ContentHashing.Hash(token),
					Data = slide
				});
			}

			return items;
		}
	}
}
