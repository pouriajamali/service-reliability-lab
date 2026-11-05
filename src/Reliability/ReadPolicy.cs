using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ServiceReliability;

public enum CircuitState
{
    Closed, Open, HalfOpen
}
public sealed record PolicyOptions(int MaxAttempts = 3, int FailureThreshold = 2,
    TimeSpan? AttemptTimeout = null, TimeSpan? RetryDelay = null, TimeSpan? OpenDuration = null)
{
    public TimeSpan Timeout => AttemptTimeout ?? TimeSpan.FromMilliseconds(200);
    public TimeSpan Delay => RetryDelay ?? TimeSpan.FromMilliseconds(25);
    public TimeSpan Break => OpenDuration ?? TimeSpan.FromSeconds(30);
    internal void Validate()
    {
        if (MaxAttempts is < 1 or > 10 || FailureThreshold < 1 || Timeout <= TimeSpan.Zero ||
            Delay < TimeSpan.Zero || Break <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(1) ||
            Delay > TimeSpan.FromMinutes(1) || Break > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(PolicyOptions));
    }
}
public sealed record DiagnosticEvent(string CorrelationId, string Event, int Attempt, string Circuit, string Detail);
public sealed record ReadResult(bool Success, string Outcome, string? Value, int Attempts);
public interface IReadDependency
{
    Task<string> ReadAsync(CancellationToken cancellationToken);
}
public sealed class CircuitRejectedException : Exception
{
    public CircuitRejectedException() : base("Dependency circuit is not admitting reads.") { }
}

/// <summary>A read-only retry policy. It never retries a financial write.</summary>
public sealed class ReadPolicy
{
    public const string TelemetryName = "PouriaJamali.ServiceReliability";
    public static readonly ActivitySource Activities = new(TelemetryName);
    private static readonly Meter Meter = new(TelemetryName, "0.1.0");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("dependency.read.attempts");
    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>("dependency.circuit.rejections");
    private readonly object sync = new();
    private readonly PolicyOptions options;
    private readonly Func<TimeSpan> now;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<DiagnosticEvent> emit;
    private CircuitState state;
    private int failures;
    private long epoch;
    private TimeSpan openUntil;
    private readonly record struct Lease(long Epoch, bool Probe);

    public ReadPolicy(PolicyOptions? options = null, Func<TimeSpan>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<DiagnosticEvent>? diagnostics = null)
    {
        this.options = options ?? new();
        this.options.Validate();
        this.now = now ?? (() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency));
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        emit = diagnostics ?? (_ => { });
    }
    public CircuitState State
    {
        get
        {
            lock (sync)
                return state;
        }
    }
    private Lease Enter()
    {
        lock (sync)
        {
            if (state == CircuitState.Open && now() >= openUntil)
            {
                state = CircuitState.HalfOpen;
                epoch++;
                return new(epoch, true);
            }
            if (state != CircuitState.Closed)
                throw new CircuitRejectedException();
            return new(epoch, false);
        }
    }
    private void Complete(Lease lease, bool success)
    {
        lock (sync)
        {
            if (lease.Epoch != epoch)
                return; // A stale in-flight completion cannot heal a newer open circuit.
            if (lease.Probe)
            {
                state = success ? CircuitState.Closed : CircuitState.Open;
                failures = success ? 0 : options.FailureThreshold;
                openUntil = now() + options.Break;
                epoch++;
                return;
            }
            if (success)
                failures = 0;
            else if (++failures >= options.FailureThreshold)
            {
                state = CircuitState.Open;
                openUntil = now() + options.Break;
                epoch++;
            }
        }
    }
    private void Abandon(Lease lease)
    {
        lock (sync)
        {
            if (lease.Probe && lease.Epoch == epoch)
            {
                state = CircuitState.Open;
                openUntil = now();
                epoch++;
            }
        }
    }
    private void Log(string id, string name, int attempt, string detail) => emit(new(id, name, attempt, State.ToString(), detail));

    public async Task<ReadResult> ReadAsync(IReadDependency dependency, string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
            throw new ArgumentException("Supply a short correlation ID.", nameof(correlationId));
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = Activities.StartActivity("reference-data.read");
        activity?.SetTag("correlation.id", correlationId);
        Lease lease;
        try
        {
            lease = Enter();
        }
        catch (CircuitRejectedException) { Rejections.Add(1); Log(correlationId, "rejected", 0, "Circuit does not admit another request."); return new(false, "circuit-open", null, 0); }
        int maxAttempts = lease.Probe ? 1 : options.MaxAttempts;
        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Attempts.Add(1);
                Log(correlationId, "attempt", attempt, lease.Probe ? "Single recovery probe." : "Read dependency.");
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                string reason;
                try
                {
                    var result = await dependency.ReadAsync(attemptCts.Token).WaitAsync(options.Timeout, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Complete(lease, true);
                    Log(correlationId, "success", attempt, "Read completed.");
                    return new(true, "success", result, attempt);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (TimeoutException) { attemptCts.Cancel(); reason = "timeout"; }
                catch (HttpRequestException) { reason = "dependency-unavailable"; }
                // Programming, authentication and validation exceptions are deliberately not retried.
                // A synchronously faulted dependency can win WaitAsync's race with caller cancellation.
                // Do not turn that caller cancellation into a retry or a circuit failure.
                cancellationToken.ThrowIfCancellationRequested();
                Log(correlationId, reason, attempt, "Dependency read did not complete.");
                if (attempt == maxAttempts)
                {
                    Complete(lease, false);
                    Log(correlationId, "exhausted", attempt, "Attempt budget exhausted.");
                    activity?.SetStatus(ActivityStatusCode.Error, reason);
                    return new(false, reason, null, attempt);
                }
                Log(correlationId, "retry-scheduled", attempt, $"Delay {options.Delay.TotalMilliseconds:0} ms; bounded attempt budget.");
                await delay(options.Delay, cancellationToken).ConfigureAwait(false);
            }
            throw new UnreachableException();
        }
        catch
        {
            Abandon(lease);
            Log(correlationId, "abandoned", 0, "No retry after caller cancellation or unclassified error.");
            throw;
        }
    }
}

/// <summary>Optional HttpClient adapter: only safe GET reads are exposed.</summary>
public sealed class HttpGetDependency(HttpClient client, Uri uri) : IReadDependency
{
    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 408 or 429 or >= 500)
                throw new HttpRequestException("Transient dependency status.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Non-transient dependency status.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > 65536)
                    throw new InvalidDataException("Reference payload exceeds 64 KiB.");
                bytes.Write(buffer, 0, read);
            }
            return new System.Text.UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && error.InnerException is TimeoutException)
        {
            // Modern HttpClient wraps its own timeout in TaskCanceledException/TimeoutException.
            // Caller or application-wide cancellation must keep its cancellation semantics.
            throw new TimeoutException("HTTP reference-data read timed out.", error);
        }
    }
}
