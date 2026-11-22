using System.Reflection;

if (args.Length != 2 || !Version.TryParse(args[0], out var expectedRuntime))
{
    throw new ArgumentException("Pass the exact runtime version and package ID.");
}

if (Environment.Version != expectedRuntime)
{
    throw new InvalidOperationException($"Expected .NET {expectedRuntime}, running {Environment.Version}.");
}

Assembly.Load("A2UI.Core");
if (args[1] == "A2UI.Agent")
{
    Assembly.Load("A2UI.Agent");
}

Console.WriteLine($"{args[1]} loaded on .NET {Environment.Version}.");
