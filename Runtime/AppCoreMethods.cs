using ForkAppCore = UltralightNet.AppCore.AppCoreMethods;

namespace Engine;

/// <summary>
/// Thin wrapper around UltralightNet.AppCore.AppCoreMethods (from the local 1.4 fork).
/// Registers native platform implementations (font loader, file system, logger)
/// via the AppCore native library.
/// </summary>
/// <remarks>
/// All public methods lazily initialize the native library loader on first call via
/// <see cref="EnsureNativeLibsLoaded"/>, which pre-loads Ultralight native libraries
/// in the correct dependency order and registers DllImport resolvers.
/// On Linux, a <c>libbz2.so.1.0</c> compatibility symlink is created automatically.
/// </remarks>
/// <seealso cref="NativeLibraryLoader"/>
/// <seealso cref="WebViewInstance"/>
internal static class AppCoreMethods
{
    private static readonly ILogger Logger = Log.Category("Engine.WebView");
    private static NativeLibraryLoader? _loader;

    /// <summary>Registers the AppCore platform font loader (calls the native <c>ulEnablePlatformFontLoader</c>).</summary>
    public static void SetPlatformFontLoader()
    {
        EnsureNativeLibsLoaded();
        ForkAppCore.SetPlatformFontLoader();
    }

    /// <summary>Registers the AppCore platform file system rooted at <paramref name="baseDirectory"/>.</summary>
    /// <param name="baseDirectory">The base directory for file:// URL resolution.</param>
    public static void SetPlatformFileSystem(string baseDirectory)
    {
        EnsureNativeLibsLoaded();
        ForkAppCore.ulEnablePlatformFileSystem(baseDirectory);
    }

    /// <summary>Registers the AppCore default logger writing to <paramref name="logPath"/>.</summary>
    /// <param name="logPath">Absolute path for the Ultralight log file.</param>
    public static void SetDefaultLogger(string logPath)
    {
        EnsureNativeLibsLoaded();
        ForkAppCore.ulEnableDefaultLogger(logPath);
    }

    /// <summary>
    /// Pre-loads all Ultralight native libraries into the process before any
    /// P/Invoke call can trigger. Uses <see cref="NativeLibraryLoader"/> for
    /// multi-directory search, compat-shim creation, and DllImport resolution.
    /// </summary>
    private static void EnsureNativeLibsLoaded()
    {
        if (_loader is not null) return;

        string baseDir = AppContext.BaseDirectory;
        string nativeDir = Path.Combine(baseDir, "runtimes", NativeLibraryLoader.RuntimeIdentifier, "native");

        NativeLibraryLoader.EnsureDirectory(nativeDir);

        // -- Configure loader --
        var loader = new NativeLibraryLoader()
            .AddSearchPath(nativeDir)
            .AddSearchPath(baseDir)
            .MapNames(
                ("Ultralight",     null),
                ("UltralightCore", null),
                ("WebCore",        null),
                ("AppCore",        null))
            .OnLog(msg  => Logger.Debug(msg))
            .OnWarn(msg => Logger.Warn(msg));

        // -- Linux: libbz2.so.1.0 compatibility --
        // The Ultralight SDK's libWebCore.so was linked against libbz2.so.1.0, but
        // modern distros only ship libbz2.so.1 → libbz2.so.1.0.x.
        // 1. Create a symlink so the file is discoverable.
        // 2. Pre-load it so the SONAME is in the process's dlopen cache.
        if (OperatingSystem.IsLinux())
        {
            string[] bz2Candidates = [
                "/usr/lib/x86_64-linux-gnu/libbz2.so.1.0.8",
                "/usr/lib/x86_64-linux-gnu/libbz2.so.1.0.6",
                "/usr/lib/x86_64-linux-gnu/libbz2.so.1",
                "/usr/lib64/libbz2.so.1.0.8",
                "/usr/lib64/libbz2.so.1.0.6",
                "/usr/lib64/libbz2.so.1",
                "/usr/lib/libbz2.so.1.0.8",
                "/usr/lib/libbz2.so.1.0.6",
                "/usr/lib/libbz2.so.1",
                "/lib/x86_64-linux-gnu/libbz2.so.1",
                "/lib64/libbz2.so.1"
            ];

            string bz2Link = Path.Combine(nativeDir, "libbz2.so.1.0");
            loader.EnsureSymlink(bz2Link, bz2Candidates);
            loader.PreloadFirstAvailable("libbz2.so.1.0",
                [bz2Link, .. bz2Candidates]);
        }

        // -- Pre-load Ultralight libraries (dependency order) with RTLD_GLOBAL --
        // libWebCore depends on symbols exported by libUltralightCore;
        // libUltralight depends on libWebCore; libAppCore depends on libUltralight.
        // NativeLibrary.TryLoad uses RTLD_LOCAL on Linux, which makes the chained
        // dlopen() of libWebCore.so fail with unresolved-symbol errors. PreloadAllGlobal
        // uses dlopen(..., RTLD_LAZY | RTLD_GLOBAL) so each library's exports are
        // visible to the next one loaded.
        loader.PreloadAllGlobal(
            NativeLibraryLoader.ToPlatformFileName("UltralightCore"),
            NativeLibraryLoader.ToPlatformFileName("WebCore"),
            NativeLibraryLoader.ToPlatformFileName("Ultralight"),
            NativeLibraryLoader.ToPlatformFileName("AppCore"));

        // -- DllImport resolution --
        // IMPORTANT: do NOT call RegisterDllImportResolver(typeof(ForkAppCore).Assembly).
        // UltralightNet.Barebones merges UltralightNet.Methods and UltralightNet.AppCore.AppCoreMethods
        // into a single assembly, and UltralightNet.Methods..cctor calls
        // NativeLibrary.SetDllImportResolver itself during Preload(). Setting our own
        // resolver first causes that call to throw InvalidOperationException
        // ("A resolver is already set for the assembly").
        // The native libraries we pre-loaded above are now cached in the dynamic
        // linker by SONAME, so UltralightNet's own resolver (and any other
        // [DllImport] in this process) will resolve them correctly.
        // The global fallback handles the rare case where an assembly we don't
        // know about needs one of these libs by short name.
        loader.RegisterGlobalFallback();

        _loader = loader;
    }
}
