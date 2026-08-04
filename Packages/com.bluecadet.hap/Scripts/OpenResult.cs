namespace Bluecadet.Hap
{
    /// <summary>
    /// How an open attempt ended. Everything except <see cref="Success"/> means no video is
    /// playing; <see cref="Superseded"/> and <see cref="Cancelled"/> are outcomes rather than
    /// errors — they say another call, or the component's teardown, took over.
    /// </summary>
    public enum HapOpenStatus
    {
        /// <summary>The file is open and its frames are decoding.</summary>
        Success = 0,

        /// <summary>A later Open or Close call replaced this one before it finished.</summary>
        Superseded,

        /// <summary>The component was disabled or destroyed before the open finished.</summary>
        Cancelled,

        /// <summary>The path was empty, or the call came from a background thread.</summary>
        InvalidPath,

        /// <summary>Nothing exists at that path, or it could not be opened.</summary>
        FileNotFound,

        /// <summary>The file exists but could not be read.</summary>
        FileUnreadable,

        /// <summary>Not a QuickTime/MP4 file.</summary>
        NotAVideoFile,

        /// <summary>A video file, but with no Hap track in it.</summary>
        NoHapTrack,

        /// <summary>A Hap track in a variant this package cannot decode.</summary>
        UnsupportedFormat,

        /// <summary>The Hap track or its first frame is damaged.</summary>
        CorruptVideo,

        /// <summary>An allocation failed while opening.</summary>
        OutOfMemory,

        /// <summary>
        /// The video opened, but its GPU output could not be set up — the playback textures or
        /// the package's output shader. Nothing would reach the screen, so the open fails.
        /// </summary>
        GpuSetupFailed,
    }

    /// <summary>
    /// The outcome of <see cref="HapPlayer.OpenAsync"/>: whether the file is now playing and,
    /// if not, why. Failures are reported here rather than thrown.
    /// </summary>
    public readonly struct OpenResult
    {
        /// <summary>How the open attempt ended.</summary>
        public HapOpenStatus Status { get; }

        /// <summary>The path the player was asked to open.</summary>
        public string FilePath { get; }

        /// <summary>True when the video is open and ready to play.</summary>
        public bool Success => Status == HapOpenStatus.Success;

        public OpenResult(HapOpenStatus status, string filePath)
        {
            Status = status;
            FilePath = filePath;
        }

        public override string ToString() =>
            Success ? $"Opened '{FilePath}'" : $"{Status} opening '{FilePath}'";
    }

    internal static class HapOpenStatusExtensions
    {
        /// <summary>Map a native error onto the status a caller sees.</summary>
        public static HapOpenStatus ToOpenStatus(this HapError error) => error switch
        {
            HapError.Ok                 => HapOpenStatus.Success,
            HapError.InvalidArgument    => HapOpenStatus.InvalidPath,
            HapError.FileNotFound       => HapOpenStatus.FileNotFound,
            HapError.FileRead           => HapOpenStatus.FileUnreadable,
            HapError.NotAMov            => HapOpenStatus.NotAVideoFile,
            HapError.NoHapTrack         => HapOpenStatus.NoHapTrack,
            HapError.UnsupportedVariant => HapOpenStatus.UnsupportedFormat,
            HapError.OutOfMemory        => HapOpenStatus.OutOfMemory,
            // CorruptTrack, InvalidFrame, FrameOutOfRange, BufferTooSmall: the file parses as
            // a Hap track, but its contents do not hold up.
            _                           => HapOpenStatus.CorruptVideo,
        };
    }
}
