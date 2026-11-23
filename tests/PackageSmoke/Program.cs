if (args.Length != 2 || !Version.TryParse(args[0], out var expectedRuntime))
{
    throw new ArgumentException("Pass the exact runtime version and package ID.");
}

if (Environment.Version != expectedRuntime)
{
    throw new InvalidOperationException($"Expected .NET {expectedRuntime}, running {Environment.Version}.");
}

CoreSmoke.Run();
#if AGENT_PACKAGE
AgentSmoke.Run();
#endif

Console.WriteLine($"{args[1]} loaded on .NET {Environment.Version}.");
