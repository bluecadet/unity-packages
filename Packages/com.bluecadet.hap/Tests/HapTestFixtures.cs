using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Hap.Tests
{
    /// <summary>
    /// Shared scaffolding for the Hap suites: the sample files the native tests decode, the
    /// block-size arithmetic their sizes come from, the main-thread pump the player tests run
    /// on, and the unmanaged buffer the decoder writes into.
    ///
    /// The fixture files live next to the plugin sources in <c>Native~/tests/fixtures</c>, so
    /// the same files back both the Zig and the C# suites.
    /// </summary>
    internal static class HapTestFixtures
    {
        static string s_dir;

        /// <summary>Absolute path of the fixtures directory.</summary>
        public static string Dir => s_dir ??= ResolveDir();

        /// <summary>
        /// How long any test waits for something it cannot poll cheaply: an open that reads a
        /// file, a decode thread parking, or a worker thread finishing. Generous on purpose —
        /// it only ever bounds a failure, never a passing run.
        /// </summary>
        public const int TimeoutMs = 15_000;

        // The fixture videos are all 640x360.
        public const int Width = 640;
        public const int Height = 360;

        public static readonly int Dxt1Size = BlockBytes(HapFormat.DXT1, Width, Height);
        public static readonly int Dxt5Size = BlockBytes(HapFormat.DXT5, Width, Height);

        public static string Hap1 => Path.Combine(Dir, "hap1.mov");
        public static string Hap1Chunked => Path.Combine(Dir, "hap1_chunked.mov");
        public static string Hap1Audio => Path.Combine(Dir, "hap1_audio.mov");
        public static string Hap5 => Path.Combine(Dir, "hap5.mov");
        public static string Hap5Chunked => Path.Combine(Dir, "hap5_chunked.mov");
        public static string HapY => Path.Combine(Dir, "hapy.mov");
        public static string HapYChunked => Path.Combine(Dir, "hapy_chunked.mov");
        public static string Hap7 => Path.Combine(Dir, "hap7.mov");
        public static string HapM => Path.Combine(Dir, "hapm.mov");
        public static string HapMGoldenTex0 => Path.Combine(Dir, "hapm_golden_tex0.bin");
        public static string HapMGoldenTex1 => Path.Combine(Dir, "hapm_golden_tex1.bin");

        /// <summary>Inputs that once crashed or leaked in the plugin's fuzzers.</summary>
        public static string FuzzRegression(string name) => Path.Combine(Dir, "fuzz_regressions", name);

        public static string[] FuzzRegressions => Directory.Exists(Path.Combine(Dir, "fuzz_regressions"))
            ? Directory.GetFiles(Path.Combine(Dir, "fuzz_regressions"), "*.bin")
            : new string[0];

        /// <summary>Skip the calling test when a fixture is missing rather than failing it.</summary>
        public static void Require(string path) =>
            Assume.That(File.Exists(path), "Test fixture not found: " + path);

        // ── Block-compressed sizes ───────────────────────────────────────────

        /// <summary>
        /// Decoded byte size of one block-compressed texture, the same arithmetic the plugin
        /// reports: one block per 4x4 pixels, 8 bytes for the single-channel/1-bit-alpha
        /// layouts and 16 for the rest.
        /// </summary>
        public static int BlockBytes(HapFormat format, int width, int height)
        {
            int blocks = ((width + 3) / 4) * ((height + 3) / 4);
            int bytesPerBlock = format is HapFormat.DXT1 or HapFormat.RGTC1 ? 8 : 16;
            return blocks * bytesPerBlock;
        }

        // ── Main-thread pumping ──────────────────────────────────────────────

        /// <summary>
        /// One turn of the main-thread work the player depends on. Edit mode never runs
        /// MonoBehaviour Update, so the tests drive the same loop the runtime does, and give
        /// the decode thread a moment to make progress.
        /// </summary>
        public static void Pump()
        {
            HapMainLoop.Tick();
            Thread.Sleep(1);
        }

        /// <summary>Pump a fixed number of times to let work in flight settle.</summary>
        public static void Pump(int ticks)
        {
            for (int i = 0; i < ticks; i++) Pump();
        }

        /// <summary>
        /// Poll <paramref name="condition"/> until it holds or the timeout expires, running
        /// <paramref name="step"/> between attempts — <see cref="Pump"/> by default, since most
        /// waits are on work the main thread has to run itself. Returns whether it held.
        /// </summary>
        public static bool PollUntil(Func<bool> condition, Action step = null, int timeoutMs = TimeoutMs)
        {
            step ??= Pump;
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds >= timeoutMs) return false;
                step();
            }
            return true;
        }

        /// <summary>Pump until an open completes, and hand back its typed result.</summary>
        public static OpenResult Await(Awaitable<OpenResult> awaitable,
                                       string message = "the open never completed")
        {
            var awaiter = awaitable.GetAwaiter();
            Assert.That(PollUntil(() => awaiter.IsCompleted), Is.True, message);
            return awaiter.GetResult();
        }

        /// <summary>Pump until a close completes.</summary>
        public static void Await(Awaitable awaitable, string message = "the close never completed")
        {
            var awaiter = awaitable.GetAwaiter();
            Assert.That(PollUntil(() => awaiter.IsCompleted), Is.True, message);
            awaiter.GetResult();
        }

        /// <summary>
        /// Expect the one message the player logs for an open it could not complete: a warning
        /// when a caller is awaiting the typed result, an error when nobody is.
        /// </summary>
        public static void ExpectOpenFailureLog(LogType logType = LogType.Warning) =>
            LogAssert.Expect(logType, new Regex("Could not open"));

        // ── Native buffers ───────────────────────────────────────────────────

        /// <summary>Unmanaged memory for the decoder to write into, freed with the using block.</summary>
        public static NativeBuffer NativeBuffer(int size) => new(size);

        /// <summary>
        /// Decode one texture of one frame into a fresh byte array, failing the test if the
        /// native decoder refuses it.
        /// </summary>
        public static byte[] DecodeToBytes(IntPtr handle, int frame, int textureIndex, int size)
        {
            using var buffer = NativeBuffer(size);
            Assert.That(HapNative.DecodeTexture(handle, frame, textureIndex, buffer.Ptr, buffer.Size),
                Is.EqualTo(HapError.Ok), $"decode of texture {textureIndex}, frame {frame}");
            return buffer.ToArray();
        }

        static string ResolveDir()
        {
            var package = PackageInfo.FindForAssembly(typeof(HapPlayer).Assembly);
            if (package != null)
                return Path.GetFullPath(Path.Combine(package.resolvedPath, "Native~", "tests", "fixtures"));

            // Fallback for a package embedded directly under the project's Packages folder.
            return Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "../Packages/com.bluecadet.hap/Native~/tests/fixtures"));
        }
    }

    /// <summary>
    /// A block of unmanaged memory to hand the native decoder, released when its using block
    /// ends. The decoder writes straight into it, exactly as the texture ring's raw pointers.
    /// </summary>
    internal readonly struct NativeBuffer : IDisposable
    {
        /// <summary>Pointer to pass to the native API.</summary>
        public readonly IntPtr Ptr;

        /// <summary>Byte capacity, to pass alongside the pointer.</summary>
        public readonly int Size;

        public NativeBuffer(int size)
        {
            Size = size;
            Ptr = Marshal.AllocHGlobal(size);
        }

        /// <summary>Copy the buffer's bytes out to managed memory.</summary>
        public byte[] ToArray()
        {
            var bytes = new byte[Size];
            Marshal.Copy(Ptr, bytes, 0, Size);
            return bytes;
        }

        public void Dispose() => Marshal.FreeHGlobal(Ptr);
    }

    /// <summary>
    /// Base for the fixtures that drive a <see cref="HapPlayer"/> component: a fresh player on
    /// its own GameObject for each test, cleaned up afterwards even when the test destroyed it
    /// itself.
    /// </summary>
    public abstract class HapPlayerTestFixture
    {
        /// <summary>The GameObject carrying <see cref="Player"/>, or null once it is destroyed.</summary>
        protected GameObject Host;

        protected HapPlayer Player;

        [SetUp]
        public void CreatePlayer()
        {
            Host = new GameObject(GetType().Name);
            Player = Host.AddComponent<HapPlayer>();
        }

        [TearDown]
        public void DestroyPlayer()
        {
            if (Host != null)
                DestroyHost();
        }

        /// <summary>
        /// Destroy the player mid-test and forget it, so the teardown does not touch it again.
        /// </summary>
        protected void DestroyHost()
        {
            UnityEngine.Object.DestroyImmediate(Host);
            Host = null;
            Player = null;
        }
    }
}
