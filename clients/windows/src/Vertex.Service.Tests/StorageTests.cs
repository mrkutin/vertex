using FluentAssertions;
using Vertex.Core.Crypto;
using Vertex.Core.Discovery;
using Vertex.Service.Storage;
using Xunit;

namespace Vertex.Service.Tests;

public class IdentityStoreTests
{
    [Fact]
    public void LoadOrCreate_FirstRun_GeneratesAndPersists()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-id-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new IdentityStore(path);
            using var first = store.LoadOrCreate();
            File.Exists(path).Should().BeTrue();

            using var second = store.LoadOrCreate();
            second.PublicKeyHex.Should().Be(first.PublicKeyHex);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Save_ThenLoad_Roundtrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-id-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new IdentityStore(path);
            using var fresh = IdentityKey.Generate();
            store.Save(fresh);

            using var loaded = store.LoadOrCreate();
            loaded.PublicKeyHex.Should().Be(fresh.PublicKeyHex);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Reset_DeletesFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-id-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new IdentityStore(path);
            using var _ = store.LoadOrCreate();
            File.Exists(path).Should().BeTrue();

            store.Reset();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

public class PasswordStoreTests
{
    [Fact]
    public void GetSet_Roundtrips_OverDpapi()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-pw-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new PasswordStore(path);
            store.Get().Should().BeNull();

            store.Set("s3cret-broker-creds!");
            store.Get().Should().Be("s3cret-broker-creds!");

            // Plain bytes are NOT on disk — file content is DPAPI blob.
            byte[] sealedBytes = File.ReadAllBytes(path);
            System.Text.Encoding.UTF8.GetString(sealedBytes)
                .Should().NotContain("s3cret-broker-creds!");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Clear_DropsTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-pw-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new PasswordStore(path);
            store.Set("hunter2");
            File.Exists(path).Should().BeTrue();

            store.Clear();
            File.Exists(path).Should().BeFalse();
            store.Get().Should().BeNull();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

public class StateStoreTests
{
    [Fact]
    public void Save_ThenLoad_Roundtrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new StateStore(path);
            store.Load<ServiceState>().Should().BeNull();

            var s = new ServiceState
            {
                LastGoodBroker     = "yc.example",
                LastGoodExit       = "aws",
                SelectedExit       = "auto",
                SelectedBroker     = "auto",
                SplitTunnelEnabled = true,
                SrvCacheRefreshedTicks = 1700000000000,
            };
            store.Save(s);

            var back = store.Load<ServiceState>();
            back.Should().BeEquivalentTo(s);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-state-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not-json");
            var store = new StateStore(path);
            store.Load<ServiceState>().Should().BeNull();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void DefaultDiscoveryDomain_IsProductionZone()
    {
        // Anchor the default; the engine reads it via state, and
        // an accidental rename would silently misconfigure SRV resolution
        // for fresh installs that haven't surfaced UI override yet.
        new ServiceState().DiscoveryDomain.Should().Be("vertices.ru");
    }
}

public class StateStoreSrvCacheTests
{
    [Fact]
    public async Task SaveLoad_RoundTripsSrvResultThroughStateJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-srv-{Guid.NewGuid():N}.json");
        try
        {
            var store = new StateStore(path);
            var sut = new StateStoreSrvCache(store);

            (await sut.LoadAsync()).Should().BeNull("no state.json yet");

            var result = new SrvDiscoveryResult(
                Domain: "vertices.ru",
                BackupDomain: "4few.ru",
                Brokers: new[] { new SrvRecord(10, 50, 8883, "yc.vertices.ru") },
                Exits: new[] { new SrvRecord(10, 50, 1, "aws.exit.vertices.ru") },
                ExitDisplayNames: new Dictionary<string, string> { ["aws"] = "Toronto, Canada" },
                UpdatedAtEpochMs: 1_700_000_000_000L);
            await sut.SaveAsync(result);

            var back = await sut.LoadAsync();
            back.Should().NotBeNull();
            back!.Domain.Should().Be("vertices.ru");
            back.BackupDomain.Should().Be("4few.ru");
            back.Brokers.Should().HaveCount(1);
            back.Brokers[0].Target.Should().Be("yc.vertices.ru");
            back.ExitDisplayNames["aws"].Should().Be("Toronto, Canada");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Save_PreservesUnrelatedServiceStateFields()
    {
        // SRV save must not stomp LastGoodBroker / SelectedExit / etc.
        // The Service mutates state.json from multiple call sites and
        // a write-without-load path would lose user-tuned settings.
        string path = Path.Combine(Path.GetTempPath(), $"vtx-srv-{Guid.NewGuid():N}.json");
        try
        {
            var store = new StateStore(path);
            store.Save(new ServiceState
            {
                LastGoodBroker     = "sber.vertices.ru",
                LastGoodExit       = "sto",
                SelectedExit       = "aws",
                SplitTunnelEnabled = true,
            });

            var sut = new StateStoreSrvCache(store);
            await sut.SaveAsync(new SrvDiscoveryResult(
                "vertices.ru", null,
                new[] { new SrvRecord(10, 50, 8883, "yc.vertices.ru") },
                Array.Empty<SrvRecord>(),
                new Dictionary<string, string>(),
                UpdatedAtEpochMs: 0));

            var back = store.Load<ServiceState>();
            back.Should().NotBeNull();
            back!.LastGoodBroker.Should().Be("sber.vertices.ru");
            back.LastGoodExit.Should().Be("sto");
            back.SelectedExit.Should().Be("aws");
            back.SplitTunnelEnabled.Should().BeTrue();
            back.LastSrv.Should().NotBeNull();
            back.SrvCacheRefreshedTicks.Should().NotBeNull();
        }
        finally { try { File.Delete(path); } catch { } }
    }
}

public class AdapterGuidStoreTests
{
    [Fact]
    public void LoadOrCreate_FirstRun_GeneratesAndPersistsStableGuid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-guid-{Guid.NewGuid():N}.txt");
        try
        {
            var first  = AdapterGuidStore.LoadOrCreate(path);
            var second = AdapterGuidStore.LoadOrCreate(path);
            first.Should().Be(second);
            File.ReadAllText(path).Trim().Should().Be(first.ToString("D"));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void LoadOrCreate_CorruptFile_RegeneratesFreshGuid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vtx-guid-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "not-a-guid");
            var fresh = AdapterGuidStore.LoadOrCreate(path);
            fresh.Should().NotBe(Guid.Empty);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
