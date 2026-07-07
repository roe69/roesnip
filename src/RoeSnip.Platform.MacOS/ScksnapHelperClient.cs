using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.MacOS;

/// <summary>Thrown when macOS denies Screen Recording (TCC) permission — scksnap's distinct exit
/// code 82. This is a FIRST-CLASS, UI-surfaced error per DESIGN-XPLAT.md, not a generic capture
/// failure: the app should tell the user to grant Screen Recording to scksnap in
/// System Settings &gt; Privacy &amp; Security and retry, rather than silently showing zero frames.
/// (Not derived from <see cref="CaptureException"/> only because that Core type is sealed.)</summary>
public sealed class ScreenRecordingPermissionDeniedException : Exception
{
    public ScreenRecordingPermissionDeniedException(string message) : base(message) { }
}

/// <summary>One active display as reported by <c>scksnap list</c> (JSON). <see cref="X"/>/<see cref="Y"/>/
/// <see cref="WidthPx"/>/<see cref="HeightPx"/> are physical pixels in global desktop coordinates
/// (top-left origin); <see cref="Scale"/> is pixels-per-point; <see cref="EdrHeadroom"/> is the
/// current NSScreen max EDR component value (1.0 = SDR right now) and
/// <see cref="EdrPotentialHeadroom"/> the display's capability.</summary>
public sealed record ScksnapDisplay(
    uint Id,
    string Name,
    int X,
    int Y,
    int WidthPx,
    int HeightPx,
    double Scale,
    double EdrHeadroom,
    double EdrPotentialHeadroom,
    bool IsPrimary);

/// <summary>One decoded scksnap frame file. <see cref="FormatCode"/>: 1 = FP16 RGBA
/// extended-linear-sRGB (macOS EDR convention, 1.0 == SDR white), 2 = BGRA8 sRGB.</summary>
public sealed record ScksnapFrame(
    uint FormatCode,
    int Width,
    int Height,
    int Stride,
    uint DisplayId,
    RectPhysical BoundsPx,
    double Scale,
    double EdrHeadroom,
    bool IsPrimary,
    byte[] Pixels);

/// <summary>Shells out to the <c>scksnap</c> Swift helper binary (helpers/scksnap/ — built by the
/// build-scksnap GitHub Actions workflow, NOT on this machine) and parses its output. The CLI
/// contract, exit codes and the "SCKSNAP1" temp-file wire format are documented at the top of
/// helpers/scksnap/Sources/Scksnap.swift and in helpers/scksnap/README.md — keep all three in sync.</summary>
public sealed class ScksnapHelperClient
{
    /// <summary>scksnap's distinct exit code for a TCC Screen Recording denial.</summary>
    public const int TccDeniedExitCode = 82;

    private const string Magic = "SCKSNAP1";
    private const int MinHeaderSize = 96;
    private const int HelperTimeoutMs = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _helperPath;

    public ScksnapHelperClient() : this(LocateHelper()) { }

    public ScksnapHelperClient(string helperPath) => _helperPath = helperPath;

    /// <summary>Resolves the helper binary's path. <c>ROESNIP_SCKSNAP_PATH</c> overrides (testing);
    /// otherwise the helper ships next to the RoeSnip executable — <see cref="AppContext.BaseDirectory"/>
    /// is that directory both for a flat <c>dotnet publish</c> layout and inside an app bundle's
    /// <c>Contents/MacOS/</c> (PLAN-XPLAT.md §6 flag 8: the flat-sibling layout is the decision here,
    /// since a bare Avalonia publish is not a .app bundle; a future bundling step keeps working
    /// because BaseDirectory is Contents/MacOS in that layout too).</summary>
    public static string LocateHelper()
    {
        string? overridePath = Environment.GetEnvironmentVariable("ROESNIP_SCKSNAP_PATH");
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        return Path.Combine(AppContext.BaseDirectory, "scksnap");
    }

    /// <summary>Runs <c>scksnap list</c> and parses the JSON display array. Needs no TCC permission
    /// (the helper enumerates via CoreGraphics/NSScreen only). Throws <see cref="CaptureException"/>
    /// on any failure — callers treat that as "enumeration itself failed" (§2.3 contract).</summary>
    public IReadOnlyList<ScksnapDisplay> ListDisplays()
    {
        var (exitCode, stdout, stderr) = Run("list", null);
        if (exitCode != 0)
        {
            throw new CaptureException(
                $"scksnap list failed (exit {exitCode}): {Trimmed(stderr)}");
        }
        try
        {
            return JsonSerializer.Deserialize<List<ScksnapDisplay>>(stdout, JsonOptions)
                ?? new List<ScksnapDisplay>();
        }
        catch (JsonException ex)
        {
            throw new CaptureException("scksnap list returned unparseable JSON.", ex);
        }
    }

