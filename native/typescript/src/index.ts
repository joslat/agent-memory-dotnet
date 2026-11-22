/** agentmemory-native for Node.js and Bun: AgentMemory's native library over its C ABI (native/CONTRACT.md). */
export { ABI_MAJOR, ABI_MINOR, ABI_VERSION, Flags, Status, statusName } from "./abi.js";
export { Job, MemoryClient, getContextRequest, type ContextState, type OpenOptions } from "./client.js";
export { JobAbortedError, NativeCallError, NativeLibraryError, OpenError } from "./errors.js";
export { hasWait, libraryFileName, load, platformPackage, resolveLibrary, verifyAgainstManifest, type Manifest } from "./library.js";
export * from "./results.js";
