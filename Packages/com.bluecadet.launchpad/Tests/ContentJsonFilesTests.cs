using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Launchpad.Tests
{
	[TestFixture]
	public class ContentJsonFilesTests
	{
		private string _tempDir;

		[SetUp]
		public void SetUp()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), "LaunchpadTests_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_tempDir);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}

		private string WriteFile(string name, string contents)
		{
			string path = Path.Combine(_tempDir, name);
			File.WriteAllText(path, contents);
			return path;
		}

		[Test]
		public void ParseItems_NullFiles_YieldsNothing()
		{
			List<JToken> items = ContentJsonFiles.ParseItems(null).ToList();

			Assert.That(items, Is.Empty);
		}

		[Test]
		public void ParseItems_EmptyFiles_YieldsNothing()
		{
			List<JToken> items = ContentJsonFiles.ParseItems(Enumerable.Empty<string>()).ToList();

			Assert.That(items, Is.Empty);
		}

		[Test]
		public void ParseItems_BareArray_YieldsEachElement()
		{
			string file = WriteFile("bare.json", "[{\"id\":\"a\"},{\"id\":\"b\"}]");

			List<JToken> items = ContentJsonFiles.ParseItems(new[] { file }).ToList();

			Assert.That(items.Count, Is.EqualTo(2));
			Assert.That((string)items[0]["id"], Is.EqualTo("a"));
			Assert.That((string)items[1]["id"], Is.EqualTo("b"));
		}

		[Test]
		public void ParseItems_DataEnvelope_YieldsEachElement()
		{
			string file = WriteFile("envelope.json", "{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");

			List<JToken> items = ContentJsonFiles.ParseItems(new[] { file }).ToList();

			Assert.That(items.Count, Is.EqualTo(2));
			Assert.That((string)items[0]["id"], Is.EqualTo("a"));
			Assert.That((string)items[1]["id"], Is.EqualTo("b"));
		}

		[Test]
		public void ParseItems_ObjectWithoutDataProperty_YieldsNothing()
		{
			string file = WriteFile("object.json", "{\"foo\":\"bar\"}");

			List<JToken> items = ContentJsonFiles.ParseItems(new[] { file }).ToList();

			Assert.That(items, Is.Empty);
		}

		[Test]
		public void ParseItems_DataPropertyNotArray_Throws()
		{
			string file = WriteFile("bad-data.json", "{\"data\":\"not-an-array\"}");

			Assert.Throws<InvalidDataException>(() => ContentJsonFiles.ParseItems(new[] { file }).ToList());
		}

		[Test]
		public void ParseItems_MalformedJson_ThrowsJsonException()
		{
			string file = WriteFile("malformed.json", "{ not valid json ");

			// Parsing throws the more specific JsonReaderException, a subclass
			// of JsonException; Assert.Catch (unlike Assert.Throws) accepts
			// any derived exception type.
			Assert.Catch<JsonException>(() => ContentJsonFiles.ParseItems(new[] { file }).ToList());
		}

		[Test]
		public void ParseItems_NonJsonExtension_IsIgnored()
		{
			string file = WriteFile("data.txt", "[{\"id\":\"a\"}]");

			List<JToken> items = ContentJsonFiles.ParseItems(new[] { file }).ToList();

			Assert.That(items, Is.Empty);
		}

		[Test]
		public void ParseItems_NullOrEmptyFileEntries_AreSkipped()
		{
			List<JToken> items = ContentJsonFiles.ParseItems(new[] { null, string.Empty }).ToList();

			Assert.That(items, Is.Empty);
		}

		[Test]
		public void ParseItems_ExtensionMatchIsCaseInsensitive()
		{
			string file = WriteFile("bare.JSON", "[{\"id\":\"a\"}]");

			List<JToken> items = ContentJsonFiles.ParseItems(new[] { file }).ToList();

			Assert.That(items.Count, Is.EqualTo(1));
		}

		[Test]
		public void ParseItems_MultipleFiles_YieldsInFileOrder()
		{
			string first = WriteFile("first.json", "[{\"id\":\"a\"}]");
			string second = WriteFile("second.json", "[{\"id\":\"b\"}]");

			List<JToken> items = ContentJsonFiles.ParseItems(new[] { first, second }).ToList();

			Assert.That(items.Count, Is.EqualTo(2));
			Assert.That((string)items[0]["id"], Is.EqualTo("a"));
			Assert.That((string)items[1]["id"], Is.EqualTo("b"));
		}
	}
}
