# A2UI over ASP.NET Core SSE

The scripted agent opens a Basic Catalog appointment form. A renderer applies
each A2UI 1.0 envelope in order, lets its user edit the name and posts the
submit action. The agent sends a confirmation and deletes the surface. The
sample uses the Core wire codec and Agent negotiation, streaming and inbound
routing APIs. Run it with .NET 8 or later. It needs no model credentials.

```powershell
dotnet run --project samples/AspNetCoreSse -c Release -- --urls http://127.0.0.1:5080
```

In a second terminal, run the headless HTTP smoke client.

```powershell
./samples/AspNetCoreSse/Test-Sample.ps1
```

## Stream and metadata

Open `GET /a2ui/events` with an `A2UI-Renderer-Capabilities` header containing
the compact JSON capabilities object. Advertise the Basic Catalog with
`{"1.0":{"supportedCatalogIds":["https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json"]}}`.
The server validates the metadata, negotiates A2UI 1.0 and requires the Basic
Catalog. It returns HTTP 400 if negotiation fails. The first SSE event carries
the session ID, bearer token and selected `agentCapabilities`. A renderer
should keep the token in memory and send it only to this service. Use an HTTP
client that supports custom headers and incremental response consumption.

Each event has a connection-local numeric `id` and a JSON value in its `data`
field. The writer prefixes every data line and separates events with a blank
line. Apply `a2ui` envelopes in delivery order, including progressive component
updates. `text` events contain conversational strings. `turn-complete` marks
when the first turn can accept an action. `end` marks a completed conversation.
A `stream-error` event carries a generic error code.

Post `application/json` to `/a2ui/sessions/{sessionId}/actions` after
`turn-complete`. Use `Authorization: Bearer <token>` and include these two
members in the request body.

- `message` contains a complete renderer-to-agent A2UI action envelope. Submit
  using the action named `submit` from the component with ID `submit`.
- `rendererDataModel` contains the renderer's A2UI 1.0 data-model metadata
  (`{"version":"1.0","surfaces":{"<surfaceId>":{"name":"Ada"}}}`). The surface
  opted in using `sendDataModel`. The name must match the action context.

HTTP 202 means the sample accepted the first submit for delivery on the open
stream. HTTP 400 rejects malformed or out-of-scope input. HTTP 404 covers
missing sessions and invalid bearer tokens. HTTP 409 covers submits outside
the action window while the session still exists. The service keeps a single
pending submission per connection.

## Cancellation and reconnect

The writer awaits the response flush before pulling the next scripted chunk
and forwards cancellation to both the parser and the HTTP write. Disconnect,
host shutdown and the two-minute session limit all release the per-connection
mirror and invalidate the return channel.

After a disconnect or an incomplete stream, discard the old renderer state
and issue a fresh GET with the same capabilities. Each connection starts a new
session and uses a freshly generated surface ID. The service does not replay
events or resume from `Last-Event-ID`. The sample has no durable storage or
external booking side effects.

Use the sample on loopback. A deployed application should use TLS and bind
session ownership to its own authentication. The sample logs static rejection
and cancellation messages and leaves action, data-model and token values out
of its diagnostics.
