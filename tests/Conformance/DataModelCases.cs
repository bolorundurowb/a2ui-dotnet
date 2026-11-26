using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

/// <summary>Runs the pinned <c>data_model.yaml</c> cases through the public <see cref="DataModel"/> API.</summary>
internal static class DataModelCases
{
    internal const string Path = "conformance/core/data_model.yaml";

    internal static void Run(JsonElement test)
    {
        var model = test.TryGetProperty("initial", out var initial) ? new DataModel(initial) : new DataModel();
        var notified = new List<string>();
        var watched = new List<(string Path, DataSubscription Subscription)>();
        if (test.TryGetProperty("watch", out var watch))
        {
            foreach (var item in watch.EnumerateArray())
            {
                var watchPath = item.GetString()!;
                Require(model.Observe(watchPath, _ => notified.Add(watchPath), out var subscription) is null && subscription is not null,
                    $"Could not observe {watchPath}.");
                watched.Add((watchPath, subscription!));
            }
        }

        JsonNode? lastInput = null;
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            notified.Clear();
            var op = step.GetProperty("op").GetString();
            switch (op)
            {
                case "get":
                    Get(model, step);
                    continue;
                case "set":
                    // The input is a mutable tree whose document is released after the call, so a model that kept a reference would fail.
                    lastInput = JsonNode.Parse(step.GetProperty("value").GetRawText());
                    using (var document = JsonDocument.Parse(lastInput?.ToJsonString() ?? "null"))
                        CheckOutcome(step, model.Set(step.GetProperty("path").GetString()!, document.RootElement));
                    break;
                case "delete":
                    CheckOutcome(step, model.Delete(step.GetProperty("path").GetString()!));
                    break;
                case "dispose":
                    model.Dispose();
                    break;
                case "mutate_input":
                    Require(lastInput is JsonObject input, "mutate_input needs a preceding set of an object.");
                    ((JsonObject)lastInput!)[step.GetProperty("path").GetString()!.TrimStart('/')] = JsonNode.Parse(step.GetProperty("value").GetRawText());
                    break;
                default:
                    throw new InvalidDataException($"Unknown data model step {op}.");
            }

            if (step.TryGetProperty("expect_notified", out var expectedNotified))
                Require(notified.SequenceEqual(expectedNotified.EnumerateArray().Select(item => item.GetString()!)),
                    $"Notified [{string.Join(", ", notified)}] instead of {expectedNotified.GetRawText()}.");
            if (step.TryGetProperty("expect_values", out var expectedValues))
            {
                foreach (var expected in expectedValues.EnumerateObject())
                {
                    var observed = watched.First(item => item.Path == expected.Name).Subscription.Value;
                    Require(Equal(observed, expected.Value), $"Observed value at {expected.Name} is {observed}.");
                }
            }
        }
    }

    private static void Get(DataModel model, JsonElement step)
    {
        var path = step.GetProperty("path").GetString()!;
        var diagnostic = model.Get(path, out var value);
        if (step.TryGetProperty("expect_error", out var expectedError))
        {
            CheckError(expectedError, diagnostic);
            return;
        }

        Require(diagnostic is null, diagnostic?.ToString() ?? "");
        if (step.TryGetProperty("expect_absent", out _))
        {
            // The upstream cases cannot tell a null from a missing value. Padding a list leaves null in the gap, and the case treats it as absent.
            Require(value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null, $"Expected no value at {path}, found {value}.");
        }
        else if (step.TryGetProperty("expect_type", out var expectedType))
        {
            var kind = expectedType.GetString() == "list" ? JsonValueKind.Array : JsonValueKind.Object;
            Require(value.ValueKind == kind, $"Expected {expectedType.GetString()} at {path}, found {value.ValueKind}.");
        }
        else
        {
            Require(value.ValueKind != JsonValueKind.Undefined && Equal(value, step.GetProperty("expect")),
                $"Expected {step.GetProperty("expect").GetRawText()} at {path}, found {value}.");
        }
    }

    private static void CheckOutcome(JsonElement step, A2uiDiagnostic? diagnostic)
    {
        if (step.TryGetProperty("expect_error", out var expectedError))
            CheckError(expectedError, diagnostic);
        else
            Require(diagnostic is null, diagnostic?.ToString() ?? "");
    }

    private static void CheckError(JsonElement expected, A2uiDiagnostic? diagnostic)
    {
        Require(diagnostic is not null, "Expected the operation to be rejected.");
        Require(RendererError.FromDiagnostic(diagnostic!).Category == expected.GetProperty("category").GetString(), diagnostic!.ToString());
        if (expected.TryGetProperty("message", out var message))
            Require(diagnostic!.Message.Contains(message.GetString()!, StringComparison.Ordinal), diagnostic.ToString());
    }

    private static bool Equal(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
