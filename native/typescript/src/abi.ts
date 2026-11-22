/** The C ABI's constants (native/include/agentmemory_native.h), ABI 1.1. */

export const ABI_MAJOR = 1;
export const ABI_MINOR = 1;
export const ABI_VERSION = (ABI_MAJOR << 16) | ABI_MINOR;

export const Status = {
  OK: 0,
  INVALID_ARGUMENT: 1,
  UNSUPPORTED_ABI: 2,
  INVALID_HANDLE: 3,
  OVERLOADED: 4,
  CLOSING: 5,
  INTERNAL_ERROR: 6,
  BUSY: 7,
} as const;

export type StatusName = keyof typeof Status;

const NAMES = new Map<number, StatusName>(Object.entries(Status).map(([name, value]) => [value, name as StatusName]));

/** The name of a status code, or `UNKNOWN(n)`. */
export function statusName(status: number): string {
  return NAMES.get(status) ?? `UNKNOWN(${status})`;
}

export const PENDING = 0;
export const TERMINAL = 1;

export const Flags = {
  READ_ALLOWED: 1,
  ADMISSION_ALLOWED: 2,
  WRITE_QUIESCENT: 4,
  CLOSING: 8,
  ERASE_PENDING: 16,
} as const;
