using System.Reflection;
using Aegis.Api.Controllers;
using Aegis.Api.Nodes;
using Aegis.Api.Nodes.Transport;
using Microsoft.Extensions.Options;
using Aegis.Application.Nodes;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Aegis.Application.Reminders;
using Aegis.Application.Places;
using Aegis.Infrastructure.Places;
using Aegis.Domain.Entities;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Diagnostics.Metrics;

// CI fixture only: loopback listener, disposable in-memory DB, local one-time bootstrap output.
// This project is not referenced by the solution or published API/NodeAdmin image.
if (args.Length != 1) return 2;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
var idleMode = Environment.GetEnvironmentVariable("AEGIS_IDLE_FIXTURE");
var metrics = new FixtureMetrics();
var withdrawalPump = new FixtureWithdrawalPumpState();
using var reminderCommandGate = new SemaphoreSlim(1, 1);
var reminderCommands = new Dictionary<(Guid NodeId, Guid CommandId), (FixtureReminderCommand Request, NodeNotificationCommand Command)>();
var backendOffline = new FixtureOfflineState(0, false);
var geofenceSyncFailure = "none";
var dropWithdrawalReceiptResponse = 0;
using var listener = new MeterListener();
listener.InstrumentPublished = (instrument, meterListener) => { if (instrument.Meter.Name is "Aegis.NodeTransport" or "Aegis.NodeTransport.Payload" or "Aegis.NodePushRegistration") meterListener.EnableMeasurementEvents(instrument); };
listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => metrics.Add(instrument.Name, measurement)); listener.Start();
builder.WebHost.UseUrls("http://127.0.0.1:18104");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddControllers().AddApplicationPart(typeof(NodesController).Assembly)
    .ConfigureApplicationPartManager(manager => { manager.FeatureProviders.Clear(); manager.FeatureProviders.Add(new NodeControllersOnly()); });
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
    ["NodeTransport:HeartbeatSeconds"] = idleMode == "before" ? "25" : idleMode == "after" ? "120" : "2",
    ["NodeTransport:TimeoutSeconds"] = idleMode == "before" ? "75" : idleMode == "after" ? "360" : "6",
    ["NodeTransport:MinimumHeartbeatSeconds"] = "1", ["NodeTransport:WatchdogSeconds"] = "1",
    ["NodeTransport:PersistSeconds"] = idleMode == "before" ? "120" : idleMode == "after" ? "300" : "2" });
