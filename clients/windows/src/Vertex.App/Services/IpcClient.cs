using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Shared.Ipc;

namespace Vertex.App.Services;

/// <summary>
/// Named-pipe client for the Vertex Service IPC. Connects to
/// <c>\\.\pipe\vertex-vpn</c>, sends <see cref="AppMessage"/>s as
/// JSON-line, surfaces incoming <see cref="ExtensionResponse"/>s
/// through <see cref="MessagesAsync"/>. Auto-reconnects on disconnect.
///
/// Mirror of the macOS <c>NETunnelProviderSession</c> + Android
/// <c>VertexService.Client</c> roles.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _log;
    private readonly Channel<ExtensionResponse> _incoming;
    private readonly CancellationTokenSource _cts = new();
    /// <summary><c>volatile</c> for ARM64 reordering safety — read by <see cref="SendAsync"/>, written by the read loop.</summary>
    private volatile NamedPipeClientStream? _stream;
    private Task? _readLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Default Service pipe name. Mirror of <c>PipeServer.DefaultPipeName</c>; kept as a literal here so Vertex.App doesn't take a Vertex.Service dependency.</summary>
    public const string DefaultPipeName = "vertex-vpn";

    public IpcClient(string pipeName = DefaultPipeName, ILogger? log = null)
    {
        _pipeName = pipeName;
        _log = log ?? NullLogger.Instance;
        _incoming = Channel.CreateUnbounded<ExtensionResponse>(
            new UnboundedChannelOptions { SingleWriter = true });
    }

    /// <summary>Async stream of inbound messages from the Service.</summary>
    public IAsyncEnumerable<ExtensionResponse> MessagesAsync => _incoming.Reader.ReadAllAsync();

    /// <summary>True when the named pipe is currently connected.</summary>
    public bool IsConnected => _stream is { IsConnected: true };

    /// <summary>
    /// Begin connect-and-read loop. Returns immediately. Reconnects
    /// on disconnect with a 2-second back-off. Cancellable via
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    public void Start()
    {
        _readLoop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>Send one <see cref="AppMessage"/> as a JSON line. Drops silently if disconnected.</summary>
    public async Task SendAsync(AppMessage msg, CancellationToken ct = default)
    {
        var stream = _stream;
        if (stream is null || !stream.IsConnected) return;

        // Explicit generic <AppMessage> so the [JsonPolymorphic] discriminator fires.
        string line = JsonSerializer.Serialize<AppMessage>(msg) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Race with reconnect: read loop disposed the stream
            // between our snapshot and Write. Expected, not an error.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pipe write failed (service down?)");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var lineBuffer = new MemoryStream();
        var readBuf = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream stream;
            try
            {
                stream = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: _pipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);
                await stream.ConnectAsync(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Connect to {Pipe} failed — retry in 2s", _pipeName);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            _stream = stream;
            _log.LogInformation("Connected to Service on \\\\.\\pipe\\{Pipe}", _pipeName);

            try
            {
                while (!ct.IsCancellationRequested && stream.IsConnected)
                {
                    int n = await stream.ReadAsync(readBuf, ct).ConfigureAwait(false);
                    if (n == 0) break;

                    for (int i = 0; i < n; i++)
                    {
                        byte b = readBuf[i];
                        if (b == (byte)'\n')
                        {
                            if (lineBuffer.Length > 0)
                            {
                                DispatchLine(lineBuffer.GetBuffer().AsSpan(0, (int)lineBuffer.Length));
                                lineBuffer.SetLength(0);
                            }
                        }
                        else
                        {
                            lineBuffer.WriteByte(b);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Pipe read failed — reconnect");
            }
            finally
            {
                // Null _stream BEFORE disposing so SendAsync's snapshot
                // pattern doesn't grab a soon-to-be-disposed reference
                // (Phase 1.10 review MAJOR-2).
                _stream = null;
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
            }

            // Brief pause before reconnect attempt.
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void DispatchLine(ReadOnlySpan<byte> json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<ExtensionResponse>(json);
            if (msg is not null) _incoming.Writer.TryWrite(msg);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pipe line unparseable: {Line}", Encoding.UTF8.GetString(json));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        if (_readLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        _incoming.Writer.TryComplete();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
