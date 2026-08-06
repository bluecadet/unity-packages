using Newtonsoft.Json;

namespace Bluecadet.Utils
{
	/// <summary>
	/// The single Json.NET configuration used for every settings conversion, so that reading and writing
	/// settings always agree. Notably it ignores reference loops: Unity value types such as
	/// <see cref="UnityEngine.Vector3"/> expose self-referencing properties (<c>normalized</c>,
	/// <c>normalized.normalized</c>, ...) that make the default serializer throw.
	/// </summary>
	internal static class SettingsJson
	{
		/// <summary>Settings for the <see cref="JsonConvert"/> string APIs.</summary>
		internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
		{
			ReferenceLoopHandling = ReferenceLoopHandling.Ignore
		};

		/// <summary>Serializer for the <c>JObject.FromObject</c> / <c>JToken.ToObject</c> APIs.</summary>
		internal static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);
	}
}
