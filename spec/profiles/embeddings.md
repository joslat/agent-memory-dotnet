# Ampack Embeddings profile

## 1. Introduction

### 1.1 Scope

The Embeddings profile defines vectors a pack carries: each set of them signed content, read strictly by every verifier,
and reused by a host only on an exact match of the model that made them. Vectors never decide a walk or a trial verdict.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Embeddings profile when it holds a file under `embeddings/`.

## 2. A set

**R-EMB-001.** An embeddings set MUST be two files, `embeddings/<name>.st` and `embeddings/<name>.json`, its name 1 to 64
lowercase letters, digits or `-`, beginning with a letter or digit; every file under `embeddings/` MUST belong to a set, and
a pack MUST carry at most 8 sets.

### 2.1 The index

**R-EMB-002.** `embeddings/<name>.json` MUST be in the JSON profile, hold only these fields, each of them but `modelDigest`,
and name `format` `ampack.embeddings/1`:

<!-- registry:documents.embeddingsIndex.fields -->
| Field |
|---|
| `format` |
| `model` |
| `modelDigest` |
| `dtype` |
| `dimension` |
| `normalized` |
| `distance` |
| `rows` |
<!-- /registry -->

**R-EMB-003.** `model` MUST be one line of 1 to 200 characters, and `modelDigest`, when present, one line of 1 to 200; `dtype`
MUST be one of these, `dimension` an integer of 1 to 4,096, `normalized` a boolean, and `distance` one of these:

<!-- registry:embeddings -->
| Embedding | Values |
|---|---|
| `dataTypes` | `F32`, `F16` |
| `distances` | `cosine`, `dot`, `l2` |
<!-- /registry -->

**R-EMB-004.** `rows` MUST list 1 to 5,000 rows, each `[type, key]` (two strings) naming a record the pack holds, by the
names its record lines carry, each record at most once.

### 2.2 The tensor

**R-EMB-005.** `embeddings/<name>.st` MUST be in the safetensors layout: an 8-byte little-endian header length of 1 to
65,536, a header of that length in the JSON profile (trailing spaces allowed), then the data, and nothing after it.

**R-EMB-006.** The header MUST hold exactly one tensor, `vectors`, and no other member (`__metadata__` included), of exactly
`dtype`, `shape` and `data_offsets`; its `dtype` MUST be the index's, its `shape` `[rows, dimension]`, and its
`data_offsets` `[0, rows × dimension × 4]` for `F32`, `× 2` for `F16`.

**R-EMB-007.** The data MUST be little-endian, and MUST hold no NaN or infinite value.

> Why: the safetensors layout has readers in many languages and runs no code. The index holds what its header could only hold
> as strings, in the JSON profile, so it is signed and read like every other document.

## 3. Using vectors

**R-EMB-008.** A host MUST reuse a set's vectors only when its own embedder's model, model digest (both absent, or equal),
dimension, `dtype` and normalization all equal the index's; otherwise it MUST embed the records' text with its own model, and
it MUST NOT hold two models' vectors in one index.

**R-EMB-009.** Vectors MUST NOT decide a walk's order or a trial's verdict; a host MAY use them in its own retrieval, which a
trial then measures as any other.
