using System;
using System.IO;
using UnityEngine;

namespace Bluecadet.Utils
{
	/// <summary>
	/// Immutable snapshot of the runtime environment: data directory, machine identity,
	/// and parsed command-line arguments. Also the entry point for constructing
	/// <see cref="SettingsFile{T}"/> instances.
	/// </summary>
	public sealed class AppEnvironment
	{
		private static volatile AppEnvironment _current;

		/// <summary>
		/// The current process's environment, built from <see cref="CommandLineArgs.FromProcess"/> and
		/// <see cref="Application.streamingAssetsPath"/>. Rebuilt eagerly on the main thread via
		/// <see cref="RuntimeInitializeLoadType.BeforeSceneLoad"/> so that <see cref="Application.streamingAssetsPath"/>
		/// is captured safely before any worker thread reads <see cref="Current"/>. If this is somehow first
		/// touched before <see cref="RuntimeInitializeLoadType.BeforeSceneLoad"/> fires, that first touch must
		/// happen on the main thread, since building it calls Unity APIs that are main-thread-only.
		/// </summary>
		public static AppEnvironment Current => _current ??= Build();

		/// <summary>
		/// The resolved data directory: the <c>--assetsPath</c> argument if present,
		/// otherwise <see cref="Application.streamingAssetsPath"/>.
		/// </summary>
		public string DataPath { get; }

		/// <summary>
		/// The machine identity: the <c>--machineId</c> argument if present,
		/// otherwise <see cref="Environment.MachineName"/>.
		/// </summary>
		public string MachineId { get; }

		/// <summary>The parsed command-line arguments for this environment.</summary>
		public CommandLineArgs Args { get; }

		/// <summary>Creates an environment from explicit, already-resolved values.</summary>
		public AppEnvironment(string dataPath, string machineId, CommandLineArgs args)
		{
			DataPath = dataPath;
			MachineId = machineId;
			Args = args ?? throw new ArgumentNullException(nameof(args));
		}

		/// <summary>
		/// Resolves <paramref name="pathOrRelative"/> against <see cref="DataPath"/>.
		/// An absolute path is returned unchanged; a relative path is combined with <see cref="DataPath"/>.
		/// </summary>
		public string ResolvePath(string pathOrRelative)
		{
			if (string.IsNullOrEmpty(pathOrRelative))
				return DataPath;

			return Path.IsPathRooted(pathOrRelative) ? pathOrRelative : Path.Combine(DataPath, pathOrRelative);
		}

		/// <summary>
		/// Creates a <see cref="SettingsFile{T}"/> whose file tiers live under <see cref="DataPath"/>
		/// and whose file names are derived from <paramref name="baseName"/>.
		/// </summary>
		public SettingsFile<T> SettingsFile<T>(string baseName = "settings") where T : class, new()
		{
			return new SettingsFile<T>(this, baseName);
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Initialize()
		{
			_current = Build();
		}

		/// <summary>
		/// Builds a fresh environment from the current process arguments. <see cref="Current"/> memoizes one
		/// of these for the lifetime of the domain; editor tooling calls this directly when it needs to pick
		/// up argument edits made since then. Main-thread only.
		/// </summary>
		internal static AppEnvironment Build()
		{
			var args = CommandLineArgs.FromProcess();
			string streamingAssetsPath = Application.streamingAssetsPath;

			string dataPath = args.TryGet("assetsPath", out string assetsPath) ? assetsPath : streamingAssetsPath;
			string machineId = args.TryGet("machineId", out string machineIdArg) ? machineIdArg : Environment.MachineName;

			return new AppEnvironment(dataPath, machineId, args);
		}
	}
}
