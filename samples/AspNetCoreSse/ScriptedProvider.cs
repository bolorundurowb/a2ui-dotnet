using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class ScriptedProvider
{
    internal static IEnumerable<string> Open(string surfaceId) => Chunks(DirectJson.Wrap([
        new("Enter your name to request an appointment."),
        new($$$"""
            [
              {"version":"1.0","createSurface":{"surfaceId":"{{{surfaceId}}}","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}","sendDataModel":true}},
              {"version":"1.0","updateComponents":{"surfaceId":"{{{surfaceId}}}","components":[
                {"id":"root","component":"Column","children":["name","submit"]},
                {"id":"name","component":"TextField","label":"Name","value":{"@path":"/name"}},
                {"id":"submit","component":"Button","child":"label","action":{
                  "event":{"name":"submit","context":{"name":{"@path":"/name"} } }
                }},
                {"id":"label","component":"Text","text":"Request appointment"}
              ]}},
              {"version":"1.0","updateDataModel":{"surfaceId":"{{{surfaceId}}}","path":"/name","value":""}}
            ]
            """, IsPayload: true)
    ]));

    internal static IEnumerable<string> Confirm(string surfaceId, string name) => Chunks(DirectJson.Wrap([
        new($$$"""
            [
              {"version":"1.0","updateComponents":{"surfaceId":"{{{surfaceId}}}","components":[
                {"id":"root","component":"Text","text":{{{JsonSerializer.Serialize($"Appointment requested for {name}.")}}} }
              ]}},
              {"version":"1.0","deleteSurface":{"surfaceId":"{{{surfaceId}}}"}}
            ]
            """, IsPayload: true)
    ]));

    private static IEnumerable<string> Chunks(string response)
    {
        const int chunkSize = 48;
        for (var offset = 0; offset < response.Length; offset += chunkSize)
            yield return response.Substring(offset, Math.Min(chunkSize, response.Length - offset));
    }
}
