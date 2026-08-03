using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Convenience helper so the common "content is plain JSON files" case
	/// stays a tiny IContentMapper implementation instead of hand-rolling
	/// file IO and envelope handling in every mapper.
	/// </summary>
	public static class ContentJsonFiles
	{
		/// <summary>
		/// Parses each *.json file among <paramref name="files"/> (bare JSON
		/// array, or a {"data":[...]} envelope) and yields every element in
		/// file order. Non-.json files are ignored. A *.json file that parses
		/// to a bare JSON object with no "data" array (e.g. a singleton
		/// config file) is silently skipped too — a mapper needs to parse
		/// such a file itself (JObject.Parse) and emit its own ContentItem
		/// (see "Multiple content models" > "Singleton models" in the
		/// package README). Read failures, Newtonsoft.Json.JsonException on
		/// malformed JSON, and InvalidDataException if a "data" property
		/// exists but isn't a JSON array all propagate — a mapper built on
		/// this helper should let them through so IContentMapper's "throw
		/// aborts the version" contract applies, rather than silently
		/// dropping content.
		/// </summary>
		public static IEnumerable<JToken> ParseItems(IEnumerable<string> files)
		{
			if (files == null)
			{
				yield break;
			}

			foreach (var file in files)
			{
				if (string.IsNullOrEmpty(file) || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				JToken root = JToken.Parse(File.ReadAllText(file));

				JArray dataArray = root as JArray;
				if (dataArray == null && root is JObject rootObject && rootObject.TryGetValue("data", out JToken dataToken))
				{
					dataArray = dataToken as JArray;
					if (dataArray == null)
					{
						// "data" exists but isn't the array the envelope
						// contract requires: this is a mapper-facing bug in
						// the CMS export, not something to silently drop, so
						// it must abort the version load like any other
						// malformed-content case.
						throw new InvalidDataException(
							$"'{file}': \"data\" property is not a JSON array (found {dataToken.Type}).");
					}
				}

				if (dataArray == null)
				{
					continue;
				}

				foreach (var item in dataArray)
				{
					yield return item;
				}
			}
		}
	}
}
