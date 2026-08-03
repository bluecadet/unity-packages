using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Utils
{
	/// <summary>
	/// A dotted path to a single settings value, e.g. <c>"general.debugMode"</c>, together with the JSON
	/// walk that goes with it. Only objects are walked through: every other token, arrays included, is a
	/// leaf the path stops at, which is why an array is tinted, diffed and saved as one value here rather
	/// than element by element.
	/// </summary>
	internal readonly struct SettingsPath
	{
		/// <summary>The empty path, i.e. the root object itself. Used as the starting prefix of a walk.</summary>
		internal static readonly SettingsPath Root = new SettingsPath(string.Empty);

		private readonly string _dottedPath;

		internal SettingsPath(string dottedPath)
		{
			_dottedPath = dottedPath ?? string.Empty;
		}

		/// <summary>True if <paramref name="token"/> is a value a path ends at instead of descending into.</summary>
		internal static bool IsLeaf(JToken token) => !(token is JObject obj && obj.HasValues);

		/// <summary>This path with <paramref name="suffix"/> (a segment, or a relative dotted path) appended; either side may be empty.</summary>
		internal SettingsPath Append(string suffix)
		{
			if (string.IsNullOrEmpty(_dottedPath))
				return new SettingsPath(suffix);

			return string.IsNullOrEmpty(suffix) ? this : new SettingsPath($"{_dottedPath}.{suffix}");
		}

		/// <summary>
		/// The value at this path in <paramref name="root"/>, or null if no object along the way defines it.
		/// An explicit JSON null is a value and comes back as a token, not as null.
		/// </summary>
		internal JToken Resolve(JObject root)
		{
			JToken current = root;

			foreach (string segment in Segments)
			{
				if (current is JObject obj && obj.TryGetValue(segment, out JToken next))
					current = next;
				else
					return null;
			}

			return current;
		}

		/// <summary>Sets this path's value on <paramref name="root"/>, creating intermediate objects as needed.</summary>
		internal void Set(JObject root, JToken value)
		{
			string[] segments = Segments;
			JObject current = root;

			for (int i = 0; i < segments.Length - 1; i++)
			{
				if (!(current[segments[i]] is JObject child))
				{
					child = new JObject();
					current[segments[i]] = child;
				}

				current = child;
			}

			current[segments[^1]] = value ?? JValue.CreateNull();
		}

		/// <summary>
		/// Removes this path's leaf from <paramref name="root"/>, then any ancestor object the removal left
		/// empty, so a stripped path doesn't leave behind a trail of empty objects. Returns true if a leaf
		/// was removed.
		/// </summary>
		internal bool Remove(JObject root)
		{
			string[] segments = Segments;
			var chain = new List<JObject> { root };
			JObject current = root;

			for (int i = 0; i < segments.Length - 1; i++)
			{
				if (!(current[segments[i]] is JObject child))
					return false;

				chain.Add(child);
				current = child;
			}

			bool removed = current.Remove(segments[^1]);

			if (removed)
			{
				for (int i = chain.Count - 1; i >= 1; i--)
				{
					if (chain[i].HasValues)
						break;

					chain[i - 1].Remove(segments[i - 1]);
				}
			}

			return removed;
		}

		public override string ToString() => _dottedPath ?? string.Empty;

		private string[] Segments => (_dottedPath ?? string.Empty).Split('.');
	}
}
