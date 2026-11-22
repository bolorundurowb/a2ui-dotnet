using System.Runtime.CompilerServices;

var expectedDynamicCode = args switch
{
    ["trimmed"] => true,
    ["aot"] => false,
    _ => throw new ArgumentException("Pass either trimmed or aot.")
};

if (RuntimeFeature.IsDynamicCodeSupported != expectedDynamicCode)
{
    throw new InvalidOperationException("The published app did not run in the expected deployment mode.");
}

CoreSmoke.Run();
Console.WriteLine($"Deployment smoke test passed in {args[0]} mode.");
