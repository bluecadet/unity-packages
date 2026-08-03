using System;
using System.Collections.Generic;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Result of diffing two content item lists by Id + ContentHash.
	/// </summary>
	public sealed class ContentDiff<T>
	{
		public List<ContentItem<T>> Added = new List<ContentItem<T>>();
		public List<ContentItem<T>> Changed = new List<ContentItem<T>>();
		public List<ContentItem<T>> Unchanged = new List<ContentItem<T>>();
		public List<string> RemovedIds = new List<string>();

		/// <summary>Same id set, same hashes, but sequence differs.</summary>
		public bool OrderChanged;

		/// <summary>No added/removed/changed AND order unchanged.</summary>
		public bool IsEmpty;
	}

	/// <summary>
	/// Pure diffing of two content snapshots by stable Id, using ContentHash
	/// to distinguish an unchanged item from a changed one. Every item must
	/// carry a non-empty Id, and Ids must be unique within each list;
	/// anything else throws InvalidOperationException. A missing or
	/// duplicated Id is a mapper bug (or a CMS record missing its stable
	/// identity field) that leaves downstream Id-keyed state undefined, so
	/// it aborts the version load rather than producing a partial diff.
	/// </summary>
	public static class ContentDiffer
	{
		public static ContentDiff<T> Diff<T>(IReadOnlyList<ContentItem<T>> oldItems, IReadOnlyList<ContentItem<T>> newItems)
		{
			var diff = new ContentDiff<T>();
			newItems ??= new List<ContentItem<T>>();

			var oldById = new Dictionary<string, ContentItem<T>>();
			if (oldItems != null)
			{
				for (int i = 0; i < oldItems.Count; i++)
				{
					ContentItem<T> item = oldItems[i];
					RequireId(item, i, "oldItems");

					// Last-wins would quietly pick one of the two records to
					// diff against, so the same duplicate that aborts a new
					// snapshot has to abort an old one too.
					if (oldById.ContainsKey(item.Id))
					{
						throw new InvalidOperationException(
							$"[ContentDiffer] Duplicate content Id '{item.Id}' found in oldItems[{i}].");
					}

					oldById[item.Id] = item;
				}
			}

			var newIds = new HashSet<string>();
			for (int i = 0; i < newItems.Count; i++)
			{
				ContentItem<T> item = newItems[i];
				RequireId(item, i, "newItems");

				if (!newIds.Add(item.Id))
				{
					throw new InvalidOperationException(
						$"[ContentDiffer] Duplicate content Id '{item.Id}' found in newItems[{i}].");
				}

				if (oldById.TryGetValue(item.Id, out var oldItem))
				{
					if (oldItem.ContentHash == item.ContentHash)
					{
						diff.Unchanged.Add(item);
					}
					else
					{
						diff.Changed.Add(item);
					}
				}
				else
				{
					diff.Added.Add(item);
				}
			}

			if (oldItems != null)
			{
				foreach (var item in oldItems)
				{
					if (!newIds.Contains(item.Id))
					{
						diff.RemovedIds.Add(item.Id);
					}
				}
			}

			bool sameMembership = diff.Added.Count == 0 && diff.Changed.Count == 0 && diff.RemovedIds.Count == 0;

			if (sameMembership && oldItems != null && oldItems.Count == newItems.Count)
			{
				for (int i = 0; i < oldItems.Count; i++)
				{
					if (oldItems[i].Id != newItems[i].Id)
					{
						diff.OrderChanged = true;
						break;
					}
				}
			}

			diff.IsEmpty = sameMembership && !diff.OrderChanged;
			return diff;
		}

		private static void RequireId<T>(ContentItem<T> item, int index, string listName)
		{
			if (item == null)
			{
				throw new InvalidOperationException(
					$"[ContentDiffer] {listName}[{index}] is null; every content item needs a stable Id.");
			}

			if (string.IsNullOrEmpty(item.Id))
			{
				throw new InvalidOperationException(
					$"[ContentDiffer] {listName}[{index}] has a{(item.Id == null ? " null" : "n empty")} Id; every content item needs a stable Id.");
			}
		}
	}
}
