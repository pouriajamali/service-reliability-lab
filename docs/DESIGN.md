# Read failures, attempt budgets and recovery

The example sits on the read side of a financial backend: a service needs a small
reference-data document before continuing. Repeating that GET can be safe;
repeating a financial command without an idempotency contract is a different
problem. That is why this API accepts `IReadDependency`, rather than an arbitrary
operation to retry.

## The request boundary

The circuit counts failed logical reads. With three allowed attempts and a
threshold of two failed reads, an outage can cause six dependency calls before
the circuit opens. Counting each attempt as a separate circuit failure would make
the configured threshold mean something different.

Only `HttpRequestException` and `TimeoutException` consume the attempt budget.
The HTTP adapter classifies 408, 429 and 5xx as transient and rejects other
non-success responses without retrying them. It deliberately does not interpret
`Retry-After`, infer business correctness from HTTP 200, or retry a write.

Each attempt is awaited with a timeout. When that wait expires, its linked token
is cancelled before another attempt begins. This bounds this caller's wait; it
cannot guarantee that a non-cooperative dependency has stopped. The fake
dependency test makes that distinction observable.

`WaitAsync` begins after the dependency has returned its task; it cannot bound
synchronous blocking inside `ReadAsync` before that return. The HTTP adapter
uses asynchronous I/O. It translates HttpClient's own non-caller timeout wrapper
into the policy's timeout classification, while preserving caller cancellation.

## The circuit boundary

The gate is protected by a lock, while dependency calls and delays run outside
the lock. Closed calls may run concurrently. An open gate does not admit new
calls. At the monotonic reset deadline, one caller takes the half-open probe;
others remain rejected until it completes. The single probe receives one attempt.

Every admitted call carries an epoch. Opening or changing the recovery epoch
invalidates older completions. A successful call admitted before the circuit
opened cannot incorrectly close the newer open circuit. An externally cancelled
probe releases its reservation without counting a dependency failure.

A dependency can cancel its caller and return an already-faulted task in the same
call. In that race, `WaitAsync` may observe the dependency fault first. The policy
checks caller cancellation again after classifying transient faults, before
logging a dependency failure, retrying or changing circuit failure state. The
regression cases exercise both transport and timeout faults in closed and
half-open states.

The default clock is derived from `Stopwatch`, so wall-clock adjustment does not
move the recovery boundary. Tests inject a manual monotonic clock and check the
instant immediately before, and exactly at, the deadline. Per-attempt timeout
tests use a real short wait; generated replay scenarios use completed synthetic
responses and a manual delay clock instead of pretending to benchmark timing.

## Diagnostics

Events are serialized as records, rather than concatenated application log lines.
The correlation ID is useful in structured logs and attached to a BCL activity.
Attempt and circuit-rejection counters have no request IDs as metric labels,
avoiding unbounded metric cardinality. A hosting application can subscribe to
these hooks or connect its normal telemetry exporter. The diagnostics callback
is application code and must not throw.

## What I would add for a real service

Use an established resilience library with a documented dependency-specific
policy; include caller deadlines, jitter, retry quotas, rate limits and service
objectives. Honour response-specific retry guidance where appropriate. Separate
readiness from liveness and validate the chosen settings against an observed
workload. Those are deployment decisions, not results claimed by this lab.
