using System.Text.Json;

namespace A2UI.Core;

/// <summary>A call to a function that the host implements.</summary>
/// <param name="CatalogId">The catalog the call resolved to.</param>
/// <param name="Name">The function name.</param>
/// <param name="Args">The resolved arguments. Bindings and nested calls are already replaced by their values.</param>
/// <param name="SurfaceId">The surface the call runs on, or an empty string when it runs outside a surface.</param>
/// <param name="UserInitiated">Whether a user gesture started the call.</param>
public sealed record HostFunctionCall(
    string CatalogId, string Name, IReadOnlyDictionary<string, JsonElement> Args, string SurfaceId, bool UserInitiated);

/// <summary>Runs a host-implemented function and returns its value, or a JSON null when it has none.</summary>
/// <param name="call">The call to run.</param>
/// <returns>The result of the call.</returns>
public delegate JsonElement HostFunction(HostFunctionCall call);

internal enum CallRoute
{
    Local,
    Agent,
    Refused
}