    /// <summary>Runs <c>scksnap capture &lt;displayId&gt;</c>, reads back and deletes the temp frame
    /// file. Throws <see cref="ScreenRecordingPermissionDeniedException"/> on exit code 82 (TCC),
    /// <see cref="CaptureException"/> on everything else.</summary>
    public ScksnapFrame Capture(uint displayId)
    {
        var (exitCode, stdout, stderr) = Run(
            "capture", displayId.ToString(CultureInfo.InvariantCulture));
        if (exitCode == TccDeniedExitCode)
        {
            throw new ScreenRecordingPermissionDeniedException(Trimmed(stderr));
        }
        if (exitCode != 0)
        {
            throw new CaptureException(
                $"scksnap capture {displayId} failed (exit {exitCode}): {Trimmed(stderr)}");
        }

        string path = stdout.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            throw new CaptureException(
                $"scksnap capture {displayId} exited 0 but printed no readable frame file path (got \"{path}\").");
        }
        try
        {
            return ParseFrameFile(path);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Decodes the "SCKSNAP1" wire format (96-byte little-endian header + raw pixel rows) —
    /// field table at the top of helpers/scksnap/Sources/Scksnap.swift.</summary>
    public static ScksnapFrame ParseFrameFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < MinHeaderSize ||
            Encoding.ASCII.GetString(bytes, 0, 8) != Magic)
        {
            throw new CaptureException($"scksnap frame file {path} has a bad or missing SCKSNAP1 header.");
        }

        var span = bytes.AsSpan();
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        uint formatCode = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        int width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[16..]));
        int height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[20..]));
        int stride = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[24..]));
        uint displayId = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
        int boundsX = BinaryPrimitives.ReadInt32LittleEndian(span[32..]);
        int boundsY = BinaryPrimitives.ReadInt32LittleEndian(span[36..]);
        int boundsW = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
        int boundsH = BinaryPrimitives.ReadInt32LittleEndian(span[44..]);
        double scale = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(span[48..]));
        double edrHeadroom = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(span[56..]));
        bool isPrimary = BinaryPrimitives.ReadUInt32LittleEndian(span[64..]) != 0;

        if (headerSize < MinHeaderSize || headerSize > bytes.Length)
        {
            throw new CaptureException($"scksnap frame file {path} reports invalid headerSize {headerSize}.");
        }
        if (width <= 0 || height <= 0 || stride <= 0)
        {
            throw new CaptureException(
                $"scksnap frame file {path} reports degenerate geometry {width}x{height} stride {stride}.");
        }
        long expectedPixelBytes = (long)stride * height;
        if (bytes.Length - headerSize < expectedPixelBytes)
        {
            throw new CaptureException(
                $"scksnap frame file {path} is truncated: expected {expectedPixelBytes} pixel bytes after the " +
                $"{headerSize}-byte header, found {bytes.Length - headerSize}.");
        }

        byte[] pixels = new byte[expectedPixelBytes];
        Array.Copy(bytes, (int)headerSize, pixels, 0, (int)expectedPixelBytes);

        return new ScksnapFrame(
            formatCode, width, height, stride, displayId,
            RectPhysical.FromSize(boundsX, boundsY, boundsW, boundsH),
            scale, edrHeadroom, isPrimary, pixels);
    }

    private (int ExitCode, string Stdout, string Stderr) Run(string verb, string? argument)
    {
        if (!File.Exists(_helperPath))
        {
            throw new CaptureException(
                $"scksnap helper not found at \"{_helperPath}\" — it is built by the build-scksnap " +
                "GitHub Actions workflow and must ship next to the RoeSnip executable " +
                "(or be pointed at via ROESNIP_SCKSNAP_PATH).");
        }

        var psi = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(verb);
        if (argument is not null) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                throw new CaptureException($"scksnap failed to start (\"{_helperPath}\").");
            }
        }
        catch (Exception ex) when (ex is not CaptureException)
        {
            throw new CaptureException($"scksnap failed to start (\"{_helperPath}\"): {ex.Message}", ex);
        }

        // Drain both streams concurrently (a synchronous double ReadToEnd can deadlock on full pipes).
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(HelperTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new CaptureException($"scksnap {verb} timed out after {HelperTimeoutMs / 1000}s.");
        }
        process.WaitForExit(); // flush the async stream readers
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string Trimmed(string stderr)
    {
        string text = stderr.Trim();
        return text.Length == 0 ? "(no stderr output)" : text;
    }
}
