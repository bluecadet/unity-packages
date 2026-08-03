using Newtonsoft.Json.Linq;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Helpers for pulling the "versionId" value out of JSON payloads coming
	/// from the Launchpad controller. Internal: used by LaunchpadClient and
	/// ContentStore only.
	/// </summary>
	internal static class LaunchpadJson
	{
		/// <summary>
		/// Returns the payload's version id, or null if none is present. The
		/// controller produces exactly two shapes: SSE
		/// content:version:promoted payloads and on-disk manifest.json both
		/// carry a top-level "versionId", while the content.manifest.read
		/// command response nests the manifest under its result envelope.
		/// </summary>
		public static string ExtractVersionId(JToken root)
		{
			JToken token = root?.SelectToken("versionId") ?? root?.SelectToken("result.manifest.versionId");

			// Only a scalar is a version id: `is JValue` rules out objects
			// and arrays, and a JSON null is a JValue whose Value is null.
			return token is JValue value ? value.Value?.ToString() : null;
		}
	}
}
