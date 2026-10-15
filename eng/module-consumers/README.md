# Independent memory-package consumers

These projects reference NuGet packages only. They are deliberately outside the solution and inert
when ModuleSdkVersion is unset, so code-discovery builds cannot accidentally restore a held SDK or
pretend to execute this proof. Nothing here is published by the release manifest.

From a checkout with the .NET SDK in global.json and .NET 8/9/10 runtimes:

```sh
python eng/verify-commitments-package.py --output artifacts/commitments-check-001
```

The output path must be new and remain under artifacts. The verifier builds/packs the SDK and its
Abstractions dependency, creates a fresh local feed and isolated NuGet cache, packs Commitments v1
and v2, and runs both package consumers on all three frameworks. AgentMemory.* package source mapping
permits only that local feed. It rejects project-reference dependencies and verifies the actual loaded
module and SDK DLL bytes against their nupkgs. The report records package/source hashes, runtime
versions, commands and failures. Existing evidence is never overwritten. No database or model runs,
Docker operations or publishing occur. Failed logs remain in the selected output directory.

The checks use deterministic structured input and a clearly named ProbeReader test double for view
composition. They prove authoring/packaged API contracts, not graph persistence, native host wiring,
migration or signed runtime lifecycle. Those need the isolated real-service acceptance world.
See [Commitments](Commitments/README.md) for input and domain semantics. CI runs this same verifier.
