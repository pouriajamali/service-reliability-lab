using System.Diagnostics;
using System.Diagnostics.Metrics;
using ServiceReliability;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
async Task Throws<T>(Func<Task> action, string name) where T : Exception { try { await action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL " + name); }
var time = TimeSpan.Zero;
ReadPolicy Make(int attempts = 3, int threshold = 2, int timeoutMs = 100) => new(new(attempts, threshold, TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.Zero, TimeSpan.FromSeconds(30)), () => time, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
var ok = new Scripted(_ => Task.FromResult("ok"));
var fail = new Scripted(_ => Task.FromException<string>(new HttpRequestException("down")));
var policy = Make();
var r = await policy.ReadAsync(ok, "a");
Check(r.Success && r.Attempts == 1 && ok.Calls == 1, "successful GET has one attempt");
var retry = new Scripted(_ => Task.FromResult("ok"));
int tries = 0;
retry.Action = _ => ++tries < 3 ? Task.FromException<string>(new HttpRequestException()) : Task.FromResult("ok");
r = await Make().ReadAsync(retry, "a");
Check(r.Success && r.Attempts == 3, "transient read succeeds inside bounded budget");
policy = Make();
r = await policy.ReadAsync(fail, "a");
Check(!r.Success && r.Attempts == 3 && policy.State == CircuitState.Closed, "first exhausted request counts one circuit failure");
await policy.ReadAsync(fail, "b");
Check(policy.State == CircuitState.Open, "second exhausted request opens circuit");
int calls = fail.Calls;
r = await policy.ReadAsync(fail, "c");
Check(r.Outcome == "circuit-open" && r.Attempts == 0 && fail.Calls == calls, "open circuit does not call dependency");
time += TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1);
r = await policy.ReadAsync(ok, "d");
Check(!r.Success, "one tick before reset remains blocked");
time += TimeSpan.FromTicks(1);
r = await policy.ReadAsync(ok, "e");
Check(r.Success && policy.State == CircuitState.Closed, "at boundary a successful probe closes circuit");
policy = Make(3, 1);
await policy.ReadAsync(fail, "a");
time += TimeSpan.FromSeconds(30);
calls = fail.Calls;
await policy.ReadAsync(fail, "b");
Check(fail.Calls == calls + 1 && policy.State == CircuitState.Open, "failed half-open probe gets one attempt, then reopens");
policy = Make(1, 1);
await policy.ReadAsync(fail, "a");
time += TimeSpan.FromSeconds(30);
var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var delayed = new Scripted(_ => gate.Task);
var probe = policy.ReadAsync(delayed, "probe");
r = await policy.ReadAsync(ok, "other");
Check(!r.Success && r.Attempts == 0 && delayed.Calls == 1, "only one concurrent half-open probe is admitted");
gate.SetResult("ok");
await probe;
Check(policy.State == CircuitState.Closed, "probe completion restores admission");
policy = Make(1, 1);
var staleGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var stale = policy.ReadAsync(new Scripted(_ => staleGate.Task), "old");
await policy.ReadAsync(fail, "new");
staleGate.SetResult("ok");
await stale;
Check(policy.State == CircuitState.Open, "stale closed-state success cannot heal a newer open epoch");
var cancelled = new CancellationTokenSource();
cancelled.Cancel();
calls = ok.Calls;
await Throws<OperationCanceledException>(() => Make().ReadAsync(ok, "cancelled", cancelled.Token), "pre-cancelled caller exits without attempts");
Check(ok.Calls == calls, "pre-cancellation does not call dependency");
foreach (var kind in new[] { "transport", "timeout" })
{
    using var cts = new CancellationTokenSource();
    var racePolicy = Make(1, 1);
    var race = new Scripted(_ =>
    {
        cts.Cancel();
        Exception error = kind == "transport" ? new HttpRequestException("synthetic failure") : new TimeoutException("synthetic timeout");
        return Task.FromException<string>(error);
    });
    await Throws<OperationCanceledException>(() => racePolicy.ReadAsync(race, "cancel-fault-race", cts.Token), $"caller cancellation wins completed {kind} fault");
    Check(racePolicy.State == CircuitState.Closed && race.Calls == 1, $"cancelled {kind} fault does not count as a circuit failure or retry");

    var probePolicy = Make(1, 1);
    await probePolicy.ReadAsync(fail, "open");
    time += TimeSpan.FromSeconds(30);
    using var probeCts = new CancellationTokenSource();
    var probeRace = new Scripted(_ =>
    {
        probeCts.Cancel();
        Exception error = kind == "transport" ? new HttpRequestException("synthetic failure") : new TimeoutException("synthetic timeout");
        return Task.FromException<string>(error);
    });
    await Throws<OperationCanceledException>(() => probePolicy.ReadAsync(probeRace, "probe-race", probeCts.Token), $"caller cancellation wins completed {kind} half-open fault");
    Check(probePolicy.State == CircuitState.Open && probeRace.Calls == 1, $"cancelled {kind} probe releases its admission");
    var nextProbe = await probePolicy.ReadAsync(ok, "next-probe");
    Check(nextProbe.Success && probePolicy.State == CircuitState.Closed, $"cancelled {kind} probe permits an immediate replacement without a new cooldown");
}
using (var cts = new CancellationTokenSource()) { var running = new Scripted(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith<string>(_ => throw new OperationCanceledException(ct), ct)); var task = Make().ReadAsync(running, "cancel", cts.Token); cts.Cancel(); await Throws<OperationCanceledException>(() => task, "in-flight caller cancellation propagates"); Check(running.Calls == 1, "caller cancellation is not retried"); }
var timeoutToken = false;
var hanging = new Scripted(ct => { ct.Register(() => timeoutToken = true); return new TaskCompletionSource<string>().Task; });
r = await Make(2, 2, 15).ReadAsync(hanging, "timeout");
Check(!r.Success && r.Outcome == "timeout" && r.Attempts == 2 && hanging.Calls == 2, "non-cooperative dependency is bounded by caller wait timeout");
Check(timeoutToken, "timed-out attempt token is cancelled");
var invalid = new Scripted(_ => Task.FromException<string>(new InvalidOperationException("validation")));
await Throws<InvalidOperationException>(() => Make().ReadAsync(invalid, "bad"), "unclassified error is propagated");
Check(invalid.Calls == 1, "unclassified error is not retried");
policy = Make(1, 1);
await policy.ReadAsync(fail, "a");
time += TimeSpan.FromSeconds(30);
using (var cts = new CancellationTokenSource()) { var dep = new Scripted(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith<string>(_ => throw new OperationCanceledException(ct), ct)); var pending = policy.ReadAsync(dep, "probe", cts.Token); cts.Cancel(); await Throws<OperationCanceledException>(() => pending, "cancelled probe is released"); r = await policy.ReadAsync(ok, "next"); Check(r.Success, "cancelled probe does not wedge half-open gate"); }
var traceSeen = false;
using (var listener = new ActivityListener { ShouldListenTo = s => s.Name == ReadPolicy.TelemetryName, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, ActivityStopped = a => traceSeen = a.GetTagItem("correlation.id")?.ToString() == "trace-test" }) { ActivitySource.AddActivityListener(listener); await Make().ReadAsync(ok, "trace-test"); }
Check(traceSeen, "ActivitySource emits correlation tag");
long count = 0;
using (var listener = new MeterListener()) { listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == ReadPolicy.TelemetryName) l.EnableMeasurementEvents(i); }; listener.SetMeasurementEventCallback<long>((i, m, _, _) => { if (i.Name == "dependency.read.attempts") count += m; }); listener.Start(); await Make().ReadAsync(ok, "metrics"); }
Check(count == 1, "Meter emits actual attempt count");
var handler = new StubHandler();
using (var client = new HttpClient(handler))
{
    var http = new HttpGetDependency(client, new Uri("https://synthetic.invalid/reference"));
    handler.Reply = n => new(n == 1 ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK) { Content = new StringContent("reference") };
    r = await Make().ReadAsync(http, "http-retry");
    Check(r.Success && r.Attempts == 2 && handler.Calls == 2, "HTTP 503 is transient and safe GET retries");
    handler.Calls = 0;
    handler.Reply = _ => new(System.Net.HttpStatusCode.Unauthorized);
    await Throws<InvalidOperationException>(() => Make().ReadAsync(http, "auth"), "HTTP 401 does not enter retry budget");
    Check(handler.Calls == 1, "authentication failure makes one request");
    handler.Reply = _ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[65537]) };
    await Throws<InvalidDataException>(() => Make().ReadAsync(http, "size"), "reference payload over 64 KiB is rejected");
    handler.Reply = _ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xff }) };
    await Throws<System.Text.DecoderFallbackException>(() => Make().ReadAsync(http, "utf8"), "invalid UTF-8 is not silently replaced or retried");
}
var timeoutHandler = new AsyncTimeoutHandler();
using (var client = new HttpClient(timeoutHandler) { Timeout = TimeSpan.FromMilliseconds(15) })
{
    var http = new HttpGetDependency(client, new Uri("https://synthetic.invalid/reference"));
    r = await Make(2, 2, 200).ReadAsync(http, "http-client-timeout");
    Check(!r.Success && r.Outcome == "timeout" && r.Attempts == 2, "HttpClient own timeout enters the bounded timeout policy");
    Check(timeoutHandler.Calls == 2, "HttpClient timeout is retried only within attempt budget");
}
var httpCancellationHandler = new AsyncTimeoutHandler();
using (var client = new HttpClient(httpCancellationHandler) { Timeout = TimeSpan.FromSeconds(10) })
using (var cts = new CancellationTokenSource())
{
    var http = new HttpGetDependency(client, new Uri("https://synthetic.invalid/reference"));
    var pending = Make(3, 2, 200).ReadAsync(http, "http-caller-cancel", cts.Token);
    cts.Cancel();
    await Throws<OperationCanceledException>(() => pending, "HTTP caller cancellation remains cancellation rather than timeout");
    Check(httpCancellationHandler.Calls == 1, "HTTP caller cancellation is not retried");
}
foreach (var options in new[] { new PolicyOptions(0), new PolicyOptions(11), new PolicyOptions(FailureThreshold: 0), new PolicyOptions(AttemptTimeout: TimeSpan.Zero), new PolicyOptions(RetryDelay: TimeSpan.FromSeconds(-1)), new PolicyOptions(OpenDuration: TimeSpan.Zero) }) { try { _ = new ReadPolicy(options); throw new Exception("invalid options accepted"); } catch (ArgumentOutOfRangeException) { passed++; } }
Console.WriteLine($"PASS {passed} independent checks");
sealed class Scripted(Func<CancellationToken, Task<string>> action) : IReadDependency
{
    public Func<CancellationToken, Task<string>> Action = action; public int Calls; public Task<string> ReadAsync(CancellationToken ct)
    {
        Calls++;
        return Action(ct);
    }
}
sealed class StubHandler : HttpMessageHandler
{
    public Func<int, HttpResponseMessage> Reply = _ => new(System.Net.HttpStatusCode.OK);
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
            throw new Exception("Only GET is permitted in this sample adapter.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Reply(++Calls));
    }
}
sealed class AsyncTimeoutHandler : HttpMessageHandler
{
    public int Calls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The synthetic dependency should only complete by cancellation.");
    }
}
