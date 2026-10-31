# How a host frames records for a model

How AgentMemory puts a pack's records into a model's context (feature 04). A record is data, never an instruction: it reaches
the model inside a frame that says where it came from, and nothing in its text can close or forge a frame.

## 1. The frame

**R-HOST-053.** A host MUST put every record a pack brought into a model's context inside a frame, never as bare text: an
element `record` with the attributes `pack` (the pack's id), `version` (the pack's version), `type` and `key`, then the
record's text, then the closing tag. The frames of one section MUST stand inside the section's one `recalled_memory`
element, which names its category.

**R-HOST-054.** Inside a frame, `<` and `>` MUST be written `&lt;` and `&gt;`, and inside an attribute the double quote too
(`&quot;`), so no text can close its frame, open another, or change what a header says.

**R-HOST-055.** A frame MUST be applied whatever the pack's trust, its tier or the host's admission decision: a record the
admission lets through is framed; one it refuses is left out.

> Why: a signature proves who wrote a record, not that it is harmless. Framed, with its source named, a record can say
> anything and still read as what it is: something a pack holds, quoted.

## 2. Quarantine

**R-HOST-056.** Every record's text MUST pass the host's admission of untrusted content before it is framed (in AgentMemory,
`IMemoryContextAdmissionPolicy`, which refuses or flags instruction-like text under the host's security mode).
