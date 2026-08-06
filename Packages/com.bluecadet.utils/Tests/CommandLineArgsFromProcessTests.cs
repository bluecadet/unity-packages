using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bluecadet.Utils.Tests
{
	/// <summary>
	/// Covers the in-editor half of <see cref="CommandLineArgs.FromProcess"/> and
	/// <see cref="AppEnvironment.Build"/>: both must re-read the simulated-args file on every
	/// call (no memoization), the way the Bluecadet Settings window's Reload button relies on when
	/// it rebuilds the environment. A regression back to caching either of these would make edits
	/// made in the "Simulated Args" window invisible until the next domain reload.
	/// </summary>
	[TestFixture]
	public class CommandLineArgsFromProcessTests
	{
		private string _argsPath;
		private string _backup;
		private bool _hadBackup;

		[SetUp]
		public void SetUp()
		{
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			_argsPath = Path.Combine(projectRoot ?? string.Empty, CommandLineArgs.SimulatedArgsProjectPath);

			_hadBackup = File.Exists(_argsPath);
			if (_hadBackup)
				_backup = File.ReadAllText(_argsPath);
		}

		[TearDown]
		public void TearDown()
		{
			if (_hadBackup)
				File.WriteAllText(_argsPath, _backup);
			else if (File.Exists(_argsPath))
				File.Delete(_argsPath);
		}

		private void WriteSimulatedArgs(string text) => File.WriteAllText(_argsPath, text);

		[Test]
		public void FromProcess_RereadsFileOnEveryCall_NoStaleCache()
		{
			WriteSimulatedArgs("--env=first");
			Assert.That(CommandLineArgs.FromProcess().Get("env"), Is.EqualTo("first"));

			WriteSimulatedArgs("--env=second");
			Assert.That(CommandLineArgs.FromProcess().Get("env"), Is.EqualTo("second"));
		}

		[Test]
		public void FromProcess_SpaceSeparatedAssetsPath_IsParsed()
		{
			string dir = Path.Combine(Path.GetTempPath(), "CommandLineArgsFromProcessTests_" + Guid.NewGuid());
			WriteSimulatedArgs($"--assetsPath {dir}");

			Assert.That(CommandLineArgs.FromProcess().Get("assetsPath"), Is.EqualTo(dir));
		}

		[Test]
		public void FromProcess_EqualsSeparatedAssetsPath_IsParsed()
		{
			string dir = Path.Combine(Path.GetTempPath(), "CommandLineArgsFromProcessTests_" + Guid.NewGuid());
			WriteSimulatedArgs($"--assetsPath={dir}");

			Assert.That(CommandLineArgs.FromProcess().Get("assetsPath"), Is.EqualTo(dir));
		}

		[Test]
		public void Build_AssetsPathArgPresent_DataPathUsesArgNotStreamingAssets()
		{
			string dir = Path.Combine(Path.GetTempPath(), "CommandLineArgsFromProcessTests_" + Guid.NewGuid());
			WriteSimulatedArgs($"--assetsPath {dir}");

			AppEnvironment env = AppEnvironment.Build();

			Assert.That(env.DataPath, Is.EqualTo(dir));
			Assert.That(env.DataPath, Is.Not.EqualTo(Application.streamingAssetsPath));
		}

		[Test]
		public void Build_NoAssetsPathArg_FallsBackToStreamingAssetsPath()
		{
			WriteSimulatedArgs(string.Empty);

			AppEnvironment env = AppEnvironment.Build();

			Assert.That(env.DataPath, Is.EqualTo(Application.streamingAssetsPath));
		}

		[Test]
		public void Build_CalledAgainAfterArgsChange_PicksUpFreshDataPath()
		{
			WriteSimulatedArgs(string.Empty);
			Assert.That(AppEnvironment.Build().DataPath, Is.EqualTo(Application.streamingAssetsPath));

			string dir = Path.Combine(Path.GetTempPath(), "CommandLineArgsFromProcessTests_" + Guid.NewGuid());
			WriteSimulatedArgs($"--assetsPath {dir}");

			Assert.That(AppEnvironment.Build().DataPath, Is.EqualTo(dir));
		}
	}
}
