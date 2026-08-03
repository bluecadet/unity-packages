using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Bluecadet.Launchpad.Tests
{
	[TestFixture]
	public class ContentDifferTests
	{
		private static ContentItem<string> Item(string id, ulong hash, string data = null)
		{
			return new ContentItem<string> { Id = id, ContentHash = hash, Data = data ?? id };
		}

		[Test]
		public void Diff_NewItemNotInOld_IsAdded()
		{
			var oldItems = new List<ContentItem<string>>();
			var newItems = new List<ContentItem<string>> { Item("a", 1) };

			ContentDiff<string> diff = ContentDiffer.Diff(oldItems, newItems);

			Assert.That(diff.Added.Count, Is.EqualTo(1));
			Assert.That(diff.Added[0].Id, Is.EqualTo("a"));
			Assert.That(diff.Changed, Is.Empty);
			Assert.That(diff.Unchanged, Is.Empty);
			Assert.That(diff.RemovedIds, Is.Empty);
			Assert.That(diff.IsEmpty, Is.False);
		}

		[Test]
		public void Diff_SameIdDifferentHash_IsChanged()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1) };
			var newItems = new List<ContentItem<string>> { Item("a", 2) };

			ContentDiff<string> diff = ContentDiffer.Diff(oldItems, newItems);

			Assert.That(diff.Changed.Count, Is.EqualTo(1));
			Assert.That(diff.Changed[0].Id, Is.EqualTo("a"));
			Assert.That(diff.Added, Is.Empty);
			Assert.That(diff.Unchanged, Is.Empty);
			Assert.That(diff.RemovedIds, Is.Empty);
			Assert.That(diff.IsEmpty, Is.False);
		}

		[Test]
		public void Diff_SameIdSameHash_IsUnchanged()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1) };
			var newItems = new List<ContentItem<string>> { Item("a", 1) };

			ContentDiff<string> diff = ContentDiffer.Diff(oldItems, newItems);

			Assert.That(diff.Unchanged.Count, Is.EqualTo(1));
			Assert.That(diff.Unchanged[0].Id, Is.EqualTo("a"));
			Assert.That(diff.Added, Is.Empty);
			Assert.That(diff.Changed, Is.Empty);
			Assert.That(diff.RemovedIds, Is.Empty);
			Assert.That(diff.IsEmpty, Is.True);
		}

		[Test]
		public void Diff_OldIdMissingFromNew_IsRemoved()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1), Item("b", 2) };
			var newItems = new List<ContentItem<string>> { Item("a", 1) };

			ContentDiff<string> diff = ContentDiffer.Diff(oldItems, newItems);

			Assert.That(diff.RemovedIds.Count, Is.EqualTo(1));
			Assert.That(diff.RemovedIds[0], Is.EqualTo("b"));
			Assert.That(diff.Unchanged.Count, Is.EqualTo(1));
			Assert.That(diff.IsEmpty, Is.False);
		}

		[Test]
		public void Diff_SameMembershipReordered_SetsOrderChangedAndNotEmpty()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1), Item("b", 2) };
			var newItems = new List<ContentItem<string>> { Item("b", 2), Item("a", 1) };

			ContentDiff<string> diff = ContentDiffer.Diff(oldItems, newItems);

			Assert.That(diff.Added, Is.Empty);
			Assert.That(diff.Changed, Is.Empty);
			Assert.That(diff.RemovedIds, Is.Empty);
			Assert.That(diff.Unchanged.Count, Is.EqualTo(2));
			Assert.That(diff.OrderChanged, Is.True);
			Assert.That(diff.IsEmpty, Is.False);
		}

		[Test]
		public void Diff_SameMembershipSameOrder_IsEmptyAndOrderUnchanged()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1), Item("b", 2) };
			var newItems = new List<ContentItem<string>> { Item("a", 1), Item("b", 2) };

			ContentDiff<string> diff = ContentDiffer.Diff(oldItems, newItems);

			Assert.That(diff.OrderChanged, Is.False);
			Assert.That(diff.IsEmpty, Is.True);
		}

		[Test]
		public void Diff_NullOldItems_TreatsEverythingAsAdded()
		{
			var newItems = new List<ContentItem<string>> { Item("a", 1) };

			ContentDiff<string> diff = ContentDiffer.Diff(null, newItems);

			Assert.That(diff.Added.Count, Is.EqualTo(1));
			Assert.That(diff.RemovedIds, Is.Empty);
		}

		[Test]
		public void Diff_NullNewItems_TreatsEverythingAsRemoved()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1) };

			ContentDiff<string> diff = ContentDiffer.Diff(oldItems, null);

			Assert.That(diff.RemovedIds, Is.EquivalentTo(new[] { "a" }));
			Assert.That(diff.Added, Is.Empty);
			Assert.That(diff.IsEmpty, Is.False);
		}

		[Test]
		public void Diff_DuplicateIdInNewItems_Throws()
		{
			var oldItems = new List<ContentItem<string>>();
			var newItems = new List<ContentItem<string>> { Item("a", 1), Item("a", 2) };

			Assert.Throws<InvalidOperationException>(() => ContentDiffer.Diff(oldItems, newItems));
		}

		[Test]
		public void Diff_DuplicateIdInOldItems_Throws()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1), Item("a", 2) };
			var newItems = new List<ContentItem<string>> { Item("a", 1) };

			var ex = Assert.Throws<InvalidOperationException>(() => ContentDiffer.Diff(oldItems, newItems));
			Assert.That(ex.Message, Does.Contain("oldItems[1]"));
		}

		[Test]
		public void Diff_NullIdInNewItems_Throws()
		{
			var oldItems = new List<ContentItem<string>>();
			var newItems = new List<ContentItem<string>> { Item("a", 1), Item(null, 2) };

			var ex = Assert.Throws<InvalidOperationException>(() => ContentDiffer.Diff(oldItems, newItems));
			Assert.That(ex.Message, Does.Contain("newItems[1]"));
		}

		[Test]
		public void Diff_EmptyIdInNewItems_Throws()
		{
			var oldItems = new List<ContentItem<string>>();
			var newItems = new List<ContentItem<string>> { Item(string.Empty, 1) };

			var ex = Assert.Throws<InvalidOperationException>(() => ContentDiffer.Diff(oldItems, newItems));
			Assert.That(ex.Message, Does.Contain("newItems[0]"));
		}

		[Test]
		public void Diff_NullIdInOldItems_Throws()
		{
			var oldItems = new List<ContentItem<string>> { Item(null, 1) };
			var newItems = new List<ContentItem<string>>();

			var ex = Assert.Throws<InvalidOperationException>(() => ContentDiffer.Diff(oldItems, newItems));
			Assert.That(ex.Message, Does.Contain("oldItems[0]"));
		}

		[Test]
		public void Diff_EmptyIdInOldItems_Throws()
		{
			var oldItems = new List<ContentItem<string>> { Item("a", 1), Item(string.Empty, 2) };
			var newItems = new List<ContentItem<string>>();

			var ex = Assert.Throws<InvalidOperationException>(() => ContentDiffer.Diff(oldItems, newItems));
			Assert.That(ex.Message, Does.Contain("oldItems[1]"));
		}
	}
}
