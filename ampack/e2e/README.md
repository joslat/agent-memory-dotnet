# End to end: one pack, every client

The clients of the ampack format are four packages in four languages. Each has its own tests, and each runs the
conformance vectors. This test asks the question those cannot: **does a pack made today, by the real builder, come out
the same from every package as its users install it?**

```
python ampack/e2e/run.py --work <a folder outside the checkout>
```

## What it does

1. **Builds** the pack builder (`tools/Ampack`) and the client packages from the checkout: the Python wheel, the npm
   package, the two Rust crates, the NuGet library and its command.
2. **Installs** each package the way its users do, each in a new place of its own:

   | Client | Installed as |
   |---|---|
   | Python library and command | the wheel, with `pip`, in a new virtual environment |
   | JavaScript library | the packed `.tgz`, with `npm install`, in a new project |
   | .NET library | a package reference in a new .NET 8 console project |
   | .NET command | `dotnet tool install`, in a tool folder |
   | Rust command | `cargo install` |

   The Rust crates are also packaged and built from their packages, as crates.io serves them.
3. **Makes** new keys with the builder, and with them: two knowledge packs, a set of both, a pack signed by a key outside
   its prefix, two revocation lists, and three damaged files. The records carry text outside ASCII, quotes, a tab and a
   line break inside a value, a long value, and links.
4. **Asks** every client about every case and compares.

## What must hold

- **The verdict.** Every client reaches the expected verdict on every case: verified, or refused.
- **The same pack.** Where a pack verifies, every library has read the same pack: its id, version, kind and title, its
  digest, the SHA-256 of every file, every record with its properties and links, and for a set every member.
- **The pack that was written.** The records read are the records step 3 wrote, and the digest is the one the builder
  printed.

## The cases

| Case | Expected |
|---|---|
| A new pack | verified |
| A set of two packs | verified |
| A revocation list naming another pack | verified |
| One byte changed | refused |
| The file cut short | refused |
| Not a pack at all | refused |
| A host that does not trust the publisher | refused |
| A key signing outside its prefix | refused |
| The pack's digest revoked | refused |
| The set, one member revoked | refused |
| A revocation list past its expiry | refused (libraries only: the commands take no clock) |

## Reading the result

One line a case, one column a client: `ok`, `FAIL`, or `-` where a client is not asked. Each failure is then named with
what the client said. `results.json` in the work folder holds the same.

Exit code 0: every check held. 1: one did not. 2: the environment or a build step failed, which says nothing about the
packages.

## What it needs

Python 3.12, Node 20 or later with npm, a Rust toolchain with the target `wasm32-unknown-unknown`, the .NET SDK and the
.NET 8 runtime. `--jobs N` keeps the builds to N jobs at a time. `--skip-build` runs steps 3 and 4 again on the packages
and installs a former run left in the work folder.

On GitHub it runs in the workflow `ampack clients`, whenever something these packages are made of changes.

## What it does not show

- Nothing here reaches a package index: the packages are built and installed from files.
- The Rust crate has no library to read a pack's content yet, so Rust answers with a verdict only.
- The reasons for a refusal are each client's own words and are not compared; the conformance kit pins the rule each
  refusal comes from.
- The keys are made for the run and thrown away with the work folder. No real key is used or needed.