builder.Services.AddNodeApi(builder.Configuration);
builder.Services.AddScoped<INodeTransportHistory, NodeTransportHistory>(); builder.Services.AddSingleton(TimeProvider.System);
var database = "native-ci-" + Guid.NewGuid();
builder.Services.AddDbContext<AegisDbContext>(o => o.UseInMemoryDatabase(database).AddInterceptors(new FixtureWrites(metrics)));
builder.Services.AddScoped<INodeRegistry, NodeRegistry>();
builder.Services.AddScoped<ReminderStore>(); builder.Services.AddScoped<IReminderStore>(p => p.GetRequiredService<ReminderStore>());
builder.Services.AddScoped<IReminderSeriesStore, ReminderSeriesStore>();
builder.Services.AddScoped<IReminderActionStore, ReminderActionStore>(); builder.Services.AddScoped<ReminderActionService>();
builder.Services.AddScoped<IReminderActionTokens, ReminderActionTokens>();
builder.Services.AddSingleton(metrics); builder.Services.AddSingleton(withdrawalPump);
builder.Services.AddScoped<ReminderWithdrawalReconciler>(p => new(p.GetRequiredService<AegisDbContext>(), p.GetRequiredService<INodeRegistry>(), new FixtureWithdrawalDispatcher(metrics), TimeProvider.System));
builder.Services.AddHostedService<FixtureWithdrawalPump>();
builder.Services.AddScoped<IPlaceService, PlaceService>();
builder.Services.AddOptions<LocationContextOptions>();
builder.Services.AddOptions<PlaceMapOptions>().Configure(o => o.GeocoderUrl = "");
builder.Services.AddScoped<PlaceMaps>();
builder.Services.AddHttpClient();
var app = builder.Build(); app.UseWebSockets();
app.Use(async (context, next) => {
    if (context.Request.Path.StartsWithSegments("/fixture")) { await next(); return; }
    var offline = Volatile.Read(ref backendOffline);
    if (Environment.TickCount64 < offline.Until && (!offline.AllowPairing || !PairingLifecycle(context.Request))) { context.Response.StatusCode = 503; return; }
    metrics.Add("http_requests", 1); metrics.Add("http_request_body_bytes", context.Request.ContentLength ?? 0);
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/api/nodes/geofences/sync") {
        metrics.Add("geofence_sync_requests", 1);
        var failure = Volatile.Read(ref geofenceSyncFailure);
        if (failure == "503") { context.Response.StatusCode = 503; return; }
        if (failure == "disconnect") { context.Abort(); return; }
    }
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/api/nodes/reminders/withdrawals/receipts") {
        metrics.Add("withdrawal_receipt_requests", 1);
        // After losing a committed response, keep transparent HTTP replays from
        // turning a lost-response test into a successful-retry test by accident.
        if (Volatile.Read(ref dropWithdrawalReceiptResponse) == -1) { metrics.Add("withdrawal_receipt_responses_unavailable", 1); context.Response.StatusCode = 503; return; }
    }
    if (context.WebSockets.IsWebSocketRequest) { await next(); return; }
    var original = context.Response.Body; await using var copy = new MemoryStream(); context.Response.Body = copy;
    try {
        await next();
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/api/nodes/reminders/withdrawals/receipts" && context.Response.StatusCode is >= 200 and < 300 && Interlocked.CompareExchange(ref dropWithdrawalReceiptResponse, -1, 1) == 1) {
            metrics.Add("withdrawal_receipt_responses_dropped", 1); context.Abort(); return;
        }
        metrics.Add("http_response_body_bytes", copy.Length); copy.Position = 0; await copy.CopyToAsync(original);
    }
    finally { context.Response.Body = original; }
});
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
app.MapGet("/api/health", () => new { status = "ok" });
// Loopback test executable only; not a production endpoint or new authorization path.
app.MapGet("/fixture/idle", () => metrics.Snapshot());
// Test-only policy change occurs between APKs, with the same disposable database/identity.
app.MapPost("/fixture/idle-policy", (FixtureIdlePolicy request, IOptions<NodeTransportOptions> options) => {
    if (request.Mode != "after") return Results.BadRequest();
    options.Value.TimeoutSeconds = 360; options.Value.PersistSeconds = 300; options.Value.HeartbeatSeconds = 120;
    Volatile.Write(ref idleMode, "after");
    return Results.Ok(new { mode = "after" });
});
app.MapPost("/fixture/backend-offline", (FixtureOffline request) => {
    if (request.Seconds is < 0 or > 300) return Results.BadRequest();
    Volatile.Write(ref backendOffline, new FixtureOfflineState(Environment.TickCount64 + request.Seconds * 1000L, request.AllowPairing));
    return Results.Ok(new { seconds = request.Seconds, allowPairing = request.AllowPairing });
});
app.MapPost("/fixture/geofence-sync-failure", (FixtureGeofenceSyncFailure request) => {
    if (request.Mode is not ("503" or "disconnect" or "none")) return Results.BadRequest();
    Volatile.Write(ref geofenceSyncFailure, request.Mode);
    return Results.Ok(new { mode = request.Mode });
});
app.MapPost("/fixture/withdrawal-receipt-failure", (FixtureWithdrawalReceiptFailure request) => {
    if (request.Mode is not ("disconnect_after_apply" or "none")) return Results.BadRequest();
    Interlocked.Exchange(ref dropWithdrawalReceiptResponse, request.Mode == "none" ? 0 : 1);
    return Results.Ok(new { mode = request.Mode });
});
// Explicitly opt in only after functional assertions and the native receipt.
// The real reconciler runs at its production five-second interval; only its
// downstream transport is simulated, without contacting FCM or a live socket.
app.MapPost("/fixture/withdrawal-pump", (FixtureWithdrawalPumpRequest request) => {
    if (request.Enabled && Volatile.Read(ref idleMode) != "after") return Results.BadRequest();
    withdrawalPump.Set(request.Enabled);
    return Results.Ok(new { enabled = request.Enabled, pollSeconds = 5, transport = "simulated_fcm" });
});
app.MapGet("/fixture/reminder-recipients", async (AegisDbContext db, CancellationToken ct) => await db.Set<ReminderRecipient>().AsNoTracking()
    .Select(r => new { r.ReminderId, r.Generation, r.NodeId, r.WithdrawnAt, r.WithdrawalQueuedAt, r.WithdrawalAttempts, r.WithdrawalCommandId, r.WithdrawalCommandExpiresAt, r.WithdrawalNextAttemptAt }).ToArrayAsync(ct));
