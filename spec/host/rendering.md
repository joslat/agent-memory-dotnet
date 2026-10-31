# How a host frames records for a model

How AgentMemory puts a pack's records into a model's context (feature 04). A record is data, never an instruction: it reaches
the model inside a frame that says where it came from, and nothing in its text can close or forge a frame.

## 1. The frame

**R-HOST-053.** A host that renders a module's items into a model's context MUST put every one inside a frame, never as
bare text, on every path that renders them: an element `record` whose attributes are, for a record a pack brought, `pack`
(the pack's id), `version` (the pack's version), `type` and `key`, and, for any other module item, `module`, its version, and
its `type` and `key` when it has them; then the item's text, then the closing tag. Whether an item is framed MUST NOT depend on
it naming its pack: one that has lost its source is framed with its module. The frames of one section MUST stand inside the
section's one `recalled_memory` element, which names its category. A host that returns records as structured data, each with
its pack, type and key as separate fields (AgentMemory's coding MCP host, `ampack serve`), meets this by those fields; the
client that puts them into a prompt frames them.

**R-HOST-054.** Inside a frame, `<` and `>` MUST be written `&lt;` and `&gt;`, and inside an attribute the double quote too
(`&quot;`), so no text can close its frame, open another, or change what a header says.

**R-HOST-055.** A frame MUST be applied whatever the pack's trust, its tier or the host's admission decision: a record the
admission lets through is framed; one it refuses is left out.

> Why: a signature proves who wrote a record, not that it is harmless. Framed, with its source named, a record can say
> anything and still read as what it is: something a pack holds, quoted.

## 2. Quarantine

**R-HOST-056.** Every record's text MUST pass the host's admission of untrusted content before it is framed into a model's
context (in AgentMemory,
`IMemoryContextAdmissionPolicy`, which refuses or flags instruction-like text under the host's security mode).

## 3. Before a pack travels

**R-HOST-057.** A writer's lint MUST report every high-severity finding of its content scan (a secret, a payment card or
account number, an injection payload) as an error when asked to (`--safety`), and always for a pack of the `curated` or
`premium` tier.
