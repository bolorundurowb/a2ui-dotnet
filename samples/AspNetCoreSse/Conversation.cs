using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal sealed record ActionRequest(JsonElement Message, JsonElement RendererDataModel);

internal sealed class Conversation(RequestProcessor processor)
{
    private readonly TaskCompletionSource<string> _submission = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private bool _open;

    internal string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string SurfaceId { get; } = Guid.NewGuid().ToString("N");

    internal bool Authenticate(string authorization) => authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
        authorization.Length == 7 + Token.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(authorization[7..]), Encoding.ASCII.GetBytes(Token));

    internal void Open()
    {
        lock (_gate)
            _open = true;
    }

    internal void Close()
    {
        lock (_gate)
            _open = false;
    }

    internal Task<string> WaitForActionAsync(CancellationToken cancellation) => _submission.Task.WaitAsync(cancellation);

    internal async Task<int> PostAsync(ActionRequest request, CancellationToken cancellation)
    {
        if (request.Message.ValueKind != JsonValueKind.Object || request.RendererDataModel.ValueKind != JsonValueKind.Object ||
            !WireMetadata.TryDecode(Encoding.UTF8.GetBytes(request.RendererDataModel.GetRawText()),
                WireMetadataKind.RendererDataModel, out var metadata, out _))
            return StatusCodes.Status400BadRequest;

        var surfaces = metadata!.Value.GetProperty("surfaces");
        // This conversation owns exactly one surface and it has opted into data-model synchronisation.
        if (surfaces.EnumerateObject().Count() != 1 || !surfaces.TryGetProperty(SurfaceId, out var dataModel) ||
            !TryReadName(dataModel, out var name))
            return StatusCodes.Status400BadRequest;

        var status = StatusCodes.Status400BadRequest;
        var inbound = processor.CreateMessageProcessor(onAction: (message, _) =>
        {
            var action = message.Payload;
            if (!action.GetProperty("surfaceId").ValueEquals(SurfaceId) ||
                !action.GetProperty("sourceComponentId").ValueEquals("submit") ||
                !action.GetProperty("name").ValueEquals("submit") ||
                !action.TryGetProperty("context", out var context) || !TryReadName(context, out var submittedName) ||
                !string.Equals(name, submittedName, StringComparison.Ordinal))
                return ValueTask.FromResult(false);

            lock (_gate)
            {
                cancellation.ThrowIfCancellationRequested();
                status = _open && _submission.TrySetResult(name) ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict;
            }
            return ValueTask.FromResult(true);
        });
        await inbound.ProcessAsync(Encoding.UTF8.GetBytes(request.Message.GetRawText()), cancellation);
        return status;
    }

    private static bool TryReadName(JsonElement model, out string name)
    {
        name = model.ValueKind == JsonValueKind.Object && model.TryGetProperty("name", out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
        return name.Length is > 0 and <= 120;
    }
}
