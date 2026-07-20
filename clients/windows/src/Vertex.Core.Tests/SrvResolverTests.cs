using System.Net;
using System.Text.Json;
using FluentAssertions;
using Vertex.Core.Discovery;
using Xunit;

namespace Vertex.Core.Tests;

public class SrvResolverTests
{
    [Fact]
    public async Task Resolve_PrimaryDomain_BuildsResultFromCloudflare()
    {
        var handler = new FakeDohHandler();
        handler.WhenSrv("_mqtt._tcp.vertices.ru",
            new SrvAns(10, 50, 8883, "yc.vertices.ru."),
            new SrvAns(20, 50, 8883, "sber.vertices.ru."));
        handler.WhenSrv("_vtx-exit._tcp.vertices.ru",
            new SrvAns(10, 50, 1, "aws.exit.vertices.ru."),
            new SrvAns(20, 50, 1, "sto.exit.vertices.ru."));
        handler.WhenSrv("_vtx-backup._tcp.vertices.ru",
            new SrvAns(10, 50, 1, "4few.ru."));
        handler.WhenTxt("aws.exit.vertices.ru", "\"Toronto, Canada\"");
        handler.WhenTxt("sto.exit.vertices.ru", "\"Stockholm, Sweden\"");

        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);

        var result = await sut.ResolveAsync("vertices.ru");

