using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Launchpad.Tests
{
	[TestFixture]
	public class ContentHashingTests
	{
		[Test]
		public void Hash_String_NullOrEmpty_ReturnsZero()
		{
			Assert.That(ContentHashing.Hash((string)null), Is.EqualTo(0UL));
			Assert.That(ContentHashing.Hash(string.Empty), Is.EqualTo(0UL));
		}

		[Test]
		public void Hash_String_SameInput_IsDeterministic()
		{
			ulong a = ContentHashing.Hash("{\"foo\":1}");
			ulong b = ContentHashing.Hash("{\"foo\":1}");

			Assert.That(a, Is.EqualTo(b));
		}

		[Test]
		public void Hash_String_DifferentInput_DiffersHash()
		{
			ulong a = ContentHashing.Hash("{\"foo\":1}");
			ulong b = ContentHashing.Hash("{\"foo\":2}");

			Assert.That(a, Is.Not.EqualTo(b));
		}

		[Test]
		public void Hash_JToken_Null_ReturnsZero()
		{
			Assert.That(ContentHashing.Hash((JToken)null), Is.EqualTo(0UL));
		}

		[Test]
		public void Hash_JToken_PropertyOrderDoesNotAffectHash()
		{
			JToken a = JToken.Parse("{\"foo\":1,\"bar\":2}");
			JToken b = JToken.Parse("{\"bar\":2,\"foo\":1}");

			Assert.That(ContentHashing.Hash(a), Is.EqualTo(ContentHashing.Hash(b)));
		}

		[Test]
		public void Hash_JToken_NestedPropertyOrderDoesNotAffectHash()
		{
			JToken a = JToken.Parse("{\"outer\":{\"foo\":1,\"bar\":2}}");
			JToken b = JToken.Parse("{\"outer\":{\"bar\":2,\"foo\":1}}");

			Assert.That(ContentHashing.Hash(a), Is.EqualTo(ContentHashing.Hash(b)));
		}

		[Test]
		public void Hash_JToken_ArrayOrderAffectsHash()
		{
			JToken a = JToken.Parse("[1,2,3]");
			JToken b = JToken.Parse("[3,2,1]");

			Assert.That(ContentHashing.Hash(a), Is.Not.EqualTo(ContentHashing.Hash(b)));
		}

		[Test]
		public void Hash_JToken_DifferentValues_DiffersHash()
		{
			JToken a = JToken.Parse("{\"foo\":1}");
			JToken b = JToken.Parse("{\"foo\":2}");

			Assert.That(ContentHashing.Hash(a), Is.Not.EqualTo(ContentHashing.Hash(b)));
		}

		[Test]
		public void Hash_JToken_ExcludedTopLevelField_DoesNotAffectHash()
		{
			JToken a = JToken.Parse("{\"id\":\"1\",\"updatedAt\":\"2024-01-01\"}");
			JToken b = JToken.Parse("{\"id\":\"1\",\"updatedAt\":\"2099-12-31\"}");

			ulong hashA = ContentHashing.Hash(a, "updatedAt");
			ulong hashB = ContentHashing.Hash(b, "updatedAt");

			Assert.That(hashA, Is.EqualTo(hashB));
		}

		[Test]
		public void Hash_JToken_NonExcludedFieldChange_DiffersHash()
		{
			JToken a = JToken.Parse("{\"id\":\"1\",\"updatedAt\":\"2024-01-01\"}");
			JToken b = JToken.Parse("{\"id\":\"2\",\"updatedAt\":\"2024-01-01\"}");

			ulong hashA = ContentHashing.Hash(a, "updatedAt");
			ulong hashB = ContentHashing.Hash(b, "updatedAt");

			Assert.That(hashA, Is.Not.EqualTo(hashB));
		}

		[Test]
		public void Hash_JToken_DoesNotMutateOriginalToken()
		{
			JToken a = JToken.Parse("{\"id\":\"1\",\"updatedAt\":\"2024-01-01\"}");

			ContentHashing.Hash(a, "updatedAt");

			Assert.That(((JObject)a).ContainsKey("updatedAt"), Is.True);
		}
	}
}
