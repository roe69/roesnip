using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.Linux;

/// <summary>Imports PipeWire DMA-BUF frames through EGL (EGL_EXT_image_dma_buf_import[_modifiers]) on
/// a surfaceless GLES 3 context and reads them back. Needed because GNOME 50's HDR formats only
/// work as DMA-BUF and NVIDIA offers only block-linear (tiled) modifiers, which no CPU mmap can
/// untile. Every GL/EGL call runs on one dedicated thread (a context is current on one thread).
///
/// Each stream gets a <see cref="Target"/>: an RGBA16F texture/FBO the newest frame is copied into
/// (one GPU draw, so the PipeWire buffer is requeued at once and never pinned). A capture reads that
/// texture back, so a snapshot costs one readback, not one per damage frame.</summary>
internal sealed unsafe class DmaBufGl : IDisposable
{
    private const string EglLib = "libEGL.so.1", GlLib = "libGLESv2.so.2";

    // EGL
    private const int EglNoSurface = 0, EglOpenGlEsApi = 0x30A0, EglNone = 0x3038;
    private const int EglExtensions = 0x3055, EglRenderableType = 0x3040, EglOpenGlEs3Bit = 0x40, EglSurfaceType = 0x3033;
    private const int EglContextClientVersion = 0x3098, EglLinuxDmaBuf = 0x3270, EglWidth = 0x3057, EglHeight = 0x3056;
    private const int EglFourcc = 0x3271, EglPlane0Fd = 0x3272, EglPlane0Offset = 0x3273, EglPlane0Pitch = 0x3274;
    private const int EglModLo = 0x3443, EglModHi = 0x3444;
    private const int PlatformSurfaceless = 0x31DD, PlatformDevice = 0x313F;
    private const int EglDrmDeviceFile = 0x3233, EglDrmRenderNodeFile = 0x3377;

    // GL
    private const int Texture2D = 0x0DE1, Rgba16F = 0x881A, Rgba = 0x1908, HalfFloat = 0x140B, Float = 0x1406;
    private const int Framebuffer = 0x8D40, ColorAttachment0 = 0x8CE0, FramebufferComplete = 0x8CD5;
    private const int TextureMinFilter = 0x2801, TextureMagFilter = 0x2800, Nearest = 0x2600;
    private const int TextureWrapS = 0x2802, TextureWrapT = 0x2803, ClampToEdge = 0x812F;
    private const int FragmentShader = 0x8B30, VertexShader = 0x8B31, CompileStatus = 0x8B81, LinkStatus = 0x8B82;
    private const int ReadFormat = 0x8B9B, ReadType = 0x8B9A, Triangles = 4;

    /// <summary>DRM_FORMAT_ABGR16161616F: memory order R,G,B,A half floats (SPA RGBA_F16).</summary>
    public const uint FourccAbgr16161616F = 0x48344241;

    /// <summary>DRM_FORMAT_XRGB8888 / ARGB8888: memory order B,G,R,x/A (SPA BGRx / BGRA).</summary>
    public const uint FourccXrgb8888 = 0x34325258, FourccArgb8888 = 0x34325241;
    private const int Rgba8 = 0x8058, UnsignedByte = 0x1401;
    public const ulong ModifierInvalid = 0x00FFFFFFFFFFFFFFUL;

    private readonly BlockingCollection<Job> _jobs = new();
    private readonly Thread _thread;
    private IntPtr _display, _context;
    private string _initError = "";
    private bool _ready;
    private int _disposed;

    private IntPtr _fnCreateImage, _fnDestroyImage, _fnTargetTexture, _fnQueryModifiers;
    private uint _program, _srcLocation;
    private bool _readHalf;

    public string DisplayKind { get; private set; } = "";

    private sealed class Job
    {
        public Action Run = null!;
        public ManualResetEventSlim Done = new(false);
        public Exception? Error;
    }

    private readonly HashSet<string> _preferredDrmFiles;

    private DmaBufGl(IEnumerable<string> preferredDrmFiles)
    {
        _preferredDrmFiles = new HashSet<string>(preferredDrmFiles);
        _thread = new Thread(Loop) { IsBackground = true, Name = "RoeSnip-DmaBufGl" };
    }

