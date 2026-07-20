using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Vertex.Core.Discovery;
using Vertex.Service;
using Vertex.Service.Ipc;
using Vertex.Service.Storage;
using Vertex.Shared.Ipc;

var options = new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
};

var builder = Host.CreateApplicationBuilder(options);

builder.Services.AddWindowsService(o =>
{
    o.ServiceName = "Vertex VPN";
});

// Storage singletons.
builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<PasswordStore>();
builder.Services.AddSingleton<StateStore>();

// SRV discovery — single HttpClient pooled across the lifetime of the
// service, with a short request timeout enforced per-call by SrvResolver.
// Cache is layered on StateStore so admins can inspect / clear the SRV
// snapshot from the same state.json that holds LastGoodBroker etc.
builder.Services.AddSingleton<HttpClient>(_ => new HttpClient());
builder.Services.AddSingleton<ISrvCache, StateStoreSrvCache>();
builder.Services.AddSingleton<SrvResolver>(sp => new SrvResolver(
    http: sp.GetRequiredService<HttpClient>(),
    cache: sp.GetRequiredService<ISrvCache>(),
    log: sp.GetRequiredService<ILoggerFactory>().CreateLogger<SrvResolver>()));

// PipeServer wired to a TunnelEngine forwarder. We have to break the
// dependency cycle (engine wants pipe, pipe handler wants engine) by
// late-binding the dispatcher delegate via DI: PipeServer asks for a
// `Func<AppMessage, Task>` which is registered with a closure that
// resolves TunnelEngine from the service provider.
builder.Services.AddSingleton<PipeServer>(sp =>
{
    var lf = sp.GetRequiredService<ILoggerFactory>();
    var log = lf.CreateLogger<PipeServer>();
    var pipe = new PipeServer(
        onMessage: async msg => await sp.GetRequiredService<TunnelEngine>().HandleAppMessageAsync(msg).ConfigureAwait(false),
        pipeName:  null,
        log:       log);
    // Replay status to every newly-attached App — Phase 1.9 review MAJOR-6.
    pipe.OnClientConnected = () =>
    {
        _ = sp.GetRequiredService<TunnelEngine>().PushCurrentStatusAsync();
    };
    return pipe;
});

builder.Services.AddSingleton<TunnelEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TunnelEngine>());

builder.Logging.AddEventLog(o =>
{
    o.SourceName = "Vertex VPN";
});

var host = builder.Build();
await host.RunAsync();
