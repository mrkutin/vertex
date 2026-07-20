using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Shared.Ipc;

namespace Vertex.Service.Ipc;

/// <summary>
/// Named-pipe server that hosts the Service ↔ App control channel.
///
/// Wire format: JSON line-delimited (one <see cref="AppMessage"/> /
/// <see cref="ExtensionResponse"/> per <c>'\n'</c>-terminated line). A
/// single client connection at a time — Phase 1 has one App per
/// service. The pipe security ACL grants
/// <list type="bullet">
///   <item>full control to <c>LocalSystem</c> (the service account);</item>
///   <item>read+write to <c>BUILTIN\Users</c> — local-machine
///   interactive users only. <c>Authenticated Users</c> would also
///   admit remote-authenticated principals on a domain-joined box,
///   widening the attack surface to anyone who can authenticate to the
///   workstation (Phase 1.8 review CRITICAL-2). A future hardening pass
///   in Phase 1.10 will additionally restrict to the active console
///   session SID.</item>
/// </list>
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    /// <summary>Default pipe path. Apps connect via <c>\\.\pipe\vertex-vpn</c>.</summary>
    public const string DefaultPipeName = "vertex-vpn";

    /// <summary>Backwards-compatible alias.</summary>
    public const string PipeName = DefaultPipeName;

    /// <summary>Hard cap on a single pipe-line buffer — protects the LocalSystem service from a DoS by an unprivileged local writer (Phase 1.8 review MAJOR-1).</summary>
    private const int MaxLineBytes = 64 * 1024;

    private readonly string _pipeName;
    private readonly Func<AppMessage, Task> _onMessage;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();

    /// <summary>Mutated under <see cref="_writeLock"/>; read under it too. Avoids a TOCTOU where Push() snapshots a stream that the accept loop is mid-disposing.</summary>
    private NamedPipeServerStream? _currentStream;
    private Task? _acceptLoop;

    /// <summary>
    /// Fired after a client connects, AFTER <c>_currentStream</c> is
    /// published. Lets <c>TunnelEngine</c> push the cached status so a
    /// late-attaching App immediately sees the current state without
    /// having to send <c>RequestStatus</c> first (Phase 1.9 review
    /// MAJOR-6).
    /// </summary>
    public Action? OnClientConnected { get; set; }

    public PipeServer(
        Func<AppMessage, Task> onMessage,
        string? pipeName = null,
        ILogger? log = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;
        _onMessage = onMessage;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Start accepting client connections. Returns immediately; the
    /// accept loop runs in the background until <see cref="DisposeAsync"/>.
    /// </summary>
    public void Start()
    {
        _acceptLoop = Task.Run(() => RunAcceptLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Push <paramref name="response"/> to the currently connected client
    /// as a single JSON line. Drops silently if no client is connected.
    /// Thread-safe; serialises writes through an internal lock so two
    /// concurrent <see cref="ExtensionResponse"/> emissions don't
    /// interleave bytes on the wire.
    /// </summary>
    public void Push(ExtensionResponse response)
    {
        // Explicit generic <ExtensionResponse> — without it, JsonSerializer
        // would dispatch on the runtime type of `response` and skip the
        // [JsonPolymorphic] "type" discriminator, breaking deserialisation
        // on the App side.
        string line = JsonSerializer.Serialize<ExtensionResponse>(response) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        lock (_writeLock)
        {
            // Snapshot the stream INSIDE the lock so the accept loop can't
            // null/dispose it between our null-check and the Write
            // (Phase 1.8 review MAJOR-4).
            var stream = _currentStream;
            if (stream is null || !stream.IsConnected) return;
            try
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Pipe push failed (client disconnected?)");
            }
        }
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream stream;
            try
            {
                stream = CreatePipe();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "CreatePipe failed — accept loop terminating");
                return;
            }

            try
            {
                await stream.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WaitForConnection failed — retrying");
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _log.LogInformation("Pipe client connected");
            lock (_writeLock) { _currentStream = stream; }
            try { OnClientConnected?.Invoke(); } catch (Exception ex) { _log.LogWarning(ex, "OnClientConnected handler threw"); }
            try
            {
                await ServeOneClientAsync(stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Client session crashed");
            }
            finally
            {
                lock (_writeLock) { _currentStream = null; }
                try { stream.Disconnect(); } catch { /* swallow */ }
                await stream.DisposeAsync().ConfigureAwait(false);
                _log.LogInformation("Pipe client disconnected");
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        // FullControl for LocalSystem (the service account); ReadWrite
        // for BUILTIN\Users (local-machine interactive users only —
        // narrower than Authenticated Users which would also admit
        // remote-authenticated principals on a domain-joined box).
        // Phase 1.10 will additionally restrict to the active console
        // session SID via WTSGetActiveConsoleSessionId.
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: ps);
    }

    private async Task ServeOneClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        // Line-delimited JSON: read until '\n', parse, dispatch.
        var buffer = new byte[8192];
        var line = new MemoryStream();

        while (!ct.IsCancellationRequested && stream.IsConnected)
        {
            int n;
            try { n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Pipe read failed (client gone?)");
                return;
            }
            if (n == 0) return; // EOF — client closed cleanly

            for (int i = 0; i < n; i++)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    if (line.Length > 0)
                    {
                        await DispatchLineAsync(line.GetBuffer().AsMemory(0, (int)line.Length)).ConfigureAwait(false);
                        line.SetLength(0);
                    }
                }
                else
                {
                    if (line.Length >= MaxLineBytes)
                    {
                        // DoS protection: drop the connection rather than
                        // grow the line buffer past the cap. A legitimate
                        // AppMessage is well under 4 KiB.
                        _log.LogWarning("Pipe client exceeded {Max}-byte line cap — disconnecting", MaxLineBytes);
                        return;
                    }
                    line.WriteByte(b);
                }
            }
        }
    }

    private async Task DispatchLineAsync(ReadOnlyMemory<byte> jsonLine)
    {
        AppMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<AppMessage>(jsonLine.Span);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pipe line unparseable: {Line}", Encoding.UTF8.GetString(jsonLine.Span));
            return;
        }

        if (msg is null)
        {
            _log.LogDebug("Unknown discriminator on pipe — ignoring");
            return;
        }

        try
        {
            await _onMessage(msg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Message handler threw on {Type}", msg.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { /* swallow */ }
        if (_currentStream is { } s)
        {
            try { s.Disconnect(); } catch { /* swallow */ }
            try { await s.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
        if (_acceptLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { /* swallow */ }
        }
        _cts.Dispose();
    }
}
