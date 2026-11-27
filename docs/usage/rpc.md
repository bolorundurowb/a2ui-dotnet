# Function RPC

Start with an `A2UI.Agent` console consumer using the [local SDK packages](README.md#use-the-local-packages). Register function schemas on both sides and their implementations on the owning side. This example calls a renderer function from the agent and an agent function from the renderer using a catalogue that both peers share.

```csharp
using System.Text;
using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

const string catalogId = "example:appointment-rpc";
using var catalogue = JsonDocument.Parse("""
    {
      "protocolVersion":"1.0","catalogId":"example:appointment-rpc","components":{},
      "functions":{
        "bookingStatus":{
          "type":"object","returnType":"string","allowedCallers":"rendererOnly",
          "properties":{"@call":{"const":"bookingStatus"},"args":{"type":"object"}},
          "required":["@call","args"]
        },
        "selectedSlot":{
          "type":"object","returnType":"string","allowedCallers":"agentOnly",
          "properties":{"@call":{"const":"selectedSlot"},"args":{"type":"object"}},
          "required":["@call","args"]
        }
      }
    }
    """);
var catalogues = new CatalogRegistry(includeBasicCatalog: false);
var handlers = new AgentFunctions();
if (handlers.Register(catalogues, catalogue.RootElement, [
        new("bookingStatus", (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement("Confirmed")))
    ]) is { } registration)
    throw new InvalidOperationException(registration.ToString());
if (RequestProcessor.Fixed(catalogues, out var request) is { } configuration)
    throw new InvalidOperationException(configuration.ToString());

var functions = new FunctionEvaluator(catalogues);
functions.RegisterHostFunction(catalogId, "selectedSlot", _ => JsonSerializer.SerializeToElement("09:00"));
var renderer = new RendererFunctionHandler(catalogues, functions);
using var rendererCalls = request!.CreateRendererFunctionClient(defaultTimeout: TimeSpan.FromSeconds(5));
var inbound = request.CreateMessageProcessor(handlers,
    onRendererFunctionResponse: (reply, _) => ValueTask.FromResult(rendererCalls.TryHandleResponse(reply)));

var selected = rendererCalls.Call(catalogId, "selectedSlot");
var rendererReply = renderer.Handle(selected.Message);
if (!(await inbound.ProcessAsync(Bytes(rendererReply))).Handled || !(await selected.Result).ValueEquals("09:00"))
    throw new InvalidOperationException("The renderer result was not correlated.");

var outbound = new List<WireMessage>();
using var agentCalls = new AgentFunctionClient(outbound.Add);
using var agentCall = JsonDocument.Parse("""
    {"@call":"bookingStatus","catalogId":"example:appointment-rpc","args":{}}
    """);
var status = agentCalls.CallAsync("appointment", agentCall.RootElement,
    Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(5));
var received = await inbound.ProcessAsync(Bytes(outbound.Single()));
if (received.Reply is not { } agentReply || !agentCalls.TryHandleResponse(agentReply) ||
    !(await status).ValueEquals("Confirmed"))
    throw new InvalidOperationException("The agent result was not correlated.");

Console.WriteLine("Function RPC passed in both directions.");

static byte[] Bytes(WireMessage message) => Encoding.UTF8.GetBytes(message.Envelope.GetRawText());
```

Keep the call clients scoped to the conversation and dispose them when it closes. Send the call message on the same owner-scoped channel used for the other messages and route each reply to the originating client. Use fresh call IDs and configure pending-call limits and deadlines for your host. For a surface-scoped renderer function, supply the renderer's `SurfaceSession` to `RendererFunctionHandler`.

Callers and argument schemas constrain which calls the SDK accepts. The host still controls authentication, ownership, external effects and how renderer-only functions obtain user activation. Pass proof of user activation only when the renderer has received a corresponding interaction.

See the [compatibility matrix](../compatibility.md#agent-calls-to-renderer-functions) for the pinned renderer RPC cases and the [headless Python replay](../../tests/Interop/README.md) for cross-language success and failure cases.