    /// <summary>Opens EGL/GLES on its thread. Null with <paramref name="why"/> when the driver cannot
    /// import DMA-BUFs.</summary>
    public static DmaBufGl? TryCreate(IEnumerable<string> preferredDrmFiles, out string why)
    {
        why = "";
        if (!NativeLibrary.TryLoad(EglLib, out _) || !NativeLibrary.TryLoad(GlLib, out _))
        {
            why = "libEGL/libGLESv2 not found";
            return null;
        }
        var gl = new DmaBufGl(preferredDrmFiles);
        var init = new Job { Run = gl.Init };
        gl._thread.Start();
        gl._jobs.Add(init);
        if (!init.Done.Wait(TimeSpan.FromSeconds(10)))
        {
            why = "EGL init timed out";
            return null;
        }
        if (init.Error is not null || !gl._ready)
        {
            why = gl._initError.Length > 0 ? gl._initError : init.Error?.Message ?? "EGL init failed";
            gl.Dispose();
            return null;
        }
        return gl;
    }

    private void Loop()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job.Run(); }
            catch (Exception ex) { job.Error = ex; }
            job.Done.Set();
        }
    }

    /// <summary>Runs <paramref name="action"/> on the GL thread and waits; rethrows its exception.</summary>
    public void Invoke(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(DmaBufGl));
        var job = new Job { Run = action };
        _jobs.Add(job);
        job.Done.Wait();
        if (job.Error is not null) throw new InvalidOperationException(job.Error.Message, job.Error);
    }

    private void Init()
    {
        // The GPU that drives the monitors comes first: mutter's modifier list is that GPU's, and a
        // different GPU's EGL (Mesa on an iGPU next to an NVIDIA card) shares no tiled modifier with it.
        var candidates = new List<(string Kind, Func<IntPtr> Get)>();
        var others = new List<(string Kind, Func<IntPtr> Get)>();
        IntPtr queryDevices = eglGetProcAddress("eglQueryDevicesEXT");
        IntPtr queryDeviceString = eglGetProcAddress("eglQueryDeviceStringEXT");
        if (queryDevices != IntPtr.Zero)
        {
            var fn = (delegate* unmanaged[Cdecl]<int, IntPtr*, int*, int>)queryDevices;
            IntPtr* devices = stackalloc IntPtr[8];
            int count = 0;
            if (fn(8, devices, &count) != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr device = devices[i];
                    string file = "";
                    if (queryDeviceString != IntPtr.Zero)
                    {
                        var qs = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)queryDeviceString;
                        file = Marshal.PtrToStringAnsi(qs(device, EglDrmDeviceFile)) ?? "";
                        string render = Marshal.PtrToStringAnsi(qs(device, EglDrmRenderNodeFile)) ?? "";
                        if (_preferredDrmFiles.Contains(file) || _preferredDrmFiles.Contains(render))
                        {
                            candidates.Add(($"device {i} {file}", () => eglGetPlatformDisplay(PlatformDevice, device, null)));
                            continue;
                        }
                    }
                    others.Add(($"device {i} {file}", () => eglGetPlatformDisplay(PlatformDevice, device, null)));
                }
            }
        }
        candidates.Add(("surfaceless", () => eglGetPlatformDisplay(PlatformSurfaceless, IntPtr.Zero, null)));
        candidates.AddRange(others);
        candidates.Add(("default", () => eglGetDisplay(IntPtr.Zero)));

        var errors = new List<string>();
        foreach (var (kind, get) in candidates)
        {
            IntPtr dpy;
            try { dpy = get(); }
            catch (Exception ex) { errors.Add($"{kind}: {ex.Message}"); continue; }
            if (dpy == IntPtr.Zero) { errors.Add($"{kind}: no display"); continue; }
            string? err = TrySetUp(dpy);
            if (err is null)
            {
                DisplayKind = kind;
                _ready = true;
                return;
            }
            errors.Add($"{kind}: {err}");
        }
        _initError = string.Join("; ", errors);
    }

    private string? TrySetUp(IntPtr dpy)
    {
        if (eglInitialize(dpy, out _, out _) == 0) return "eglInitialize failed";
        string exts = Marshal.PtrToStringAnsi(eglQueryString(dpy, EglExtensions)) ?? "";
        if (!exts.Contains("EGL_EXT_image_dma_buf_import")) return "no EGL_EXT_image_dma_buf_import";
        if (eglBindAPI(EglOpenGlEsApi) == 0) return "eglBindAPI failed";

        int* cfgAttr = stackalloc int[] { EglRenderableType, EglOpenGlEs3Bit, EglSurfaceType, 0, EglNone };
        IntPtr config;
        int n;
        if (eglChooseConfig(dpy, cfgAttr, &config, 1, &n) == 0 || n < 1)
        {
            int* loose = stackalloc int[] { EglRenderableType, EglOpenGlEs3Bit, EglNone };
            if (eglChooseConfig(dpy, loose, &config, 1, &n) == 0 || n < 1) return "no GLES3 config";
        }
        int* ctxAttr = stackalloc int[] { EglContextClientVersion, 3, EglNone };
        IntPtr ctx = eglCreateContext(dpy, config, IntPtr.Zero, ctxAttr);
        if (ctx == IntPtr.Zero) return "eglCreateContext failed";
        if (eglMakeCurrent(dpy, EglNoSurface, EglNoSurface, ctx) == 0)
        {
            eglDestroyContext(dpy, ctx);
            return "eglMakeCurrent (surfaceless) failed";
        }

        _fnCreateImage = eglGetProcAddress("eglCreateImageKHR");
        _fnDestroyImage = eglGetProcAddress("eglDestroyImageKHR");
        _fnTargetTexture = eglGetProcAddress("glEGLImageTargetTexture2DOES");
        _fnQueryModifiers = eglGetProcAddress("eglQueryDmaBufModifiersEXT");
        if (_fnCreateImage == IntPtr.Zero || _fnDestroyImage == IntPtr.Zero || _fnTargetTexture == IntPtr.Zero)
        {
            eglMakeCurrent(dpy, EglNoSurface, EglNoSurface, IntPtr.Zero);
            eglDestroyContext(dpy, ctx);
            return "EGLImage entry points missing";
        }
        _display = dpy;
        _context = ctx;
        string? shader = BuildProgram();
        if (shader is not null)
        {
            eglMakeCurrent(dpy, EglNoSurface, EglNoSurface, IntPtr.Zero);
            eglDestroyContext(dpy, ctx);
            _display = _context = IntPtr.Zero;
            return shader;
        }
        return null;
    }

    private string? BuildProgram()
    {
        const string vs = "#version 300 es\nvoid main(){vec2 p=vec2(float((gl_VertexID<<1)&2),float(gl_VertexID&2));" +
            "gl_Position=vec4(p*2.0-1.0,0.0,1.0);}";
        const string fs = "#version 300 es\nprecision highp float;uniform sampler2D src;out vec4 o;" +
            "void main(){o=texelFetch(src,ivec2(gl_FragCoord.xy),0);}";
        uint v = Compile(VertexShader, vs, out string? e1);
        uint f = Compile(FragmentShader, fs, out string? e2);
        if (e1 is not null || e2 is not null) return "shader: " + (e1 ?? e2);
        uint p = glCreateProgram();
        glAttachShader(p, v);
        glAttachShader(p, f);
        glLinkProgram(p);
        int ok;
        glGetProgramiv(p, LinkStatus, &ok);
        if (ok == 0) return "shader link failed";
        _program = p;
        _srcLocation = (uint)glGetUniformLocation(p, "src");
        return null;
    }

    private static uint Compile(int type, string source, out string? error)
    {
        error = null;
        uint s = glCreateShader(type);
        byte[] bytes = Encoding.ASCII.GetBytes(source);
        fixed (byte* b = bytes)
        {
            IntPtr ptr = (IntPtr)b;
            int len = bytes.Length;
            glShaderSource(s, 1, &ptr, &len);
        }
        glCompileShader(s);
        int ok;
        glGetShaderiv(s, CompileStatus, &ok);
        if (ok == 0)
        {
            byte* log = stackalloc byte[512];
            int n;
            glGetShaderInfoLog(s, 512, &n, log);
            error = "compile failed: " + Encoding.ASCII.GetString(log, Math.Max(0, n));
        }
        return s;
    }

    /// <summary>Modifiers EGL can import for <paramref name="fourcc"/> as a plain 2D texture (not
    /// external-only), in the driver's order. Empty when it cannot import the format.</summary>
    public IReadOnlyList<ulong> ModifiersFor(uint fourcc)
    {
        var result = new List<ulong>();
        Invoke(() =>
        {
            if (_fnQueryModifiers == IntPtr.Zero) return;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, int, int, ulong*, int*, int*, int>)_fnQueryModifiers;
            int count = 0;
            if (fn(_display, (int)fourcc, 0, null, null, &count) == 0 || count <= 0) return;
            var mods = new ulong[count];
            var external = new int[count];
            fixed (ulong* m = mods)
            fixed (int* x = external)
            {
                if (fn(_display, (int)fourcc, count, m, x, &count) == 0) return;
            }
            for (int i = 0; i < count; i++)
            {
                if (external[i] == 0) result.Add(mods[i]);
            }
        });
        return result;
    }

    /// <summary>A GPU-resident copy of the newest frame of one stream.</summary>
    public sealed class Target
    {
        internal uint Texture, Fbo, SrcTexture;
        public int Width { get; internal set; }
        public int Height { get; internal set; }
    }

    /// <summary>Creates a target of the given size, or throws with the reason. GL thread only.</summary>
    public Target CreateTarget(int width, int height, bool eightBit = false)
    {
        Target? t = null;
        Invoke(() =>
        {
            var target = new Target { Width = width, Height = height };
            uint tex, fbo, src;
            glGenTextures(1, &tex);
            glBindTexture(Texture2D, tex);
            glTexParameteri(Texture2D, TextureMinFilter, Nearest);
            glTexParameteri(Texture2D, TextureMagFilter, Nearest);
            glTexParameteri(Texture2D, TextureWrapS, ClampToEdge);
            glTexParameteri(Texture2D, TextureWrapT, ClampToEdge);
            if (eightBit) glTexImage2D(Texture2D, 0, Rgba8, width, height, 0, Rgba, UnsignedByte, IntPtr.Zero);
            else glTexImage2D(Texture2D, 0, Rgba16F, width, height, 0, Rgba, HalfFloat, IntPtr.Zero);
            glGenFramebuffers(1, &fbo);
            glBindFramebuffer(Framebuffer, fbo);
            glFramebufferTexture2D(Framebuffer, ColorAttachment0, Texture2D, tex, 0);
            int status = glCheckFramebufferStatus(Framebuffer);
            if (status != FramebufferComplete)
            {
                glDeleteFramebuffers(1, &fbo);
                glDeleteTextures(1, &tex);
                throw new InvalidOperationException($"RGBA16F framebuffer incomplete (0x{status:X})");
            }
            glGenTextures(1, &src);
            glBindTexture(Texture2D, src);
            glTexParameteri(Texture2D, TextureMinFilter, Nearest);
            glTexParameteri(Texture2D, TextureMagFilter, Nearest);
            glTexParameteri(Texture2D, TextureWrapS, ClampToEdge);
            glTexParameteri(Texture2D, TextureWrapT, ClampToEdge);
            if (!eightBit)
            {
                int fmt, type;
                glGetIntegerv(ReadFormat, &fmt);
                glGetIntegerv(ReadType, &type);
                _readHalf = fmt == Rgba && type == HalfFloat;
            }
            target.Texture = tex;
            target.Fbo = fbo;
            target.SrcTexture = src;
            t = target;
        });
        return t!;
    }

    public void DestroyTarget(Target t)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            Invoke(() =>
            {
                uint tex = t.Texture, fbo = t.Fbo, src = t.SrcTexture;
                glDeleteFramebuffers(1, &fbo);
                glDeleteTextures(1, &tex);
                glDeleteTextures(1, &src);
            });
        }
        catch { /* context gone */ }
    }

    /// <summary>Copies a DMA-BUF frame (R,G,B,A half floats) into <paramref name="t"/>. Returns null
    /// on success, otherwise the reason.</summary>
    public string? CopyFrame(Target t, int fd, int offset, int stride, ulong modifier, uint fourcc = FourccAbgr16161616F)
    {
        string? error = null;
        Invoke(() => error = CopyFrameOnThread(t, fd, offset, stride, modifier, fourcc));
        return error;
    }

    private string? CopyFrameOnThread(Target t, int fd, int offset, int stride, ulong modifier, uint fourcc)
    {
        int* attrs = stackalloc int[24];
        int i = 0;
        attrs[i++] = EglWidth; attrs[i++] = t.Width;
        attrs[i++] = EglHeight; attrs[i++] = t.Height;
        attrs[i++] = EglFourcc; attrs[i++] = (int)fourcc;
        attrs[i++] = EglPlane0Fd; attrs[i++] = fd;
        attrs[i++] = EglPlane0Offset; attrs[i++] = offset;
        attrs[i++] = EglPlane0Pitch; attrs[i++] = stride;
        if (modifier != ModifierInvalid)
        {
            attrs[i++] = EglModLo; attrs[i++] = (int)(uint)modifier;
            attrs[i++] = EglModHi; attrs[i++] = (int)(uint)(modifier >> 32);
        }
        attrs[i++] = EglNone;

        var create = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, int*, IntPtr>)_fnCreateImage;
        IntPtr image = create(_display, IntPtr.Zero, EglLinuxDmaBuf, IntPtr.Zero, attrs);
        if (image == IntPtr.Zero) return $"eglCreateImage failed (EGL error 0x{eglGetError():X})";
        try
        {
            glBindTexture(Texture2D, t.SrcTexture);
            ((delegate* unmanaged[Cdecl]<int, IntPtr, void>)_fnTargetTexture)(Texture2D, image);
            glBindFramebuffer(Framebuffer, t.Fbo);
            glViewport(0, 0, t.Width, t.Height);
            glUseProgram(_program);
            glActiveTexture(0x84C0);
            glBindTexture(Texture2D, t.SrcTexture);
            glUniform1i((int)_srcLocation, 0);
            glDrawArrays(Triangles, 0, 3);
            int err = glGetError();
            if (err != 0) return $"GL error 0x{err:X} drawing the imported frame";
            glFinish(); // the PipeWire buffer is requeued right after: the GPU must be done reading it
            return null;
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)_fnDestroyImage)(_display, image);
        }
    }

    /// <summary>Reads the block at (x, y) of an 8-bit target as tight R,G,B,A bytes into
    /// <paramref name="dest"/>. Null on success, else the reason.</summary>
    public string? ReadBytes(Target t, int x, int y, int width, int height, byte[] dest)
    {
        string? error = null;
        Invoke(() =>
        {
            glBindFramebuffer(Framebuffer, t.Fbo);
            glPixelStorei(0x0D05, 1); // GL_PACK_ALIGNMENT
            fixed (byte* d = dest) glReadPixels(x, y, width, height, Rgba, UnsignedByte, (IntPtr)d);
            int err = glGetError();
            if (err != 0) error = $"GL error 0x{err:X} reading the frame back";
        });
        return error;
    }

    /// <summary>Reads the <paramref name="width"/> x <paramref name="height"/> block at (x, y) of the
    /// target as tight half-float RGBA into <paramref name="dest"/>. Null on success, else the reason.</summary>
    public string? ReadHalf(Target t, int x, int y, int width, int height, byte[] dest)
    {
        string? error = null;
        Invoke(() =>
        {
            glBindFramebuffer(Framebuffer, t.Fbo);
            glPixelStorei(0x0D05, 1); // GL_PACK_ALIGNMENT
            fixed (byte* d = dest)
            {
                if (_readHalf)
                {
                    glReadPixels(x, y, width, height, Rgba, HalfFloat, (IntPtr)d);
                }
                else
                {
                    // The driver only reads RGBA16F as float: read a band of rows at a time.
                    int band = Math.Max(1, (8 << 20) / (width * 16));
                    var tmp = new float[width * 4 * Math.Min(band, height)];
                    fixed (float* f = tmp)
                    {
                        for (int row = 0; row < height; row += band)
                        {
                            int rows = Math.Min(band, height - row);
                            glReadPixels(x, y + row, width, rows, Rgba, Float, (IntPtr)f);
                            var halves = (Half*)(d + (long)row * width * 8);
                            for (int k = 0; k < width * 4 * rows; k++) halves[k] = (Half)f[k];
                        }
                    }
                }
            }
            int err = glGetError();
            if (err != 0) error = $"GL error 0x{err:X} reading the frame back";
        });
        return error;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ready)
        {
            var job = new Job
            {
                Run = () =>
                {
                    eglMakeCurrent(_display, EglNoSurface, EglNoSurface, IntPtr.Zero);
                    if (_context != IntPtr.Zero) eglDestroyContext(_display, _context);
                },
            };
            try
            {
                _jobs.Add(job);
                job.Done.Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* thread gone */ }
        }
        _jobs.CompleteAdding();
    }

    [DllImport(EglLib)] private static extern IntPtr eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(EglLib)] private static extern IntPtr eglGetPlatformDisplay(int platform, IntPtr nativeDisplay, IntPtr* attribs);
    [DllImport(EglLib)] private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport(EglLib)] private static extern int eglInitialize(IntPtr dpy, out int major, out int minor);
    [DllImport(EglLib)] private static extern IntPtr eglQueryString(IntPtr dpy, int name);
    [DllImport(EglLib)] private static extern int eglBindAPI(int api);
    [DllImport(EglLib)] private static extern int eglChooseConfig(IntPtr dpy, int* attribs, IntPtr* configs, int size, int* count);
    [DllImport(EglLib)] private static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int* attribs);
    [DllImport(EglLib)] private static extern int eglDestroyContext(IntPtr dpy, IntPtr ctx);
    [DllImport(EglLib)] private static extern int eglMakeCurrent(IntPtr dpy, int draw, int read, IntPtr ctx);
    [DllImport(EglLib)] private static extern int eglGetError();

    [DllImport(GlLib)] private static extern void glGenTextures(int n, uint* textures);
    [DllImport(GlLib)] private static extern void glDeleteTextures(int n, uint* textures);
    [DllImport(GlLib)] private static extern void glBindTexture(int target, uint texture);
    [DllImport(GlLib)] private static extern void glActiveTexture(int texture);
    [DllImport(GlLib)] private static extern void glTexParameteri(int target, int pname, int param);
    [DllImport(GlLib)] private static extern void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr data);
    [DllImport(GlLib)] private static extern void glGenFramebuffers(int n, uint* fbos);
    [DllImport(GlLib)] private static extern void glDeleteFramebuffers(int n, uint* fbos);
    [DllImport(GlLib)] private static extern void glBindFramebuffer(int target, uint fbo);
    [DllImport(GlLib)] private static extern void glFramebufferTexture2D(int target, int attachment, int texTarget, uint texture, int level);
    [DllImport(GlLib)] private static extern int glCheckFramebufferStatus(int target);
    [DllImport(GlLib)] private static extern void glViewport(int x, int y, int w, int h);
    [DllImport(GlLib)] private static extern void glUseProgram(uint program);
    [DllImport(GlLib)] private static extern void glDrawArrays(int mode, int first, int count);
    [DllImport(GlLib)] private static extern void glReadPixels(int x, int y, int w, int h, int format, int type, IntPtr data);
    [DllImport(GlLib)] private static extern void glPixelStorei(int pname, int param);
    [DllImport(GlLib)] private static extern void glFinish();
    [DllImport(GlLib)] private static extern int glGetError();
    [DllImport(GlLib)] private static extern void glGetIntegerv(int pname, int* data);
    [DllImport(GlLib)] private static extern uint glCreateShader(int type);
    [DllImport(GlLib)] private static extern void glShaderSource(uint shader, int count, IntPtr* strings, int* lengths);
    [DllImport(GlLib)] private static extern void glCompileShader(uint shader);
    [DllImport(GlLib)] private static extern void glGetShaderiv(uint shader, int pname, int* value);
    [DllImport(GlLib)] private static extern void glGetShaderInfoLog(uint shader, int max, int* length, byte* log);
    [DllImport(GlLib)] private static extern uint glCreateProgram();
    [DllImport(GlLib)] private static extern void glAttachShader(uint program, uint shader);
    [DllImport(GlLib)] private static extern void glLinkProgram(uint program);
    [DllImport(GlLib)] private static extern void glGetProgramiv(uint program, int pname, int* value);
    [DllImport(GlLib)] private static extern int glGetUniformLocation(uint program, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(GlLib)] private static extern void glUniform1i(int location, int value);
}
