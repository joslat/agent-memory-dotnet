# Certified memory packs

A memory pack is a signed, content-addressed archive of agent memory that any host can verify offline. A pack can also
say what it is, and carry what an independent evaluator measured about it:

- **The pack card** (`card.json`, pack format v1.2 §15) says who answers for the pack, what it is for and what it is not
  for, the boundaries a host must keep, the risks its publisher knows, and where its content comes from. It is a listed,
  signed file like any other, read strictly by every verifier, and shown by `ampack verify` and `ampack inspect`.
- **An evaluation certificate** (§16) is an evaluator's signed summary of a trial it ran on its own tasks: the trial the
  pack declares, every check, both arms (with the pack and without it), whether every target was met, and the AEF run
  (AgentEval's evidence format) it summarises, by run id and run hash. It travels beside the pack in
  `META/attestations/`, outside the files list, so it can be added after release without changing the pack's digest. It
  is bound to that digest: moved onto other content, it is not trusted.
- **Who may certify** is the host's choice: a key its trust policy names as `evaluator` for the pack's id. An evaluator key
  holds that role alone, so the key that signs a pack never certifies it as well. A certificate from any other key is shown
  for what it claims and admits nothing.
- **Proven here is the default.** A host that declares trials (`ModuleSetBuilder.RequireTrials`) switches a pack on only
  after its own trial passed. Where it chooses, namespace by namespace, it may rely on a trusted certificate instead
  (`ModuleSetBuilder.TrustCertificatesFor`): it records a decision from the certificate, signed with its own key, naming
  the certificate it relied on. An upgrade that must not regress still needs the host's own trial, because only the host
  can compare against the version that passed on it.

## Run it

Needs Docker and the .NET 10 SDK; PowerShell 7 (`pwsh`) on Windows, macOS or Linux.

```
pwsh samples/CertifiedPacks/run.ps1
```

The script, in a temporary folder that it deletes afterwards:

1. Makes four keys: the **publisher**'s, an independent **evaluator**'s (role `evaluator`), the evaluator's gate host key,
   and the consuming **host**'s key. The host's trust policy names who may publish `io.agentmemory` packs and who may
   certify them.
2. **Publisher:** ships the code map definition as a pack (`ampack migrate`, `ampack build`) with its card.
3. **Evaluator:** runs the pack's declared trial, test impact on this repository's recent commits, against a throwaway
   Neo4j container (`agentmemory evaluate --gate ... --pack codemap.ampack`), which signs the decision with the
   evaluator's gate key. The container's password is random and reaches the CLI through its environment only.
4. **Evaluator:** turns the decision into an AEF run (`ampack evidence`) and signs a certificate from it that travels
   beside the pack with the run (`ampack certify --aef run`). The pack's digest does not change.
5. **Host:** verifies the certificate (`ampack certificate verify`), records a decision from it with its own key
   (`ampack admit`), and shows both admission modes from its signed decisions alone (`ampack gate`): off by default, because
   it was admitted on a certificate and the host has not run its own trial, and on with
   `--trust-certificates-for io.agentmemory`.
6. A host whose policy does not name the evaluator sees the certificate as untrusted, and `ampack admit` refuses.

With `-AgentEval <command>` the evaluator also seals the AEF run with AgentEval (`agenteval aef seal run --sealed-by
ingest`) before certifying; the seal travels beside the run and is outside its run hash. `-KeepWork` keeps the work
folder (it holds private keys: delete it when done).

## In a host

```csharp
var trust = PackTrust.Load("trust.json");                       // names publisher and evaluator keys
var pack = PackLoader.Load("codemap.certified.ampack", trust);  // pack.Card, pack.Certificates (trusted or not)

var (decision, reason) = PackCertificates.Admit(pack, DateTimeOffset.UtcNow);
if (decision is not null) PackActivationFolder.Write("activations", decision, hostKey);

services.AddAgentMemoryModules(m => m
    .AddPack(pack)
    .RequireTrials(new PackActivationFolder("activations", hostPublicKeyPem))
    .TrustCertificatesFor("io.agentmemory"));                    // elsewhere, only this host's own trial counts
```

`PackGateCheck.Of(packs, activations, requireTrialsFor, trustCertificatesFor)` shows what the gate would do without
composing a host, as `ampack gate` does.