        result.Domain.Should().Be("vertices.ru");
        result.BackupDomain.Should().Be("4few.ru");
        result.Brokers.Should().HaveCount(2);
        result.BrokerUrls.Should().Equal("mqtts://yc.vertices.ru:8883", "mqtts://sber.vertices.ru:8883");
        result.ExitIds.Should().Equal("aws", "sto");
        result.ExitDisplayNames.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["aws"] = "Toronto, Canada",
            ["sto"] = "Stockholm, Sweden",
        });
    }

    [Fact]
    public async Task Resolve_NoBrokers_Throws()
    {
        var handler = new FakeDohHandler();
        handler.WhenSrv("_mqtt._tcp.vertices.ru"); // empty
        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);

        var act = async () => await sut.ResolveAsync("vertices.ru");

        await act.Should().ThrowAsync<SrvResolveException>();
    }

    [Fact]
    public async Task Resolve_CloudflareFails_FailsOverToGoogle()
    {
        var handler = new FakeDohHandler();
        handler.FailHost("cloudflare-dns.com"); // every request from this host throws
        handler.WhenSrv("_mqtt._tcp.vertices.ru",
            new SrvAns(10, 50, 8883, "yc.vertices.ru."));
        // exit + backup empty is fine; not required
        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);

        var result = await sut.ResolveAsync("vertices.ru");

        result.Brokers.Should().HaveCount(1);
        result.Brokers[0].Target.Should().Be("yc.vertices.ru");
        handler.RequestsTo("cloudflare-dns.com").Should().BeGreaterThan(0); // tried first
        handler.RequestsTo("dns.google").Should().BeGreaterThan(0);          // failed over
    }

    [Fact]
    public async Task Resolve_TxtMissingForOneExit_StillIncludesOthers()
    {
        var handler = new FakeDohHandler();
        handler.WhenSrv("_mqtt._tcp.vertices.ru",
            new SrvAns(10, 50, 8883, "yc.vertices.ru."));
        handler.WhenSrv("_vtx-exit._tcp.vertices.ru",
            new SrvAns(10, 50, 1, "aws.exit.vertices.ru."),
            new SrvAns(20, 50, 1, "sto.exit.vertices.ru."));
        handler.WhenTxt("aws.exit.vertices.ru", "\"Toronto, Canada\"");
        // sto TXT is absent — both providers return Status=0 with no Answer
        handler.WhenTxt("sto.exit.vertices.ru"); // empty

        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);

        var result = await sut.ResolveAsync("vertices.ru");

        result.ExitDisplayNames.Should().HaveCount(1);
        result.ExitDisplayNames["aws"].Should().Be("Toronto, Canada");
        result.ExitDisplayNames.ContainsKey("sto").Should().BeFalse();
    }

    [Fact]
    public async Task ResolveWithFallback_PrimaryFails_TriesCachedBackupDomain()
    {
        var cache = new InMemorySrvCache(new SrvDiscoveryResult(
            Domain: "vertices.ru",
            BackupDomain: "4few.ru",
            Brokers: new[] { new SrvRecord(10, 50, 8883, "old.broker.ru") },
            Exits: Array.Empty<SrvRecord>(),
            ExitDisplayNames: new Dictionary<string, string>(),
            UpdatedAtEpochMs: 0L));

        var handler = new FakeDohHandler();
        handler.FailQuery("_mqtt._tcp.vertices.ru"); // primary fails
        handler.WhenSrv("_mqtt._tcp.4few.ru",
            new SrvAns(10, 50, 8883, "yc.4few.ru."));

        var sut = new SrvResolver(new HttpClient(handler), cache);

        var result = await sut.ResolveWithFallbackAsync("vertices.ru");

        result.Should().NotBeNull();
        result!.Domain.Should().Be("4few.ru");
        result.Brokers.Single().Target.Should().Be("yc.4few.ru");
        cache.LastSaved!.Domain.Should().Be("4few.ru");
    }

    [Fact]
    public async Task ResolveWithFallback_AllFails_ReturnsCachedRegardlessOfAge()
    {
        var ancient = new SrvDiscoveryResult(
            Domain: "vertices.ru",
            BackupDomain: null,
            Brokers: new[] { new SrvRecord(10, 50, 8883, "yc.vertices.ru") },
            Exits: Array.Empty<SrvRecord>(),
            ExitDisplayNames: new Dictionary<string, string>(),
            UpdatedAtEpochMs: 0L); // 1970, definitely stale
        var cache = new InMemorySrvCache(ancient);

        var handler = new FakeDohHandler();
        handler.FailQuery("_mqtt._tcp.vertices.ru");

        var sut = new SrvResolver(new HttpClient(handler), cache);

        var result = await sut.ResolveWithFallbackAsync("vertices.ru");

        result.Should().BeSameAs(ancient);
    }

    [Fact]
    public async Task ResolveWithFallback_NoCacheNoNetwork_ReturnsNull()
    {
        var handler = new FakeDohHandler();
        handler.FailQuery("_mqtt._tcp.vertices.ru");
        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);

        var result = await sut.ResolveWithFallbackAsync("vertices.ru");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_RequestTimeout_FailsOverToNextProvider()
    {
        var handler = new FakeDohHandler();
        handler.HangHost("cloudflare-dns.com"); // simulate slow provider
        handler.WhenSrv("_mqtt._tcp.vertices.ru",
            new SrvAns(10, 50, 8883, "yc.vertices.ru."));

        var sut = new SrvResolver(
            new HttpClient(handler),
            NullSrvCache.Instance,
            requestTimeout: TimeSpan.FromMilliseconds(150));

        var result = await sut.ResolveAsync("vertices.ru");

        result.Brokers.Should().HaveCount(1);
        handler.RequestsTo("dns.google").Should().BeGreaterThan(0);
    }

    [Fact]
    public void DiscoveryResult_IsFresh_TrueWithinTtl()
    {
        var now = 1_700_000_000_000L;
        var fresh = new SrvDiscoveryResult(
            "vertices.ru", null, Array.Empty<SrvRecord>(), Array.Empty<SrvRecord>(),
            new Dictionary<string, string>(), now - 1_000);
        var stale = fresh with { UpdatedAtEpochMs = now - SrvDiscoveryResult.CacheTtlMs - 1 };

        fresh.IsFresh(now).Should().BeTrue();
        stale.IsFresh(now).Should().BeFalse();
    }

    [Fact]
    public void DiscoveryResult_BrokerUrls_MapPortsToSchemes()
    {
        var result = new SrvDiscoveryResult(
            "vertices.ru", null,
            Brokers: new[]
            {
                new SrvRecord(10, 50, 8883, "yc.vertices.ru"),
                new SrvRecord(10, 50, 443,  "yc.vertices.ru"),
                new SrvRecord(10, 50, 1883, "plain.example"),
                new SrvRecord(10, 50, 9999, "weird.example"),
            },
            Exits: Array.Empty<SrvRecord>(),
            ExitDisplayNames: new Dictionary<string, string>(),
            UpdatedAtEpochMs: 0L);

        result.BrokerUrls.Should().Equal(
            "mqtts://yc.vertices.ru:8883",
            "wss://yc.vertices.ru:443",
            "mqtt://plain.example:1883",
            "mqtt://weird.example:9999");
    }

    [Fact]
    public async Task Resolve_DohStatusNonZero_TreatedAsEmpty()
    {
        // NXDOMAIN-like: 200 OK but Status=3 (or any non-zero). Matches
        // Swift+Kotlin behavior of treating the answer set as absent.
        var handler = new FakeDohHandler();
        handler.WhenSrvNxdomain("_mqtt._tcp.vertices.ru");
        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);

        var act = async () => await sut.ResolveAsync("vertices.ru");

        await act.Should().ThrowAsync<SrvResolveException>();
    }

    [Fact]
    public async Task Resolve_TxtMultiSegment_ConcatenatesViaTxtParser()
    {
        var handler = new FakeDohHandler();
        handler.WhenSrv("_mqtt._tcp.vertices.ru",
            new SrvAns(10, 50, 8883, "yc.vertices.ru."));
        handler.WhenSrv("_vtx-exit._tcp.vertices.ru",
            new SrvAns(10, 50, 1, "aws.exit.vertices.ru."));
        // Two RFC 1035 character-strings joined at the wire — TxtParser
        // collapses to a single string with no separator.
        handler.WhenTxt("aws.exit.vertices.ru", "\"Toronto, \" \"Canada\"");

        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);
        var result = await sut.ResolveAsync("vertices.ru");

        result.ExitDisplayNames["aws"].Should().Be("Toronto, Canada");
    }

    [Fact]
    public async Task DiscoveryResult_RoundTripsThroughJson()
    {
        var handler = new FakeDohHandler();
        handler.WhenSrv("_mqtt._tcp.vertices.ru",
            new SrvAns(10, 50, 8883, "yc.vertices.ru."));
        handler.WhenSrv("_vtx-exit._tcp.vertices.ru",
            new SrvAns(10, 50, 1, "aws.exit.vertices.ru."));
        handler.WhenTxt("aws.exit.vertices.ru", "\"Toronto, Canada\"");

        var sut = new SrvResolver(new HttpClient(handler), NullSrvCache.Instance);
        var original = await sut.ResolveAsync("vertices.ru");

        var json = JsonSerializer.Serialize(original);
        json.Should().Contain("\"domain\":\"vertices.ru\"");
        json.Should().Contain("\"updatedAtEpochMs\":");
        json.Should().Contain("\"exitDisplayNames\":");

        var back = JsonSerializer.Deserialize<SrvDiscoveryResult>(json)!;
        back.Domain.Should().Be(original.Domain);
        back.Brokers.Should().BeEquivalentTo(original.Brokers);
        back.Exits.Should().BeEquivalentTo(original.Exits);
        back.ExitDisplayNames.Should().BeEquivalentTo(original.ExitDisplayNames);
    }

    // -------- helpers --------

    private sealed class InMemorySrvCache : ISrvCache
    {
        private SrvDiscoveryResult? _value;
        public SrvDiscoveryResult? LastSaved => _value;
        public InMemorySrvCache(SrvDiscoveryResult? initial = null) => _value = initial;
        public Task<SrvDiscoveryResult?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_value);
        public Task SaveAsync(SrvDiscoveryResult result, CancellationToken ct = default) { _value = result; return Task.CompletedTask; }
    }

    private readonly record struct SrvAns(int Priority, int Weight, int Port, string Target);

    /// <summary>
    /// Stub <see cref="HttpMessageHandler"/> matching DoH "application/dns-json"
    /// queries. Records routed by query (<c>name</c> + <c>type</c>) so a
    /// single test can program both Cloudflare and Google replies; failure
    /// modes (host-wide refusal, hang) compose with the per-query map.
    /// </summary>
    private sealed class FakeDohHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, SrvAns[]> _srv = new();
        private readonly Dictionary<string, string?> _txt = new();
        private readonly HashSet<string> _failingHosts = new();
        private readonly HashSet<string> _hangingHosts = new();
        private readonly HashSet<string> _failingQueries = new();
        private readonly HashSet<string> _nxdomainQueries = new();
        private readonly Dictionary<string, int> _hostHits = new();

        public void WhenSrv(string name, params SrvAns[] answers) => _srv[name] = answers;
        public void WhenTxt(string name, string? data = null) => _txt[name] = data;
        public void FailHost(string host) => _failingHosts.Add(host);
        public void HangHost(string host) => _hangingHosts.Add(host);
        public void FailQuery(string name) => _failingQueries.Add(name);
        public void WhenSrvNxdomain(string name) => _nxdomainQueries.Add(name);
        public int RequestsTo(string host) => _hostHits.GetValueOrDefault(host);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            _hostHits[host] = _hostHits.GetValueOrDefault(host) + 1;

            if (_failingHosts.Contains(host)) throw new HttpRequestException($"simulated {host} failure");
            if (_hangingHosts.Contains(host))
            {
                // Block until the per-request CTS fires (5s default, override
                // in tests via SrvResolver requestTimeout).
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }

            var (name, type) = ParseDohQuery(request.RequestUri!.Query);

            if (_failingQueries.Contains(name)) throw new HttpRequestException($"simulated query failure for {name}");

            object body = type switch
            {
                "SRV" when _nxdomainQueries.Contains(name) => new { Status = 3 },
                "SRV" => BuildSrvBody(name, _srv.GetValueOrDefault(name) ?? Array.Empty<SrvAns>()),
                "TXT" => BuildTxtBody(_txt.GetValueOrDefault(name)),
                _ => new { Status = 0 },
            };
            var json = JsonSerializer.Serialize(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/dns-json"),
            };
        }

        private static (string Name, string Type) ParseDohQuery(string raw)
        {
            string name = "", type = "";
            var trimmed = raw.StartsWith('?') ? raw[1..] : raw;
            foreach (var pair in trimmed.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = Uri.UnescapeDataString(pair[..eq]);
                var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (key == "name") name = val;
                else if (key == "type") type = val;
            }
            return (name, type);
        }

        private static object BuildSrvBody(string queryName, SrvAns[] answers) => new
        {
            Status = 0,
            Answer = answers.Select(a => new
            {
                name = queryName,
                type = 33,
                data = $"{a.Priority} {a.Weight} {a.Port} {a.Target}",
            }).ToArray(),
        };

        private static object BuildTxtBody(string? data)
        {
            if (data is null) return new { Status = 0 };
            return new
            {
                Status = 0,
                Answer = new[] { new { name = "x", type = 16, data = data } },
            };
        }
    }
}
