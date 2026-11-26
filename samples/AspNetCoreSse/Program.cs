using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
var app = builder.Build();
var catalogs = new CatalogRegistry();
var sessions = new ConcurrentDictionary<Guid, Conversation>();
var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/a2ui/events", async (HttpContext http) =>
{
    var response = http.Response;
    response.Headers.CacheControl = "no-store";
    if (!WireMetadata.TryDecode(Encoding.UTF8.GetBytes(http.Request.Headers["A2UI-Renderer-Capabilities"].ToString()),
            WireMetadataKind.RendererCapabilities, out var capabilities, out _) ||
        RequestProcessor.Negotiate(catalogs, capabilities!.Value, acceptInline: false, out var processor) is not null ||
        !processor!.CatalogIds.Contains(CatalogRegistry.BasicCatalogId))
    {
        app.Logger.LogInformation("Rejected renderer capabilities.");
        response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var sessionId = Guid.NewGuid();
    var conversation = new Conversation(processor);
    if (!sessions.TryAdd(sessionId, conversation))
        throw new InvalidOperationException("The session identifier was already in use.");

    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, app.Lifetime.ApplicationStopping);
    lifetime.CancelAfter(TimeSpan.FromMinutes(2));
    var cancellation = lifetime.Token;
    response.ContentType = "text/event-stream";
    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    using var state = processor.CreateSession();
    var events = new SseEvents(response);
    try
    {
        await events.WriteAsync("session", JsonSerializer.Serialize(new
        {
            sessionId,
            conversation.Token,
            agentCapabilities = new Dictionary<string, object>
            {
                [WireMessage.Version] = new { supportedCatalogIds = processor.CatalogIds, acceptsInlineCatalogs = false }
            }
        }, webJson), cancellation);

        await events.StreamAsync(state, ScriptedProvider.Open(conversation.SurfaceId), cancellation);
        conversation.Open();
        await events.WriteAsync("turn-complete", "null", cancellation);

        var name = await conversation.WaitForActionAsync(cancellation);
        await events.StreamAsync(state, ScriptedProvider.Confirm(conversation.SurfaceId, name), cancellation);
        await events.WriteAsync("end", "null", cancellation);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        app.Logger.LogInformation("The SSE session ended before delivery completed.");
    }
    catch (IOException) when (http.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogInformation("The SSE session ended before delivery completed.");
    }
    catch (InvalidDataException)
    {
        app.Logger.LogWarning("The scripted provider returned an invalid response.");
        await events.WriteAsync("stream-error", "\"invalid-response\"", cancellation);
    }
    finally
    {
        conversation.Close();
        sessions.TryRemove(sessionId, out _);
    }
});

app.MapPost("/a2ui/sessions/{sessionId:guid}/actions", async (Guid sessionId, HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    if (!sessions.TryGetValue(sessionId, out var conversation) ||
        !conversation.Authenticate(http.Request.Headers.Authorization.ToString()))
        return Results.NotFound();
    if (!http.Request.HasJsonContentType())
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    int status;
    try
    {
        var request = await http.Request.ReadFromJsonAsync<ActionRequest>(http.RequestAborted);
        status = request is null ? StatusCodes.Status400BadRequest :
            await conversation.PostAsync(request, http.RequestAborted);
    }
    catch (JsonException)
    {
        status = StatusCodes.Status400BadRequest;
    }
    catch (BadHttpRequestException error)
    {
        status = error.StatusCode;
    }

    if (status == StatusCodes.Status400BadRequest)
        app.Logger.LogInformation("Rejected renderer action or data-model metadata.");
    return Results.StatusCode(status);
});

app.Run();