app.MapPost("/fixture/reminder-command", async (FixtureReminderCommand request, AegisDbContext db, INodeLiveNotifications socket, IReminderActionTokens tokens, CancellationToken ct) => {
    if (request.NodeId == Guid.Empty || request.CommandId == Guid.Empty) return Results.BadRequest();
    if (!await db.Nodes.AnyAsync(n => n.Id == request.NodeId && n.Enabled && n.RevokedAt == null, ct)) return Results.NotFound();
    NodeNotificationCommand command;
    await reminderCommandGate.WaitAsync(ct);
    try {
        var key = (request.NodeId, request.CommandId);
        if (reminderCommands.TryGetValue(key, out var cached)) {
            if (cached.Request != request) return Results.Conflict(new { code = "fixture_command_id_conflict" });
            command = cached.Command;
        }
        else {
            if (request.ReminderId == Guid.Empty || request.Generation < 1 || request.Revision < 1 ||
                request.Capability is not (ReminderNotificationContract.NotifyCapability or ReminderNotificationContract.WithdrawCapability)) return Results.BadRequest();
            // Keep original envelopes for the entire isolated fixture lifetime.
            // Capacity rejects new identities; no expired command is evicted or reminted.
            if (reminderCommands.Count >= 1000) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var now = DateTimeOffset.UtcNow;
            NotificationInput input;
            if (request.Capability == ReminderNotificationContract.WithdrawCapability) input = new(null, null, request.ReminderId, Generation: request.Generation);
            else {
                var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == request.ReminderId, ct);
                if (reminder is null) {
                    reminder = new Reminder("Lembrete sintético do CI", now.AddSeconds(-1), "UTC", null, now.AddSeconds(-2));
                    // Test fixture selects the occurrence ID before attaching the entity. No
                    // test setter/hook is introduced into the production domain or native app.
                    typeof(AuditableEntity).GetProperty(nameof(AuditableEntity.Id))!.SetValue(reminder, request.ReminderId);
                    reminder.Claim(Guid.NewGuid(), now); reminder.BeginTrigger(now); reminder.Finish(null, true, now);
                    db.Reminders.Add(reminder); await db.SaveChangesAsync(ct);
                }
                input = new("Aegis", "Lembrete sintético do CI", request.ReminderId, request.Revision, request.Generation, tokens.Create(reminder, now.AddDays(7), request.NodeId));
            }
            command = new(request.CommandId, request.Capability, 1, now.AddMinutes(5), input);
            reminderCommands.Add(key, (request, command));
        }
    }
    finally { reminderCommandGate.Release(); }
    var result = await socket.SendAsync(request.NodeId, command, ct);
    if (request.Capability == "reminder.notify" && (result.Status is "success" or "duplicate") &&
        !await db.Set<ReminderRecipient>().AnyAsync(r => r.ReminderId == request.ReminderId && r.Generation == request.Generation && r.NodeId == request.NodeId, ct)) {
        var now = DateTimeOffset.UtcNow;
        var recipient = new ReminderRecipient(request.ReminderId, request.Generation, request.NodeId, now);
        recipient.Begin(now, command.CommandId, command.ExpiresAt);
        recipient.Result(result.Status, "live_websocket", false, now);
        db.Set<ReminderRecipient>().Add(recipient); await db.SaveChangesAsync(ct);
    }
    return Results.Ok(new { status = result.Status, reminderId = request.ReminderId });
});
app.MapGet("/fixture/reminder-actions", async (AegisDbContext db, CancellationToken ct) => await db.Set<ReminderActionReceipt>().AsNoTracking()
    .Select(r => new { actionId = r.Id, r.ReminderId, r.ExpectedRevision, r.Generation, r.Action, r.Applied }).ToArrayAsync(ct));
