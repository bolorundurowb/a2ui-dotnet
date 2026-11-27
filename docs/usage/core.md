# Core quickstart

Start with an `A2UI.Core` console consumer using the [local SDK packages](README.md#use-the-local-packages). This example creates a transport-independent renderer session, registers a custom catalogue, applies a mixed-catalogue form, and dispatches an action after a local edit. In a renderer, take the input bytes from your transport and use the resolved nodes to update platform controls.

```csharp
using System.Text.Json;
using A2UI.Core;

const string customCatalogue = "example:appointment";
var catalogues = new CatalogRegistry();
if (!catalogues.TryRegister("""
    {
      "protocolVersion":"1.0",
      "catalogId":"example:appointment",
      "components":{
        "Label":{
          "type":"object",
          "properties":{
            "component":{"const":"Label"},
            "text":{"$ref":"common_types.json#/$defs/DynamicString"}
          },
          "required":["component","text"]
        }
      }
    }
    """u8.ToArray(), out var registration))
    throw new InvalidOperationException(registration!.ToString());

using var session = new SurfaceSession(catalogues, GraphValidationOptions.Strict);
Apply("""
    {
      "version":"1.0",
      "createSurface":{
        "surfaceId":"appointment",
        "catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json",
        "sendDataModel":true,
        "dataModel":{"name":""},
        "components":[
          {"id":"root","component":"Column","children":["heading","name","submit"]},
          {"id":"heading","component":"Label","@catalogId":"example:appointment","text":"Appointment form"},
          {"id":"name","component":"TextField","label":"Name","value":{"@path":"/name"}},
          {"id":"submit","component":"Button","child":"submitLabel","action":{
            "event":{"name":"submit","context":{"name":{"@path":"/name"}}}
          }},
          {"id":"submitLabel","component":"Text","text":"Request appointment"}
        ]
      }
    }
    """u8.ToArray());

using var resolver = new NodeResolver(session, catalogues, "appointment");
var field = resolver.Nodes.Single(node => node.ComponentId == "name");
var submit = resolver.Nodes.Single(node => node.ComponentId == "submit");

if (resolver.Write(field, "value", JsonSerializer.SerializeToElement("Ada")) is { } writeError)
    throw new InvalidOperationException(writeError.ToString());

var action = resolver.Invoke(submit, "action", userInitiated: true);
if (action.Message is not { } message)
    throw new InvalidOperationException(action.Error?.ToString() ?? "No action message.");

var data = session.GetRendererDataModel() ?? throw new InvalidOperationException("No data-model metadata.");
if (catalogues.TryGetDefinition(customCatalogue, out _) &&
    message.Payload.GetProperty("context").GetProperty("name").ValueEquals("Ada") &&
    data.Value.GetProperty("surfaces").GetProperty("appointment").GetProperty("name").ValueEquals("Ada"))
    Console.WriteLine("Core action and data-model sync passed.");
else
    throw new InvalidOperationException("The edited name was not captured.");

void Apply(byte[] utf8)
{
    if (!WireMessage.TryDecode(utf8, WireDirection.AgentToRenderer, out var inbound, out var decodeError))
        throw new InvalidOperationException(decodeError!.ToString());
    if (session.Apply(inbound!) is { } validationError)
        throw new InvalidOperationException(validationError.ToString());
}
```

- Share a registry configured by the host. Install it before opening sessions. `catalogId` supplies the default for the surface and `@catalogId` selects a different registered catalogue for one item. The registry resolves supported schema references offline. Host code supplies any platform widget needed by the custom component.
- Serialise messages and local edits within each session. Strict validation requires a complete reachable graph in each component batch. The default progressive mode allows references to later components. Dispose resolvers and the session when the renderer closes.
- Drive controls from `NodeResolver.Nodes` and its change events. Write to writable properties through the resolver so a templated item keeps its scope. Pass `userInitiated: true` only for a genuine user interaction. Forward the action and opted-in data-model metadata to the owning agent.
- Inspect `A2uiDiagnostic` and `RendererError` results before continuing a rejected turn. A network host should log only the diagnostic fields its policy allows.

Continue with the [Agent quickstart](agent.md), [RPC example](rpc.md) or [compatibility matrix](../compatibility.md).
