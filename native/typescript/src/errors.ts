import { statusName } from "./abi.js";

/** The library could not be found or loaded, or is not an AgentMemory native library of the right ABI. */
export class NativeLibraryError extends Error {
  override name = "NativeLibraryError";
}

/** A C ABI call returned a status other than OK. It describes the call, not an operation's outcome. */
export class NativeCallError extends Error {
  override name = "NativeCallError";
  readonly statusName: string;

  constructor(readonly fn: string, readonly status: number, detail = "") {
    super(`${fn} returned ${statusName(status)}${detail ? `: ${detail}` : ""}`);
    this.statusName = statusName(status);
  }
}

/** am_v1_open refused or failed; `code` and `message` come from the library's error document. */
export class OpenError extends NativeCallError {
  override name = "OpenError";
  readonly code: string;
  readonly detailMessage: string;

  constructor(status: number, document: { error?: { code?: string; message?: string } } | null) {
    const code = document?.error?.code ?? "unknown";
    const message = document?.error?.message ?? "";
    super("am_v1_open", status, `${code}: ${message}`);
    this.code = code;
    this.detailMessage = message;
  }
}

/** A job's work was given up on by the caller (an AbortSignal), after its cancellation reached the library. */
export class JobAbortedError extends Error {
  override name = "AbortError";
}
