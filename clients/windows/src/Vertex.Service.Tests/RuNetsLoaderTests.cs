using System.Net;
using FluentAssertions;
using Vertex.Core.Net;
using Xunit;

namespace Vertex.Service.Tests;

public class RuNetsLoaderTests
{
    [Theory]
    [InlineData("2.56.24.0/22",   "2.56.24.0",    22)]
    [InlineData("8.8.8.0/24",     "8.8.8.0",      24)]
    [InlineData("0.0.0.0/0",      "0.0.0.0",       0)]
    [InlineData("255.255.255.255/32", "255.255.255.255", 32)]
    public void TryParseCidr_Valid(string line, string host, byte prefix)
    {
        RuNetsLoader.TryParseCidr(line, out var ip, out var p).Should().BeTrue();
        ip.Should().Be(IPAddress.Parse(host));
        p.Should().Be(prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment")]
    [InlineData("  # leading-ws comment")]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.1")]            // missing /prefix
    [InlineData("10.0.0.1/")]           // empty prefix
    [InlineData("10.0.0.1/33")]         // prefix > 32
    [InlineData("10.0.0/24")]           // 3-octet host
    [InlineData("10.0.0.999/24")]       // octet out of range
    [InlineData("::1/64")]              // IPv6
    public void TryParseCidr_InvalidOrSkipped(string line)
    {
        RuNetsLoader.TryParseCidr(line, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Load_ReadsBundledZone()
    {
        var bundled = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}.zone");
        var persistent = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}-active.zone");
        try
        {
            File.WriteAllText(bundled,
                "# header\n" +
                "1.2.3.0/24\n" +
                "5.6.7.0/16\n" +
                "\n" +
                "  10.0.0.0/8  \n");

            var loader = new RuNetsLoader(persistentPath: persistent, bundledPath: bundled);
            var lines = loader.Load();
            lines.Should().Equal("1.2.3.0/24", "5.6.7.0/16", "10.0.0.0/8");
            loader.ActiveSource.Should().Be(RuNetsLoader.Source.Bundled);
        }
        finally
        {
            try { File.Delete(bundled); } catch { }
            try { File.Delete(persistent); } catch { }
        }
    }

    [Fact]
    public void Load_PrefersUpdatedOverBundled()
    {
        var bundled = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}.zone");
        var persistent = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}-active.zone");
        try
        {
            File.WriteAllText(bundled, "1.2.3.0/24\n");
            File.WriteAllText(persistent, "9.9.9.0/24\n");

            var loader = new RuNetsLoader(persistentPath: persistent, bundledPath: bundled);
            loader.ActiveSource.Should().Be(RuNetsLoader.Source.Updated);
            loader.Load().Should().ContainSingle().Which.Should().Be("9.9.9.0/24");
        }
        finally
        {
            try { File.Delete(bundled); } catch { }
            try { File.Delete(persistent); } catch { }
        }
    }

    [Fact]
    public void LoadInfo_ReportsLineCountAndSize()
    {
        var bundled = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}.zone");
        var persistent = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}-active.zone");
        try
        {
            File.WriteAllText(bundled, "# c\n1.2.3.0/24\n5.6.7.0/24\n");
            var loader = new RuNetsLoader(persistentPath: persistent, bundledPath: bundled);
            var info = loader.LoadInfo();
            info.LineCount.Should().Be(2);
            info.Source.Should().Be(RuNetsLoader.Source.Bundled);
            info.UpdatedAtUtc.Should().BeNull();
            info.SizeBytes.Should().BeGreaterThan(0);
        }
        finally
        {
            try { File.Delete(bundled); } catch { }
            try { File.Delete(persistent); } catch { }
        }
    }

    [Fact]
    public async Task RefreshAsync_TooSmallBody_RejectedAndKeepsBundled()
    {
        var bundled = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}.zone");
        var persistent = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}-active.zone");
        try
        {
            File.WriteAllText(bundled, "1.2.3.0/24\n");
            // Stub the network to return a tiny body that fails MinBodyBytes.
            using var http = new HttpClient(new SmallBodyHandler("garbage"));
            var loader = new RuNetsLoader(persistentPath: persistent, bundledPath: bundled, http: http);

            var act = async () => await loader.RefreshAsync();
            await act.Should().ThrowAsync<InvalidDataException>();
            File.Exists(persistent).Should().BeFalse("rejected refresh must NOT promote tmp into persistent");
            loader.ActiveSource.Should().Be(RuNetsLoader.Source.Bundled);
        }
        finally
        {
            try { File.Delete(bundled); } catch { }
            try { File.Delete(persistent); } catch { }
        }
    }

    [Fact]
    public async Task RefreshAsync_ValidBody_PromotesAndOverridesBundled()
    {
        // Build a body that passes MinBodyBytes (50 KB) and MinCidrLines (1000).
        // Each line is ~15 bytes; need ≥3334 lines for 50 KB. Use 4000 to leave headroom.
        var lines = new List<string>(4_000);
        for (int i = 0; i < 4_000; i++)
        {
            lines.Add($"10.{i / 256 % 256}.{i % 256}.0/24");
        }
        var bigBody = string.Join("\n", lines) + "\n";

        var bundled = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}.zone");
        var persistent = Path.Combine(Path.GetTempPath(), $"vtx-zone-{Guid.NewGuid():N}-active.zone");
        try
        {
            File.WriteAllText(bundled, "1.2.3.0/24\n");
            using var http = new HttpClient(new FixedBodyHandler(bigBody));
            var loader = new RuNetsLoader(persistentPath: persistent, bundledPath: bundled, http: http);

            var info = await loader.RefreshAsync();
            info.Source.Should().Be(RuNetsLoader.Source.Updated);
            info.LineCount.Should().Be(4_000);
            info.UpdatedAtUtc.Should().NotBeNull();
            File.Exists(persistent).Should().BeTrue();
        }
        finally
        {
            try { File.Delete(bundled); } catch { }
            try { File.Delete(persistent); } catch { }
        }
    }

    private sealed class SmallBodyHandler : HttpMessageHandler
    {
        private readonly string _body;
        public SmallBodyHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            });
    }

    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FixedBodyHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            });
    }
}
