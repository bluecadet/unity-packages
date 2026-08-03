using System;
using System.IO;
using NUnit.Framework;

namespace Bluecadet.Utils.Tests
{
	[TestFixture]
	public class AppEnvironmentTests
	{
		private static AppEnvironment MakeEnvironment(string argsText = "") =>
			new AppEnvironment("/tmp/some-dir", "TEST-MACHINE", CommandLineArgs.ParseText(argsText));

		[Test]
		public void Constructor_NullArgs_Throws()
		{
			Assert.Throws<ArgumentNullException>(() => new AppEnvironment("/tmp/some-dir", "TEST-MACHINE", null));
		}

		[Test]
		public void ResolvePath_AbsolutePath_PassesThrough()
		{
			var env = MakeEnvironment();

			string absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "file.json");
			Assert.That(env.ResolvePath(absolute), Is.EqualTo(absolute));
		}

		[Test]
		public void ResolvePath_RelativePath_ResolvesUnderDataPath()
		{
			var env = MakeEnvironment();

			Assert.That(env.ResolvePath("settings.json"), Is.EqualTo(Path.Combine("/tmp/some-dir", "settings.json")));
		}

		[Test]
		public void Args_ExposesParsedArguments()
		{
			var env = MakeEnvironment("--verbose");

			Assert.That(env.Args.HasFlag("verbose"), Is.True);
		}
	}
}
