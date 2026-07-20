using System.Collections.Concurrent;
using FluentAssertions;
using Vertex.Core.Crypto;
using Vertex.Service.Tun;
using Xunit;

namespace Vertex.Service.Tests;

/// <summary>
/// In-memory <see cref="ITunDevice"/> that lets tests script the kernel
/// side of the data plane: queue inbound packets to be returned by
/// <see cref="ReceivePacket"/>, capture outbound packets that the
/// pipeline pushed via <see cref="SendPacket"/>.
/// </summary>
internal sealed class FakeTunDevice : ITunDevice
{
    private readonly BlockingCollection<byte[]> _inbound = new();
    public ConcurrentQueue<byte[]> Outbound { get; } = new();

    /// <summary>If &gt; 0, that many subsequent <see cref="SendPacket"/> calls return false (overflow simulation).</summary>
    public int FailNextSends;

    private int _disposed;

    public void EnqueueRead(byte[] packet) => _inbound.Add(packet);

    /// <summary>
    /// Wake the receive loop so it returns 0 — same contract as
    /// <see cref="WintunDevice.SignalShutdown"/>. PacketPipeline calls
    /// the WintunDevice form via type check; for tests we expose this
    /// directly and trigger it from <see cref="Dispose"/> as well.
    /// </summary>
    public void SignalShutdown() => _inbound.CompleteAdding();

    public int ReceivePacket(Span<byte> destination)
    {
        try
        {
            byte[] next = _inbound.Take();
            if (next.Length > destination.Length)
            {
                // Match WintunDevice: drop oversized + return -1 sentinel.
                return -1;
            }
            next.CopyTo(destination);
            return next.Length;
        }
        catch (InvalidOperationException)
        {
            // Queue closed.
            return 0;
        }
    }

    public bool SendPacket(ReadOnlySpan<byte> packet)
    {
        if (FailNextSends > 0)
        {
            FailNextSends--;
            return false;
        }
        Outbound.Enqueue(packet.ToArray());
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _inbound.CompleteAdding(); } catch { }
        _inbound.Dispose();
    }
}

public class PacketPipelineTests
{
    /// <summary>Build a minimal IPv4 packet (header version=4) of the requested length.</summary>
    private static byte[] MakeIpv4(int length, byte payloadByte = 0xAA)
    {
        var pkt = new byte[length];
        pkt[0] = 0x45; // IPv4, IHL=5
        for (int i = 1; i < length; i++) pkt[i] = payloadByte;
        return pkt;
    }

    [Fact]
    public void UpDirection_SealsAndPublishes_OnlyAfterSessionSet()
    {
        using var tun = new FakeTunDevice();
        var published = new BlockingCollection<byte[]>();
        var pipeline = new PacketPipeline(tun, sealedPacket => published.Add(sealedPacket.ToArray()));

        pipeline.Start(_ => { /* no download in this test */ });

        // No session yet — packet should be silently dropped.
        tun.EnqueueRead(MakeIpv4(64));
        Thread.Sleep(50);
        published.Count.Should().Be(0);

        // After SetSession the next read becomes a published sealed packet.
        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)i;
        using var crypto = SessionCrypto.FromKey(key);
        pipeline.SetSession(crypto);

        var ip = MakeIpv4(64, payloadByte: 0xCC);
        tun.EnqueueRead(ip);

        if (!published.TryTake(out var sealedPacket, TimeSpan.FromSeconds(2)))
            throw new TimeoutException("No published packet within 2s");

        sealedPacket.Length.Should().Be(ip.Length + 28); // ChaCha20-Poly1305 overhead
        crypto.Open(sealedPacket).Should().Equal(ip);

        pipeline.PacketsUp.Should().Be(1);
        pipeline.BytesUp.Should().Be(64);

