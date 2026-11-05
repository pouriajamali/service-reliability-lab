using System.Text.Json;
using ServiceReliability;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("demo/traces.json");
var scenarios = new List<object>();
foreach (var scenario in new[] { "healthy", "transient", "exhausted", "recovery" })
{
    var events = new List<DiagnosticEvent>();
    var current = TimeSpan.Zero;
    var policy = new ReadPolicy(new(3, 2), () => current, (d, ct) => { ct.ThrowIfCancellationRequested(); current += d; return Task.CompletedTask; }, events.Add);
    var results = new List<object>();
    int call = 0;
    var dependency = new Scripted(() =>
    {
        call++;
        if (scenario == "healthy" || scenario == "transient" && call >= 2 || scenario == "recovery" && call >= 7)
            return Task.FromResult("synthetic-reference-data");
        return Task.FromException<string>(new HttpRequestException("Synthetic unavailable dependency."));
    });
    results.Add(await policy.ReadAsync(dependency, "read-001"));
    if (scenario is "exhausted" or "recovery")
    {
        results.Add(await policy.ReadAsync(dependency, "read-002"));
        results.Add(await policy.ReadAsync(dependency, "read-003"));
        if (scenario == "recovery")
        {
            current += TimeSpan.FromSeconds(30);
            results.Add(await policy.ReadAsync(dependency, "read-004"));
        }
    }
    scenarios.Add(new
    {
        id = scenario,
        events,
        results,
        dependencyCalls = call,
        finalCircuit = policy.State.ToString()
    });
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
File.WriteAllText(output, JsonSerializer.Serialize(new { schema = "service-reliability-replay-v1", source = "C# .NET deterministic scenario runner", scenarios }, options).Replace("\r\n", "\n") + "\n");
Console.WriteLine($"Generated four .NET scenario traces: {output}");
sealed class Scripted(Func<Task<string>> action) : IReadDependency
{
    public Task<string> ReadAsync(CancellationToken ct) => action();
}
