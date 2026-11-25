if (args.Length is < 2 or > 3 || !Version.TryParse(args[0], out var expectedRuntime))
{
    throw new ArgumentException("Pass the exact runtime version, package ID, and optional API snapshot path.");
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

if (args.Length == 3)
{
    var sdk = System.Reflection.Assembly.Load(args[1]);
    PublicApi.Write(args[2], sdk);
}
