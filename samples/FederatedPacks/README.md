# Federated memory packs on Solid

A memory pack is a signed, content-addressed archive of agent memory that any host can verify offline. The **federated
profile** lets one organisation publish a pack for named readers on the Web, and lets their hosts find, check and install
it, using [Solid](https://solidproject.org/) Pods and WebIDs:

- **Who published it:** the pack names its publisher's WebID inside what is signed. The publisher asserts its signing key in
  its WebID profile, and the reader's host binds that key to that WebID in its own trust policy after checking the key id.
  A WebID string alone proves nothing; the two-way binding does.
- **Who it is for:** the pack names its audience inside what is signed, as a critical extension. A host that is not in the
  audience refuses the pack, and a host that does not understand the profile refuses it too, rather than ignoring it.
- **Where it was placed:** each placement in a Pod gets a separately signed publication receipt for that URL and those
  exact bytes, with an audience no wider than the pack's and the result of a probe: anonymous and strangers refused, the
  readers able to read the bytes back. A copy offered anywhere else has no receipt for that place.
- **Finding it:** from the publisher's WebID alone, through its public type index to a catalog per reader, so no reader
  learns what others receive.
- **Access is not trust:** a pack read from a Pod still goes through the host's full verification, install ledger and
  trial before it is used.

## Run it

Needs Docker and the .NET 10 SDK; PowerShell 7 (`pwsh`) on Windows, macOS or Linux.

```
pwsh samples/FederatedPacks/run.ps1
```

The script:

1. Starts a local [Community Solid Server](https://github.com/CommunitySolidServer/CommunitySolidServer) with three test
   accounts (`supplier`, `buyer`, `stranger`) from [compose.yaml](compose.yaml), enforcing access with Web Access Control,
   or with Access Control Policies when you pass `-Access acp`.
2. Registers client credentials for each account on that local server, and passes them to `ampack` only through the
   environment of each call.
3. **Supplier:** makes a signing key, asserts it in its WebID profile (`ampack solid bind-key`), writes a small knowledge
   pack for the buyer alone, builds and signs it, and publishes it (`ampack solid publish`).
4. **Buyer's host:** binds the supplier's key after checking its id (`ampack trust webid`), discovers the pack from the
   supplier's WebID (`ampack solid discover`), fetches, verifies and installs it (`ampack solid fetch`), and runs the
   pack's own test.
5. **Stranger and anyone else:** discover nothing; the stranger's fetch is refused.
6. Stops the server (`-KeepServer` leaves it running at http://localhost:3000/).

The test accounts and passwords exist only on the local server; nothing leaves your machine.

## Against a real Pod

Drop `--allow-loopback-http`: every URL must then be `https`. Register client credentials for your WebID with your Pod
provider, set `AMPACK_SOLID_CLIENT_ID` and `AMPACK_SOLID_CLIENT_SECRET`, and run the same commands.

Solid servers enforce access with one of two models, and `ampack` writes whichever the server uses: Web Access Control
(an `.acl` document per resource) or Access Control Policies (an access control resource). It tells them apart by what the
resource's `Link rel="acl"` points at, then the probe checks that the rules actually hold, and the receipt records which
model enforced them. `pwsh samples/FederatedPacks/run.ps1 -Access acp` runs the whole sample on an ACP server.
