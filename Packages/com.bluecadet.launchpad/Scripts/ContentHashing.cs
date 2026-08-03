using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Shared hashing helper so mappers compute ContentItem.ContentHash
	/// consistently.
	/// </summary>
	public static class ContentHashing
	{
		// SHA256 isn't thread-safe to share concurrently, but Hash() only
		// ever runs synchronously on whatever pool thread Task.Run assigned
		// it — never re-entrantly or from multiple threads at once — so a
		// [ThreadStatic] instance is safe and avoids re-allocating a new
		// SHA256 per item per load without needing any locking.
		[ThreadStatic]
		private static SHA256 _sha256;

		/// <summary>
		/// Hashes a caller-supplied canonical (stable key order, stable
		/// formatting) JSON string.
		/// </summary>
		public static ulong Hash(string canonicalJson)
		{
			if (string.IsNullOrEmpty(canonicalJson))
			{
				return 0UL;
			}

			byte[] bytes = Encoding.UTF8.GetBytes(canonicalJson);

			// com.unity.collections (present in this project) ships xxHash3,
			// which would be faster than SHA256 here — but every Hash64
			// overload it exposes takes a byte* and therefore requires
			// "Allow Unsafe Code", which is disabled project-wide
			// (ProjectSettings/ProjectSettings.asset: allowUnsafeCode: 0) and
			// out of scope for this library to change. SHA256 truncated to
			// 64 bits gives the same collision-resistant ulong hash surface
			// without requiring an unsafe context.
			SHA256 sha = _sha256 ??= SHA256.Create();
			byte[] hash = sha.ComputeHash(bytes);
			return BitConverter.ToUInt64(hash, 0);
		}

		/// <summary>
		/// Canonicalizes <paramref name="value"/> — recursively sorts object
		/// property names (ordinal) and serializes with compact formatting —
		/// removing the named top-level fields first, then hashes the
		/// result. Solves CMS exports whose JSON property order is not
		/// stable across identical republishes (which would otherwise make
		/// an unchanged item hash as "changed"), and lets callers exclude
		/// volatile/identity fields (timestamps, reassigned numeric ids,
		/// etc.) from the comparison.
		/// </summary>
		public static ulong Hash(JToken value, params string[] excludeTopLevelFields)
		{
			if (value == null)
			{
				return 0UL;
			}

			JToken clone = value.DeepClone();
			if (excludeTopLevelFields != null && clone is JObject topObject)
			{
				foreach (var field in excludeTopLevelFields)
				{
					topObject.Remove(field);
				}
			}

			JToken canonical = Canonicalize(clone);
			return Hash(canonical.ToString(Formatting.None));
		}

		private static JToken Canonicalize(JToken token)
		{
			switch (token)
			{
				case JObject obj:
					var sorted = new JObject();
					foreach (var prop in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
					{
						sorted.Add(prop.Name, Canonicalize(prop.Value));
					}

					return sorted;

				case JArray array:
					var canonicalArray = new JArray();
					foreach (var item in array)
					{
						canonicalArray.Add(Canonicalize(item));
					}

					return canonicalArray;

				default:
					return token;
			}
		}
	}
}
