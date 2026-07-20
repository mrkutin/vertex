using System.Runtime.InteropServices;
using FluentAssertions;
using Vertex.Service.Net;
using Xunit;

namespace Vertex.Service.Tests;

/// <summary>
/// Pin our managed P/Invoke struct sizes against the canonical Windows
/// SDK <c>netioapi.h</c> sizes. Drift here = the kernel writes past our
/// struct's end, corrupting the C# stack — the Phase 1.8 review
/// CRITICAL-1 path. These tests run in-process on Windows ARM64 / x64
/// CI runners and fail loudly the moment a struct field is added or a
/// Pack attribute is applied incorrectly.
/// </summary>
public class IpHelperInteropTests
{
    /// <summary>
    /// MIB_IPFORWARD_ROW2 — canonical size on x64 / ARM64 with default
    /// SDK packing is 104 bytes. (Windows 10 SDK 10.0.22621 verified;
    /// 32-bit hosts would be 80 — but the project targets x64/ARM64
    /// only via <c>RuntimeIdentifiers</c>.)
    /// </summary>
    [Fact]
    public void MibIpForwardRow2_Size_MatchesNativeOnX64Arm64()
    {
        if (!Environment.Is64BitProcess)
        {
            // Skip on 32-bit hosts — production never runs there.
            return;
        }
        Marshal.SizeOf<IpHelperInterop.MibIpForwardRow2>().Should().Be(104);
    }

    /// <summary>
    /// SOCKADDR_INET — 28 bytes per WS2def.h (header packing). Sanity
    /// guard: if this drifts, IpAddressPrefix and the row containing
    /// it shift accordingly.
    /// </summary>
    [Fact]
    public void SockaddrInet_Size_Is28Bytes()
    {
        Marshal.SizeOf<IpHelperInterop.SockaddrInet>().Should().Be(28);
    }

    /// <summary>
    /// IP_ADDRESS_PREFIX — SOCKADDR_INET (28) + UCHAR PrefixLength + 3 B
    /// trailing alignment pad = 32 bytes.
    /// </summary>
    [Fact]
    public void IpAddressPrefix_Size_Is32Bytes()
    {
        Marshal.SizeOf<IpHelperInterop.IpAddressPrefix>().Should().Be(32);
    }
}
