using System.Reflection;
using System.Runtime.InteropServices;

namespace Vertex.Core.Crypto;

/// <summary>
/// Native-library lookup hook for NSec.Cryptography. Teaches .NET's loader
/// where to find <c>libsodium.dll</c> when this assembly is dropped into a
/// build output that didn't materialise the RID-specific native asset.
///
/// Today, on the Vertex.Core.Tests pipeline, the ARM64-host x64-emulated
/// testhost picks up the win-x64 libsodium copied flat by an MSBuild target
/// — the resolver below is redundant in that path. It becomes load-bearing
/// once we ship single-file <c>dotnet publish</c> output (where
/// <c>runtimes/{rid}/native/</c> still lives next to the exe but the
/// runtime native loader doesn't always probe it on first call) and once
/// MS finally ships an ARM64 testhost (then the resolver routes the
/// runtime to the right RID without us having to swap the copied DLL).
///
/// Strategy: register a <see cref="DllImportResolver"/> on the NSec
/// assembly that looks in <c>runtimes/{rid}/native/</c> next to the
/// consuming assembly and loads by absolute path. If nothing is found, it
/// returns <c>IntPtr.Zero</c> and the default resolver takes over (which
/// keeps an OS-installed libsodium working when present).
/// </summary>
internal static class LibsodiumLoader
{
    private static int s_initialized;

    /// <summary>
    /// Idempotent. Called from every Crypto type's static constructor before
    /// it touches any NSec API, so the resolver is registered before NSec's
    /// own static initialization tries to dlopen <c>libsodium</c>.
    /// </summary>
    internal static void Initialize()
    {
        if (Interlocked.CompareExchange(ref s_initialized, 1, 0) != 0) return;

        var nsec = typeof(NSec.Cryptography.KeyAgreementAlgorithm).Assembly;
        NativeLibrary.SetDllImportResolver(nsec, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("libsodium", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86   => "win-x86",
            _ => null,
        };
        if (rid is null) return IntPtr.Zero;

        var probe = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes", rid, "native", "libsodium.dll");

        return File.Exists(probe) && NativeLibrary.TryLoad(probe, out var handle)
            ? handle
            : IntPtr.Zero;
    }
}
