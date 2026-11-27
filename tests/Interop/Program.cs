using System.Text;
using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Pass the form fixture, output transcript path and optional Python transcript path.");

using var peer = args.Length == 3 ? JsonDocument.Parse(File.ReadAllText(args[2])) : null;
var surfaceId = peer?.RootElement.GetProperty("opening")[0].GetProperty("createSurface").GetProperty("surfaceId").GetString()
    ?? $"appointment-{Guid.NewGuid():N}";
using var fixture = JsonDocument.Parse(File.ReadAllText(args[0]).Replace("APPOINTMENT_SURFACE_ID", surfaceId, StringComparison.Ordinal));
var catalogs = new CatalogRegistry();
using var agent = new RendererState(catalogs);
using var session = new SurfaceSession(catalogs);
using var resolver = new NodeResolver(session, catalogs, surfaceId, timeProvider: new TranscriptClock());

var opening = Compile("opening");
var receivedOpening = Received("opening", opening);
Apply(receivedOpening[0]);
var name = Node("name");
var initialSlots = Slots();
Require(initialSlots.SequenceEqual(new[] { "Morning", "Afternoon" }), "Initial slot order");
Apply(receivedOpening[1]);
Require(!name.IsValid && name.ValidationErrors.SequenceEqual(new[] { "Enter a name" }), "Empty name check");
Apply(receivedOpening[2]);
var reorderedSlots = Slots();
Require(reorderedSlots.SequenceEqual(new[] { "Afternoon", "Morning" }), "Reordered slots");

Check(resolver.Write(name, "value", JsonSerializer.SerializeToElement("Ada")));
Require(name.IsValid && name.Props["value"].Value!.Value.ValueEquals("Ada"), "Edited name check");
var rendererData = session.GetRendererDataModel()!.Value;
Require(rendererData.GetProperty("surfaces").GetProperty(surfaceId).GetProperty("name").ValueEquals("Ada"),
    "Synchronised local edit");
var action = resolver.Invoke(Node("submit"), "action");
if (action.Error is not null || action.Message is not { } submit)
    throw new InvalidOperationException($"Submit action failed: {action.Error}");

var routed = false;
var inbound = new RendererMessageProcessor(catalogs, onAction: (message, _) =>
{
    routed = message.Payload.GetProperty("name").ValueEquals("book") &&
        message.Payload.GetProperty("surfaceId").ValueEquals(surfaceId) &&
        message.Payload.GetProperty("sourceComponentId").ValueEquals("submit") &&
        message.Payload.GetProperty("context").GetProperty("name").ValueEquals("Ada");
    return ValueTask.FromResult(routed);
});
var receivedAction = peer?.RootElement.GetProperty("action") ?? submit.Envelope;
var dispatch = await inbound.ProcessAsync(Bytes(receivedAction));
Check(dispatch.Diagnostic);
Require(dispatch.Handled && routed, "Agent received the resolved action");
var rpc = await RpcReplay.Run(session, surfaceId,
    Path.Combine(Path.GetDirectoryName(args[0])!, "rpc-catalog.json"), peer?.RootElement.GetProperty("rpc"));

var confirmation = Compile("confirmation");
var receivedConfirmation = Received("confirmation", confirmation);
Apply(receivedConfirmation[0]);
var confirmed = Text(Node("label"), "text");
Require(confirmed == "Appointment confirmed", "Confirmation");
Apply(receivedConfirmation[1]);
var disposed = !session.TryGetSurface(surfaceId, out _) && resolver.Nodes.Count == 0 && session.GetRendererDataModel() is null;
Require(disposed, "Surface and bindings disposed");

var transcript = new
{
    opening = opening.Select(message => message.Envelope),
    initialSlots,
    reorderedSlots,
    rendererData,
    action = submit.Envelope,
    rpc,
    confirmation = confirmation.Select(message => message.Envelope),
    confirmed,
    disposed
};
File.WriteAllText(args[1], JsonSerializer.Serialize(transcript, new JsonSerializerOptions { WriteIndented = true }));

IReadOnlyList<WireMessage> Received(string section, IReadOnlyList<WireMessage> local)
{
    if (peer is null)
        return local;

    return peer.RootElement.GetProperty(section).EnumerateArray().Select(value =>
    {
        Check(WireMessage.TryDecode(Bytes(value), WireDirection.AgentToRenderer, out var message, out var diagnostic)
            ? null : diagnostic);
        return message!;
    }).ToArray();
}

IReadOnlyList<WireMessage> Compile(string section)
{
    Check(DirectJson.Compile(fixture.RootElement.GetProperty(section).GetRawText(), catalogs, out var messages));
    Check(agent.Prepare(messages, out var pending));
    using var turn = pending ?? throw new InvalidOperationException("The agent produced no turn.");
    turn.Commit();
    foreach (var message in messages)
        turn.Acknowledge(message);
    return messages;
}

void Apply(WireMessage message) => Check(session.Apply(message));
ResolvedNode Node(string id) => resolver.Nodes.Single(node => node.ComponentId == id);
string?[] Slots() => resolver.Nodes.Where(node => node.ComponentId == "slot").Select(node => Text(node, "text")).ToArray();
static string? Text(ResolvedNode node, string property) => node.Props[property].Value?.GetString();
static byte[] Bytes(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());
static void Check(A2uiDiagnostic? diagnostic) => Require(diagnostic is null, diagnostic?.ToString() ?? "");
static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class TranscriptClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
