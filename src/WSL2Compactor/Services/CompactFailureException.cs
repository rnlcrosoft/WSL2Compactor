namespace WSL2Compactor.Services;

internal enum CompactFailureKind
{
    Locked,
    Missing,
    AccessDenied,
    Canceled,
    Backend,
    CommandFailed,
    Unknown
}

internal sealed class CompactFailureException : Exception
{
    public CompactFailureException(
        CompactFailureKind kind,
        string phase,
        string message,
        string? distro = null,
        string? backend = null,
        string? vhdPath = null,
        int? exitCode = null,
        uint? win32ErrorCode = null,
        bool fallbackAllowed = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Phase = phase;
        Distro = distro;
        Backend = backend;
        VhdPath = vhdPath;
        ExitCode = exitCode;
        Win32ErrorCode = win32ErrorCode;
        FallbackAllowed = fallbackAllowed;
    }

    public CompactFailureKind Kind { get; }

    public string Phase { get; }

    public string? Distro { get; }

    public string? Backend { get; }

    public string? VhdPath { get; }

    public int? ExitCode { get; }

    public uint? Win32ErrorCode { get; }

    public bool FallbackAllowed { get; }

    public static CompactFailureKind ClassifyWin32(uint errorCode)
        => errorCode switch
        {
            2 or 3 => CompactFailureKind.Missing,
            5 => CompactFailureKind.AccessDenied,
            32 or 33 => CompactFailureKind.Locked,
            995 or 1223 => CompactFailureKind.Canceled,
            _ => CompactFailureKind.Backend
        };

    public static bool IsFallbackSafe(CompactFailureKind kind)
        => kind is CompactFailureKind.Backend or CompactFailureKind.CommandFailed or CompactFailureKind.Unknown;

    public static CompactFailureException FromWin32(
        uint errorCode,
        string phase,
        string message,
        string? distro = null,
        string? backend = null,
        string? vhdPath = null,
        bool fallbackAllowed = true,
        Exception? innerException = null)
    {
        var kind = ClassifyWin32(errorCode);
        return new CompactFailureException(
            kind,
            phase,
            message,
            distro,
            backend,
            vhdPath,
            win32ErrorCode: errorCode,
            fallbackAllowed: fallbackAllowed && IsFallbackSafe(kind),
            innerException: innerException);
    }
}
