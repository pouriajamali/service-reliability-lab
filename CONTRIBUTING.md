# Contributing

Start with the README and the current release. A useful contribution is a small
case another developer can reproduce, with a clear expected result.

## Report a problem

Include the commit or release, OS and toolchain versions. Provide a deterministic dependency-response sequence, cancellation/timeout timing or barriers, expected circuit state and actual trace.
Use synthetic inputs only. Remove credentials, account identifiers, personal
data and employer or proprietary source before opening an issue.

## Run the checks

Use .NET 10 SDK and Python 3.10+. From the repository root:

```sh
dotnet run --project tests/Verify -c Release
dotnet run --project src/Demo -c Release -- artifacts/traces.json
python scripts/compare_traces.py
python scripts/build_demo.py --check
```

## Submit a focused change

Explain the behavior before and after the change. For a bug, add a regression
case that fails before the repair. Preserve the documented input and output
contracts, keep historical evidence intact, and update the relevant design or
format notes when the contract changes. Do not replace recorded measurements
with estimates or transfer their results to a different implementation.

Small improvements to setup instructions, fixtures and diagnostic messages are
welcome. Read the license and attribution in this repository before contributing.
