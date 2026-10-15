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

## Native service consumer

`Commitments.NativeChecks` references the actual example, both SDK adapters and Microsoft Agent
Framework through NuGet. `eng/verify-commitments-native.py` packs the complete eight-package chain
into a fresh mapped feed, rejects ProjectReference assets and checks every loaded AgentMemory DLL
against its nupkg. Use a new output directory:

```sh
python eng/verify-commitments-native.py --output artifacts/native-build-001 --build-only
python eng/verify-commitments-native.py --output artifacts/native-world-001 \
  --world /path/to/frozen-world.json --world-sha256 <registered-sha256>
```

Service execution requires `AGENTMEMORY_DISPOSABLE_NEO4J=1`, a loopback `NEO4J_URI`, and the disposable
service's `NEO4J_PASSWORD`. The initial database must be empty; the runner never resets existing data.
The host bootstraps core schema and explicitly applies module migrations. It seeds only core entity
anchors; teaching goes through actual `ChatClientAgent.RunAsync`, native persisted-source ingestion,
the packaged provider and real Neo4j. Each retrieval gets a new native session. Recent and relevant
core conversation recall are both zero in enabled and control hosts.

The deterministic chat client only acknowledges; it records the exact instructions/messages a model
would receive. Assertions cover frozen record IDs, version/provenance/owner stamps, recorded replay,
view results, system/valid-time selection, rendered content and an off control with retained data.
Entity/due contributors use the package factories; the bounded history case uses the same declared
package view through `ViewContributor` with its explicit runtime limit. No model, semantic-quality or
coding benchmark claim follows from these checks.

The default runs the primary world. Named negative cases use `--case <id>` on the compiled consumer;
each invocation refuses a nonempty database and reconstructs its fork using actual native teaching.
The source/anchor/owner/role cases preserve recorded refusals and retained provenance. The source-clear
case uses the public ClearSessionAsync API. The missing-anchor case tests history without a target
filter, so a filter cannot accidentally hide a broken required-anchor check. The paging case verifies
that a lost earlier candidate does not consume the declared limit.

`--negative-cases` on the Python verifier is hosted-only (`GITHUB_ACTIONS=true`). It requires
`NEO4J_SERVICE_ID` to identify the primary job's actual service container, then runs every frozen
negative case sequentially in a separate disposable container using that exact image ID. It reuses
the verified package build, binds each service only to loopback, records logs/identity/results, and
removes only the container carrying that iteration's random ownership label. It never resets a database
or touches local Docker. An infrastructure failure stops the sequence with incomplete coverage.

The module-off case compares warmed core query shapes/counts and actual inputs before and after
module teaching. Warm-up is separately recorded; every measured read still uses a fresh session.
Query observation delegates to the actual transaction runner, including its atomic-write capability.
Provider calls are observed while invoking the real package provider. Expected mutation-control
failures remain visible in the receipt instead of being counted as successful retrievals.

Reports explicitly distinguish primary and named negative passes from full acceptance. All cases must
have terminal receipts; a successful compile or a primary pass does not prove the negative cases.
Explicit retained-data v1/v2 migration and signed runtime lifecycle remain separate. Failures are never
averaged into a passing score. Supplied worlds and raw traces belong in private evidence, not public CI
artifacts. `--build-only` performs no service operations and cannot claim a world pass.

## Native telemetry receipts

The native consumer records the existing AgentMemory diagnostics source while executing the frozen
world. Fixture operation scopes distinguish module turns, core-only turns and recorded replay without
adding a library source/exporter. `report.json` includes the exact activities, parent ids, tags, events,
module metadata fingerprint and package version. The host uses explicit resolution-bound compilation.

`eng/module_native_telemetry.py` independently inspects these records for all four module span families,
actual database nesting, owning versions, set-level migration attribution, no-module core turns, replay,
upgrade no-op behavior and private input exclusion. It writes a separate `telemetry-checks.json` and
fails the service stage if any check fails. Original world assertions remain unchanged. The mutation
suite runs without services using `python eng/test_module_native_telemetry.py` and is included in CI.
The build-only command proves package consumption, not native trace acceptance. Hosted execution must
pass with exact source/package/world identities before claiming those results. No inference is involved.
