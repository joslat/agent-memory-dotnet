# Licensing of this repository

This repository holds work under three licences and one proposed patent pledge (a draft until issued). This file is the complete map. The root [LICENSE](LICENSE) file is the MIT licence: it is the default that applies to every file that is not inside a project with its own `LICENSE` file. The projects listed below carry their own licence files, which take precedence for their folders, and each NuGet package states its licence in its metadata.

## The map (from version 2.0.0)

| Folder | Licence | Patent position | Licence files |
|---|---|---|---|
| `src/AgentMemory.Extensibility` (the module SDK, the declared traversal and the trial gate; package 0.1.0 and earlier were MIT, Apache-2.0 from 0.1.1; the SDK line stays 0.1 because every pack declares the range `[0.1,0.2)`) | Apache-2.0 | Apache-2.0 section 3 gives users of the code a patent licence | `LICENSE`, `NOTICE` in the folder |
| `src/AgentMemory.Extensibility.Ampack` (ampack packs in the host) | Apache-2.0 | as above | in the folder |
| `src/AgentMemory.Extensibility.Neo4j` (the store side of the module SDK and the walk) | Apache-2.0 | as above | in the folder |
| `src/AgentMemory.Extensibility.AgentFramework` (the module SDK in the Agent Framework host) | Apache-2.0 | as above | in the folder |
| `src/AgentMemory.Packs.Format` (the ampack format: identity, archive profile, signatures) | Apache-2.0 | as above | in the folder |
| `src/AgentMemory.Packs.CodeMap`, `src/AgentMemory.Packs.CodeMap.Roslyn`, `src/AgentMemory.Packs.Coding`, `src/AgentMemory.Packs.Decisions`, `src/AgentMemory.Packs.Repository`, `src/AgentMemory.Packs.Solid`, `src/AgentMemory.Packs.Work`, `src/AgentMemory.Packs.Intent`, `src/AgentMemory.Packs.Architecture` (the pack libraries) | Apache-2.0 | as above | in each folder |
| `tools/Ampack` (the `ampack` command-line tool) | Apache-2.0 | as above | in the folder |
| `reader-rs` (the ampack reader in Rust and WebAssembly) | Apache-2.0 | as above | in the folder; `Cargo.toml` |
| `conformance/pack-v1.1` (the conformance kit and the Python verifier) | Apache-2.0 | as above | in the folder |
| `shared/` (source files compiled into several packages: the format's text, number and namespace rules, the record frame, the instruction-like detector and others) | MIT (no SPDX header; rule 2 below), including where an Apache-2.0 package compiles them in, which the MIT licence permits | Not covered by the Apache-2.0 patent grant | the root `LICENSE` |
| `spec/` (the specification texts and its data files) | CC BY 4.0 | CC BY 4.0 licenses no patent rights; the proposed pledge below would apply once issued | [`spec/LICENSE.md`](spec/LICENSE.md) |
| Everything else: `AgentMemory.Core`, `AgentMemory.Abstractions`, `AgentMemory.Neo4j`, the Agent Framework, Semantic Kernel and MCP adapters, the samples, the benchmarks, the remaining tools, the tests, the sample knowledge pack under `packs/` | MIT | Not covered by the proposed pledge; MIT has no express patent-grant clause, and whether any claim reads on a component is a question of the claims, not of the copyright licence | root [`LICENSE`](LICENSE) |

The proposed patent non-assertion pledge is [PATENTS.md](PATENTS.md): the canonical draft of a royalty-free promise for conforming implementations of the listed specification versions that do not use the Apache-2.0 code, with the conditions stated there. It is a draft until the author issues it, after a check by patent counsel; until then it grants nothing. The Apache-2.0 patent licence for users of the code is in force regardless. "2.0.0" is the repository release; each package has its own version.

## Why two code licences

The Apache-2.0 projects implement the ampack format and its host procedure, on which the author filed a Swiss patent application on 3 November 2026 (pending; whether and how its claims are granted is not decided). Apache-2.0 section 3 gives every user of that code an express patent licence; MIT has no express patent-grant clause. The core library stays MIT as it has been since its first release, so nothing changes for existing users of the core. The same code is never offered under both licences: that would let a user pick MIT and lose the patent terms.

## Packages that bundle

A NuGet library package carries only its own project's code and names its dependencies, each under its own licence.
A .NET tool package is different: it bundles every assembly it runs. The `AgentMemory.McpHost` tool package
(`agent-memory-mcp`, from `tools/AgentMemory.McpHost`, whose own code is MIT) bundles the Apache-2.0 module SDK and
packs, so the package is licensed `MIT AND Apache-2.0` and carries both licence texts and the Apache NOTICE under
`licenses/`. Each file inside is still under exactly one licence, as the table says.

## How to tell which licence a file is under

1. A source file whose first line is `SPDX-License-Identifier: Apache-2.0` is Apache-2.0.
2. Otherwise, the nearest `LICENSE` file up the folder tree applies (the Apache-2.0 projects carry one; `spec/` carries `LICENSE.md`).
3. Otherwise, the root `LICENSE` (MIT) applies.

## What each licence asks of you

- **MIT:** keep the copyright notice and the licence text with copies of the code.
- **Apache-2.0:** keep the `LICENSE` and the `NOTICE` with copies, keep the header lines, mark files you changed, and read section 4 for redistribution and section 5 for contributions. Section 3 is the patent licence for the code you use; it has its own termination rule.
- **CC BY 4.0:** credit the author, link the licence, and say so if you changed the text.
- **The proposed pledge:** if you implement the specification without this code, read `PATENTS.md`; once issued, the promise would hold for conforming implementations of the listed versions under its conditions, and it does not touch what Apache-2.0 already grants.

## How this is kept consistent

- Every Apache-2.0 project has an `.editorconfig` header rule (IDE0073) with build-time enforcement, so a missing header fails the build of that project.
- `tests/AgentMemory.Tests.Unit.Extensibility/LicensingGuardTests.cs` checks the licence files, the notices, the package licence expressions, the headers, the MIT boundary, this map, the README table and the pledge.
- Contributions are accepted under the licence of the project they change ([CONTRIBUTING.md](CONTRIBUTING.md), section 8).

## Why the root LICENSE file is still MIT

It is the licence of the largest part of the repository and the default for everything without a project-level licence. GitHub reads the root file to label the repository, and the MIT text must stay unmodified for that label to be right. The Apache-2.0 projects override it inside their folders and in their packages; this file and the README's "Licensing" section are where the whole picture is explained.

## Where the texts are

- Root `LICENSE`: the MIT licence.
- Each Apache-2.0 folder: the canonical Apache License 2.0 text and a `NOTICE`.
- `spec/LICENSE.md`: the CC BY 4.0 notice.
- `PATENTS.md`: the proposed pledge (draft).
- `CHANGELOG.md`, "Licensing" under the next release: what changed and when.

This file is a plain-language map, not legal advice. The licence texts are the operative documents; the pledge is a draft until issued.
