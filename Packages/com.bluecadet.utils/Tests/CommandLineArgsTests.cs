using NUnit.Framework;

namespace Bluecadet.Utils.Tests
{
	[TestFixture]
	public class CommandLineArgsTests
	{
		[Test]
		public void Parse_EmptyInput_HasNoArgs()
		{
			var args = CommandLineArgs.Parse();

			Assert.That(args.All.Count, Is.EqualTo(0));
		}

		[Test]
		public void Parse_FlagWithValue_ParsesValue()
		{
			var args = CommandLineArgs.Parse("--port", "8080");

			Assert.That(args.Get("port"), Is.EqualTo("8080"));
		}

		[Test]
		public void Parse_KeyEqualsValue_ParsesValue()
		{
			var args = CommandLineArgs.Parse("--env=staging");

			Assert.That(args.Get("env"), Is.EqualTo("staging"));
		}

		[Test]
		public void Parse_BareFlag_ParsesAsEmptyString()
		{
			var args = CommandLineArgs.Parse("--verbose");

			Assert.That(args.Get("verbose"), Is.EqualTo(string.Empty));
		}

		[Test]
		public void Parse_BareFlagFollowedByAnotherFlag_ParsesAsEmptyString()
		{
			var args = CommandLineArgs.Parse("--verbose", "--env=staging");

			Assert.That(args.Get("verbose"), Is.EqualTo(string.Empty));
			Assert.That(args.Get("env"), Is.EqualTo("staging"));
		}

		[Test]
		public void Get_IsCaseInsensitive()
		{
			var args = CommandLineArgs.Parse("--Port", "8080");

			Assert.That(args.Get("port"), Is.EqualTo("8080"));
			Assert.That(args.Get("PORT"), Is.EqualTo("8080"));
		}

		[TestCase("-x")]
		[TestCase("--x")]
		public void Parse_NormalizesLeadingDashes(string token)
		{
			var args = CommandLineArgs.Parse(token, "value");

			Assert.That(args.Get("x"), Is.EqualTo("value"));
		}

		[Test]
		public void Parse_RepeatedName_LastOccurrenceWinsInAll()
		{
			var args = CommandLineArgs.Parse("--env=dev", "--env=staging", "--env=prod");

			Assert.That(args.Get("env"), Is.EqualTo("prod"));
			Assert.That(args.All.Count, Is.EqualTo(1));
		}

		[Test]
		public void ParseText_QuotedToken_KeepsSpacesTogether()
		{
			var args = CommandLineArgs.ParseText("--name=\"Blue Cadet\" --verbose");

			Assert.That(args.Get("name"), Is.EqualTo("Blue Cadet"));
			Assert.That(args.HasFlag("verbose"), Is.True);
		}

		[Test]
		public void ParseText_QuotedTokenWithoutEquals_KeepsSpacesTogether()
		{
			var args = CommandLineArgs.ParseText("--message \"hello world\"");

			Assert.That(args.Get("message"), Is.EqualTo("hello world"));
		}

		[Test]
		public void ParseText_EmptyInput_HasNoArgs()
		{
			var args = CommandLineArgs.ParseText(string.Empty);

			Assert.That(args.All.Count, Is.EqualTo(0));
		}

		[Test]
		public void HasFlag_UnknownFlag_ReturnsFalse()
		{
			var args = CommandLineArgs.Parse("--verbose");

			Assert.That(args.HasFlag("missing"), Is.False);
		}

		[Test]
		public void Get_UnknownFlag_ReturnsFallback()
		{
			var args = CommandLineArgs.Parse();

			Assert.That(args.Get("missing", "fallback"), Is.EqualTo("fallback"));
		}

		[Test]
		public void TryGet_KnownFlag_ReturnsTrueAndValue()
		{
			var args = CommandLineArgs.Parse("--env=staging");

			bool found = args.TryGet("env", out string value);

			Assert.That(found, Is.True);
			Assert.That(value, Is.EqualTo("staging"));
		}

		[Test]
		public void TryGet_UnknownFlag_ReturnsFalse()
		{
			var args = CommandLineArgs.Parse();

			bool found = args.TryGet("missing", out string value);

			Assert.That(found, Is.False);
			Assert.That(value, Is.Null);
		}
	}
}