        tun.SignalShutdown();
        pipeline.Stop();
    }

    [Fact]
    public void UpDirection_DropsIPv6Packets()
    {
        using var tun = new FakeTunDevice();
        var published = new BlockingCollection<byte[]>();
        var pipeline = new PacketPipeline(tun, sealedPacket => published.Add(sealedPacket.ToArray()));

        var key = new byte[32];
        using var crypto = SessionCrypto.FromKey(key);
        pipeline.SetSession(crypto);
        pipeline.Start(_ => { });

        // IPv6 first byte: version 6 in upper nibble = 0x6_.
        var ipv6 = new byte[64]; ipv6[0] = 0x60;
        tun.EnqueueRead(ipv6);
        Thread.Sleep(100);

        published.Count.Should().Be(0);
        pipeline.PacketsUp.Should().Be(0);

        tun.SignalShutdown();
        pipeline.Stop();
    }

    [Fact]
    public void DownDirection_DecryptsAndWritesIPv4_ToTun()
    {
        using var tun = new FakeTunDevice();
        Action<byte[]>? downloadHandler = null;
        var pipeline = new PacketPipeline(tun, _ => { });

        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)i;
        using var crypto = SessionCrypto.FromKey(key);
        pipeline.SetSession(crypto);
        pipeline.Start(handler => downloadHandler = handler);

        downloadHandler.Should().NotBeNull();

        var plaintext = MakeIpv4(80, payloadByte: 0xDD);
        var sealedPacket = crypto.Seal(plaintext);

        downloadHandler!(sealedPacket);

        // Wait briefly for the synchronous write through tun.SendPacket.
        for (int i = 0; i < 50 && tun.Outbound.IsEmpty; i++) Thread.Sleep(20);

        tun.Outbound.TryDequeue(out var written).Should().BeTrue();
        written.Should().Equal(plaintext);
        pipeline.PacketsDown.Should().Be(1);
        pipeline.BytesDown.Should().Be(80);

        tun.SignalShutdown();
        pipeline.Stop();
    }

    [Fact]
    public void DownDirection_DecryptError_IncrementsCounter_AndDoesNotWrite()
    {
        using var tun = new FakeTunDevice();
        Action<byte[]>? downloadHandler = null;
        var pipeline = new PacketPipeline(tun, _ => { });

        var key = new byte[32];
        using var crypto = SessionCrypto.FromKey(key);
        pipeline.SetSession(crypto);
        pipeline.Start(handler => downloadHandler = handler);

        // Garbage payload that won't decrypt to anything.
        downloadHandler!(new byte[64]);

        // Give the handler a moment to register the error.
        Thread.Sleep(50);
        pipeline.DecryptErrors.Should().Be(1);
        tun.Outbound.IsEmpty.Should().BeTrue();

        tun.SignalShutdown();
        pipeline.Stop();
    }

    [Fact]
    public void Stop_BeforeSessionSet_ExitsCleanly()
    {
        using var tun = new FakeTunDevice();
        var pipeline = new PacketPipeline(tun, _ => { });

        pipeline.Start(_ => { });
        // No traffic, no session — Stop() must unblock the up thread via
        // ITunDevice.Dispose() and join within timeout.
        pipeline.Stop();
    }

    [Fact]
    public void DownDirection_SendRingOverflow_IncrementsDroppedCounter_AndDoesNotCountAsDelivered()
    {
        using var tun = new FakeTunDevice();
        Action<byte[]>? downloadHandler = null;
        var pipeline = new PacketPipeline(tun, _ => { });

        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)i;
        using var crypto = SessionCrypto.FromKey(key);
        pipeline.SetSession(crypto);
        pipeline.Start(handler => downloadHandler = handler);

        // Simulate the WinTUN ring being full for the next call.
        tun.FailNextSends = 1;

        var plaintext = MakeIpv4(80);
        downloadHandler!(crypto.Seal(plaintext));

        Thread.Sleep(50);
        pipeline.PacketsDroppedDown.Should().Be(1);
        pipeline.PacketsDown.Should().Be(0);
        tun.Outbound.IsEmpty.Should().BeTrue();

        tun.SignalShutdown();
        pipeline.Stop();
    }

    [Fact]
    public void SetSession_SwappedMidFlow_NextSealUsesNewKey()
    {
        using var tun = new FakeTunDevice();
        var published = new BlockingCollection<byte[]>();
        var pipeline = new PacketPipeline(tun, sealedPacket => published.Add(sealedPacket.ToArray()));

        // First key.
        var k1 = new byte[32]; for (int i = 0; i < 32; i++) k1[i] = (byte)i;
        using var c1 = SessionCrypto.FromKey(k1);
        pipeline.SetSession(c1);
        pipeline.Start(_ => { });

        var p1 = MakeIpv4(40, 0xAA);
        tun.EnqueueRead(p1);
        if (!published.TryTake(out var s1, TimeSpan.FromSeconds(2)))
            throw new TimeoutException("packet 1 not published");
        c1.Open(s1).Should().Equal(p1);

        // Swap to a fresh key mid-flow — the NEXT seal must use it.
        var k2 = new byte[32]; for (int i = 0; i < 32; i++) k2[i] = (byte)(i ^ 0xFF);
        using var c2 = SessionCrypto.FromKey(k2);
        pipeline.SetSession(c2);

        var p2 = MakeIpv4(48, 0xBB);
        tun.EnqueueRead(p2);
        if (!published.TryTake(out var s2, TimeSpan.FromSeconds(2)))
            throw new TimeoutException("packet 2 not published");

        // The new sealed bytes must NOT decrypt with the old key.
        Action openWithOld = () => c1.Open(s2);
        openWithOld.Should().Throw<System.Security.Cryptography.CryptographicException>();
        c2.Open(s2).Should().Equal(p2);

        tun.SignalShutdown();
        pipeline.Stop();
    }

    [Fact]
    public void PipelineCounters_StartAtZero()
    {
        using var tun = new FakeTunDevice();
        var pipeline = new PacketPipeline(tun, _ => { });

        pipeline.BytesUp.Should().Be(0);
        pipeline.BytesDown.Should().Be(0);
        pipeline.PacketsUp.Should().Be(0);
        pipeline.PacketsDown.Should().Be(0);
        pipeline.DecryptErrors.Should().Be(0);
    }
}
