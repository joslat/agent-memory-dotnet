// SPDX-License-Identifier: Apache-2.0
/** A pack (or a document beside it) is not accepted. `reason` says why. */
export class Refused extends Error {
  readonly reason: string;
  constructor(reason: string);
}

/** Bytes, or for a trust policy or a revocation document also its JSON text. */
export type Document = Uint8Array | ArrayBuffer | string;

export interface Options {
  /** The host's revocation documents, applied to the trust policy first. */
  revocations?: Document[];
  /** The moment to judge validity windows at. Default: now. */
  at?: Date;
}

export interface PackInfo {
  id: string;
  version: string;
  /** "sha256:" and the hex of the pack's signed file list. */
  digest: string;
  /** knowledge, definition, snapshot, composite or set. */
  kind: string;
  /** The profiles the pack uses; ["core"] when none. */
  profiles: string[];
}

export type Verdict = ({ verified: true } & PackInfo) | { verified: false; reason: string };

export interface Summary {
  files: number;
  bytes: number;
  id?: string;
  version?: string;
  kind?: string;
  digest?: string;
  envelopes: number;
  title?: string;
}

export interface Link {
  relation: string;
  /** Another record's key. */
  target: string;
}

export interface PackRecord {
  type: string;
  key: string;
  properties: Record<string, unknown>;
  links: Link[];
  /** The other fields the format allows on a line (validFrom, expiresAt, confidenceMicro, ...), not checked here. */
  extra: Record<string, unknown>;
  /** The file inside the pack, and the line number, for a message. */
  file: string;
  line: number;
}

/** A verified pack. Create one with load(). */
export class Pack {
  private constructor();
  readonly id: string;
  readonly version: string;
  /** Not given for a member of a set in this preview. */
  readonly digest: string | undefined;
  readonly kind: string;
  readonly profiles: string[];
  /** The paths of every file of the pack, as verified. */
  readonly paths: string[];
  has(path: string): boolean;
  bytes(path: string): Uint8Array | undefined;
  text(path: string): string | undefined;
  json(path: string): unknown;
  readonly manifest: Record<string, any>;
  readonly title: string | undefined;
  readonly card: Record<string, any> | undefined;
  readonly ontology: Record<string, any> | undefined;
  readonly recordFiles: string[];
  /** The pack's own record lines; each line is checked for its shape only. Throws Refused on a malformed line. */
  records(): PackRecord[];
  /** For a pack set: its member packs, each already verified on its own. */
  readonly members: Pack[];
}

/** Verifies a pack (or a pack set and each member) against a trust policy. Never throws for a refusal. */
export function verify(archive: Uint8Array | ArrayBuffer, trust: Document, options?: Options): Verdict;

/** What an archive says about itself. No signature is judged: never treat this as a verdict. */
export function inspect(archive: Uint8Array | ArrayBuffer): Summary;

/** Verifies a pack as verify() does and returns it for reading. Throws Refused; nothing comes out of a refused pack. */
export function load(archive: Uint8Array | ArrayBuffer, trust: Document, options?: Options): Pack;