// Isolated test fixture only. Counts contain no endpoint token or credential.
app.MapGet("/fixture/nodes/{id:guid}/push-count", async (Guid id, AegisDbContext db) => new {
    count = await db.NodePushRegistrations.CountAsync(r => r.NodeId == id),
    valid = await db.NodePushRegistrations.CountAsync(r => r.NodeId == id && r.ExpiresAt > DateTimeOffset.UtcNow)
});
using (var scope = app.Services.CreateScope())
{
    var registry = scope.ServiceProvider.GetRequiredService<INodeRegistry>();
    var code = await registry.CreateCodeAsync(null);
    var replacement = await registry.CreateCodeAsync(null);
    // Both are the existing issuerless bootstrap procedure. The reserve survives
    // revocation of the first paired Node; no test bypass or HTTP endpoint exists.
    await WriteBootstrapCode(args[0] + ".replacement", replacement.Code);
    await WriteBootstrapCode(args[0], code.Code); // Main file is the readiness marker.
}
await app.RunAsync(); return 0;
static async Task WriteBootstrapCode(string path, string code)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    await using var file = new FileStream(path, options);
    await using var writer = new StreamWriter(file);
    await writer.WriteAsync(code);
}
static bool PairingLifecycle(HttpRequest request)
{
    var path = request.Path.Value;
    if (HttpMethods.IsGet(request.Method)) return path == "/api/nodes/me";
    if (!HttpMethods.IsPost(request.Method)) return false;
    if (path is "/api/nodes/pairing-codes" or "/api/nodes/pair" or "/api/nodes/pair/finalize") return true;
    const string prefix = "/api/nodes/", suffix = "/revoke";
    return path is not null && path.Length == prefix.Length + 36 + suffix.Length && path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal) &&
        Guid.TryParseExact(path[prefix.Length..^suffix.Length], "D", out _);
}
sealed class NodeControllersOnly : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo type) => type.AsType() == typeof(NodesController) || type.AsType() == typeof(NodePairingController) || type.AsType() == typeof(NodeTransportController) ||
        type.AsType() == typeof(RemindersController) || type.AsType() == typeof(RemindersReconcileController) || type.AsType() == typeof(ReminderWithdrawalReceiptsController) || type.AsType() == typeof(ScopedReminderActionsController) ||
        type.AsType() == typeof(PlacesController) || type.AsType() == typeof(LocationController) || type.AsType() == typeof(GeofencesController);
}
sealed class FixtureMetrics
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> values = new();
    public void Add(string name, long value) => values.AddOrUpdate(name, value, (_, current) => current + value);
    public object Snapshot() => values.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
}
sealed class FixtureWrites(FixtureMetrics metrics) : SaveChangesInterceptor
{
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) { metrics.Add("entities_written", result); return ValueTask.FromResult(result); }
}
sealed record FixtureReminderCommand(Guid NodeId, Guid ReminderId, Guid CommandId, string Capability = "reminder.notify", int Revision = 1, int Generation = 1);

sealed record FixtureOffline(int Seconds, bool AllowPairing = false);
sealed record FixtureOfflineState(long Until, bool AllowPairing);

sealed record FixtureIdlePolicy(string Mode);

sealed record FixtureGeofenceSyncFailure(string Mode);

sealed record FixtureWithdrawalReceiptFailure(string Mode);

sealed record FixtureWithdrawalPumpRequest(bool Enabled);

sealed class FixtureWithdrawalPumpState
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private int enabled;
    public bool Enabled => Volatile.Read(ref enabled) != 0;
    public void Set(bool value)
    {
        Interlocked.Exchange(ref enabled, value ? 1 : 0);
        if (value && signal.CurrentCount == 0) { try { signal.Release(); } catch (SemaphoreFullException) { } }
    }
    public Task WaitAsync(CancellationToken ct) => signal.WaitAsync(ct);
}

sealed class FixtureWithdrawalPump(IServiceScopeFactory scopes, FixtureWithdrawalPumpState state, FixtureMetrics metrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await state.WaitAsync(stoppingToken);
                while (state.Enabled && !stoppingToken.IsCancellationRequested)
                {
                    metrics.Add("withdrawal_reconcile_cycles", 1);
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<ReminderWithdrawalReconciler>().ReconcileAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch { metrics.Add("withdrawal_reconcile_failures", 1); }
                    await Task.Delay(TimeSpan.FromSeconds(5), TimeProvider.System, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

sealed class FixtureWithdrawalDispatcher(FixtureMetrics metrics) : INodeNotificationDispatcher
{
    public Task<NotificationDispatchResult> DispatchToNodeAsync(Guid nodeId, NodeNotificationCommand command, CancellationToken ct = default)
    {
        metrics.Add("withdrawal_reconcile_dispatches", 1);
        return Task.FromResult(new NotificationDispatchResult(new(nodeId, "Synthetic fixture recipient"), "backgroundReachable", "fcm", "accepted", command.CommandId));
    }
    public Task<NotificationDispatchResult> DispatchAsync(Guid actor, NotificationShowRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}
