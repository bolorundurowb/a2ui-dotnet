namespace A2UI.Core;

/// <summary>A validation failure at a location in an A2UI document.</summary>
/// <param name="Code">The machine-readable reason for the failure.</param>
/// <param name="Path">The instance JSON Pointer. An empty string identifies the root.</param>
/// <param name="Message">A description of the failure.</param>
public sealed record A2uiDiagnostic(string Code, string Path, string Message);
