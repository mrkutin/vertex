using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Vertex.Service.Ipc;
using Vertex.Shared;
using Vertex.Shared.Ipc;
using Xunit;

namespace Vertex.Service.Tests;

public class PipeServerTests
{
    private static string UniquePipeName() => $"vertex-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task RoundTrip_AppMessageDispatched_ServerCanPushBack()
    {
        var received = new List<AppMessage>();
        var dispatched = new TaskCompletionSource();
        string pipeName = UniquePipeName();

        await using var server = new PipeServer(msg =>
        {
            lock (received) received.Add(msg);
            if (msg is AppMessage.SetSelectedExit) dispatched.TrySetResult();
            return Task.CompletedTask;
        }, pipeName);
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);

        var line = JsonSerializer.Serialize<AppMessage>(new AppMessage.SetSelectedExit("aws")) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await client.WriteAsync(bytes);
        await client.FlushAsync();

        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (received) received.Should().HaveCount(1);
        ((AppMessage.SetSelectedExit)received[0]).ExitId.Should().Be("aws");

        server.Push(new ExtensionResponse.StatusEnvelope(
            new ConnectionStatus(ConnectionState.Connected, AssignedIp: "10.9.0.5")));

        var readBuf = new byte[8192];
        int n = await client.ReadAsync(readBuf.AsMemory(), default).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        n.Should().BeGreaterThan(0);
        var pushed = Encoding.UTF8.GetString(readBuf, 0, n);
        pushed.Should().Contain("\"type\":\"status\"");
        pushed.Should().Contain("\"assignedIp\":\"10.9.0.5\"");
    }

    [Fact]
    public async Task UnknownDiscriminator_Logged_AndDoesNotKillServer()
    {
        var received = new List<AppMessage>();
        string pipeName = UniquePipeName();

        await using var server = new PipeServer(msg =>
        {
            lock (received) received.Add(msg);
            return Task.CompletedTask;
        }, pipeName);
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);

        var bad = Encoding.UTF8.GetBytes("""{"type":"setLogLevel","level":"trace"}""" + "\n");
        await client.WriteAsync(bad);
        await client.FlushAsync();

        var ok = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize<AppMessage>(new AppMessage.RequestStatus()) + "\n");
        await client.WriteAsync(ok);
        await client.FlushAsync();

        for (int i = 0; i < 50; i++)
        {
            lock (received) if (received.Count >= 1) break;
            await Task.Delay(20);
        }
        lock (received) received.Should().HaveCount(1);
        received[0].Should().BeOfType<AppMessage.RequestStatus>();
    }

    [Fact]
    public async Task LineCap_OversizedInputDisconnects_ServerKeepsAcceptingNewClients()
    {
        var received = new List<AppMessage>();
        string pipeName = UniquePipeName();

        await using var server = new PipeServer(msg =>
        {
            lock (received) received.Add(msg);
            return Task.CompletedTask;
        }, pipeName);
        server.Start();

        // First client: send 80 KiB without a newline → server hits the
        // 64 KiB cap and disconnects us mid-write.
        await using (var hostile = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await hostile.ConnectAsync(2000);
            var blob = new byte[80 * 1024];
            for (int i = 0; i < blob.Length; i++) blob[i] = (byte)'A';
            try
            {
                await hostile.WriteAsync(blob);
                await hostile.FlushAsync();
            }
            catch { /* expected — pipe closed by server */ }
            // Give the server a moment to recycle the accept loop.
            await Task.Delay(100);
        }

        // Second client: a well-behaved one must still get through.
        await using var goodClient = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await goodClient.ConnectAsync(2000);

        var ok = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize<AppMessage>(new AppMessage.RequestStatus()) + "\n");
        await goodClient.WriteAsync(ok);
        await goodClient.FlushAsync();

        for (int i = 0; i < 50; i++)
        {
            lock (received) if (received.Count >= 1) break;
            await Task.Delay(20);
        }
        lock (received) received.Should().HaveCount(1);
    }
}
