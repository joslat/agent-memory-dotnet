/**
 * Finding, checking and loading the native library, and its ten C functions through koffi.
 *
 * The library is loaded from a known place only: an explicit path, else AGENTMEMORY_NATIVE_LIBRARY, else the platform
 * package of this binding (its `_native` folder, checked against its MANIFEST.json first). Nothing searches loader paths
 * or the working directory. A loaded library is never unloaded: it is pinned (pinLibrary), since koffi would unload it.
 */
import { createHash } from "node:crypto";
import { existsSync, readFileSync, realpathSync, statSync } from "node:fs";
import { createRequire } from "node:module";
import { arch, platform } from "node:os";
import { dirname, join } from "node:path";
import koffi from "koffi";
import { ABI_MAJOR } from "./abi.js";
import { NativeLibraryError } from "./errors.js";

/** The library's functions, with BigInt handles. Output parameters are one-element arrays. */
export interface NativeFunctions {
  readonly path: string;
  /** Whether the library is pinned in memory until the process ends (pinLibrary); false when the pin failed. */
  readonly pinned: boolean;
  am_v1_abi_version(): number;
  am_v1_open(abi: number, config: Uint8Array, length: number, outContext: unknown[], outError: unknown[], outErrorLength: unknown[]): number;
  am_v1_submit(context: bigint, request: Uint8Array, length: number, timeoutMs: number, outJob: unknown[]): number;
  am_v1_poll(context: bigint, job: bigint, waitMs: number, outDisposition: unknown[], outResult: unknown[], outLength: unknown[]): number;
  am_v1_cancel(context: bigint, job: bigint): number;
  am_v1_job_release(context: bigint, job: bigint): number;
  am_v1_buffer_free(buffer: unknown): void;
  am_v1_state(context: bigint, a: unknown[], b: unknown[], c: unknown[], d: unknown[], flags: unknown[]): number;
  am_v1_close(context: bigint): number;
  /** ABI 1.1; absent from a 1.0 library. Blocks the calling thread up to waitMs. */
  am_v1_wait?: (context: bigint, waitMs: number, outJob: unknown[]) => number;
}

// koffi's worker-thread calls (fn.async) are not used: on Linux a process that had used them failed as it exited.
type KoffiCall = (...args: any[]) => any;

const FILE_NAMES: Record<string, string> = { win32: "agentmemory_native.dll", darwin: "agentmemory_native.dylib" };

/** The library's file name on this platform. */
export function libraryFileName(): string {
  return FILE_NAMES[platform()] ?? "agentmemory_native.so";
}

/** The platform package's name for this machine: agentmemory-native-<os>-<cpu>[-gnu]. */
export function platformPackage(): string {
  const os = platform();
  const suffix = os === "linux" ? "-gnu" : "";
  return `agentmemory-native-${os}-${arch()}${suffix}`;
}

export interface Manifest {
  abi: { major: number; minor: number };
  platformTag: string;
  library: { file: string; size: number; sha256: string };
  [key: string]: unknown;
}

/** Where the library comes from: a path, and the manifest it must match when it is a packaged copy. */
export interface Resolved {
  path: string;
  manifest: Manifest | null;
}

/** The library to load; throws NativeLibraryError naming the fix when there is none. */
export function resolveLibrary(explicit?: string): Resolved {
  const given = explicit ?? process.env.AGENTMEMORY_NATIVE_LIBRARY;
  if (given) {
    if (!existsSync(given) || !statSync(given).isFile()) {
      throw new NativeLibraryError(`AgentMemory native library not found at ${given}`);
    }
    return { path: realpathSync(given), manifest: null };
  }
  const name = platformPackage();
  let folder: string;
  try {
    folder = dirname(createRequire(import.meta.url).resolve(`${name}/package.json`));
  } catch {
    throw new NativeLibraryError(
      `the platform package ${name} is not installed. npm installs it as an optional dependency of agentmemory-native, ` +
        `and skips it on a platform there is no package for (linux x64 and arm64 with glibc, macOS arm64, Windows x64) or ` +
        `with --omit=optional: reinstall without --omit=optional on one of those, or set AGENTMEMORY_NATIVE_LIBRARY to a ` +
        `library built for ${platform()} ${arch()}.`,
    );
  }
  const path = join(folder, "_native", libraryFileName());
  const manifestPath = join(folder, "_native", "MANIFEST.json");
  if (!existsSync(path)) throw new NativeLibraryError(`${name} has no library at ${path}; reinstall agentmemory-native`);
  const manifest = existsSync(manifestPath) ? (JSON.parse(readFileSync(manifestPath, "utf8")) as Manifest) : null;
  return { path: realpathSync(path), manifest };
}

/** Refuses a packaged library whose bytes are not the ones its manifest records. */
export function verifyAgainstManifest(path: string, manifest: Manifest): void {
  const data = readFileSync(path);
  const digest = createHash("sha256").update(data).digest("hex");
  if (data.length !== manifest.library.size || digest !== manifest.library.sha256) {
    throw new NativeLibraryError(
      `the native library at ${path} is not the one this package was built with (sha256 ${digest}, expected ` +
        `${manifest.library.sha256}); reinstall agentmemory-native`,
    );
  }
}

type Pin = (path: string) => boolean;
let pin: Pin | null | undefined; // undefined until first needed; null when this platform offers none

/** dlopen's flags: the library must already be loaded (never a second load), and no dlclose may unload it. */
const DLOPEN: Record<string, { libraries: string[]; flags: number }> = {
  // RTLD_NOW 0x2 | RTLD_NOLOAD 0x4 | RTLD_NODELETE 0x1000 (glibc). dlopen is in libc since glibc 2.34, libdl before.
  linux: { libraries: ["libc.so.6", "libdl.so.2"], flags: 0x2 | 0x4 | 0x1000 },
  // RTLD_NOW 0x2 | RTLD_NOLOAD 0x10 | RTLD_NODELETE 0x80.
  darwin: { libraries: ["/usr/lib/libSystem.B.dylib"], flags: 0x2 | 0x10 | 0x80 },
};

function makePin(): Pin | null {
  if (platform() === "win32") {
    // GET_MODULE_HANDLE_EX_FLAG_PIN: the module stays loaded until the process ends, however often FreeLibrary runs.
    const getModuleHandleEx = koffi.load("kernel32.dll").func(
      "int __stdcall GetModuleHandleExW(uint32_t, const char16_t *, _Out_ void **)",
    ) as KoffiCall;
    // It compares the path with the names of loaded modules, which use backslashes.
    return (path) => getModuleHandleEx(0x1, path.replace(/\//g, "\\"), [null]) !== 0;
  }
  const dl = DLOPEN[platform()];
  for (const library of dl?.libraries ?? []) {
    try {
      const dlopen = koffi.load(library).func("void *dlopen(const char *, int)") as KoffiCall;
      // The handle is never passed to dlclose: it is the reference that keeps the library loaded.
      return (path) => dlopen(path, dl!.flags) !== null;
    } catch {
      // the next library, or none
    }
  }
  return null;
}

/**
 * Keeps a library this process has loaded in memory until the process ends, whatever closes it: koffi unloads a library
 * when the last reference to it goes, which Node's teardown of the environment brings about as the process exits. The
 * AgentMemory library's .NET runtime keeps threads of its own that outlive that, and one that woke in unmapped code
 * crashed the process (SIGSEGV after all its work, NATIVE-TS-EXIT-ARM). Python's ctypes and the Rust crate never close
 * it. Best effort: false when the pin could not be taken. Never loads a library that is not loaded already.
 */
export function pinLibrary(path: string): boolean {
  if (pin === undefined) {
    try {
      pin = makePin();
    } catch {
      pin = null;
    }
  }
  try {
    return pin !== null && pin(path);
  } catch {
    return false;
  }
}

const loaded = new Map<string, NativeFunctions>();
let openSslPreloaded = false;

/**
 * Linux: loads the system's libcrypto and libssl with RTLD_DEEPBIND before the library, so each binds to its own
 * dependencies. Node exports the symbols of the OpenSSL it bundles, and without this the libssl that .NET loads for TLS
 * binds some of its libcrypto calls to Node's copy: the first TLS connection crashed the process (SIGSEGV), on Node 24
 * with the system's OpenSSL 3. .NET's own dlopen of the same library then gets these copies. Elsewhere .NET does not use
 * OpenSSL (Windows: SChannel; macOS: the Security framework). A system without them is left as it is: .NET then
 * reports TLS as unavailable.
 */
function preloadSystemOpenSsl(): void {
  if (openSslPreloaded || platform() !== "linux") return;
  openSslPreloaded = true;
  for (const [crypto, ssl] of [["libcrypto.so.3", "libssl.so.3"], ["libcrypto.so.1.1", "libssl.so.1.1"]] as const) {
    try {
      koffi.load(crypto, { deep: true });
      koffi.load(ssl, { deep: true });
      // Their koffi handles are dropped here, and koffi closes a library once nothing refers to it: pinned, or a
      // collection could unload them before .NET first uses TLS and .NET would load copies without DEEPBIND.
      pinLibrary(crypto);
      pinLibrary(ssl);
      return;
    } catch {
      // the next pair, or none
    }
  }
}

/** Loads the library once per path and declares its functions. */
export function load(explicit?: string): NativeFunctions {
  const resolved = resolveLibrary(explicit);
  const cached = loaded.get(resolved.path);
  if (cached) return cached;
  if (resolved.manifest) verifyAgainstManifest(resolved.path, resolved.manifest);
  preloadSystemOpenSsl();
  let lib: ReturnType<typeof koffi.load>;
  try {
    lib = koffi.load(resolved.path);
  } catch (error) {
    const builtFor = resolved.manifest ? ` This package was built for ${resolved.manifest.platformTag}.` : "";
    throw new NativeLibraryError(
      `cannot load the AgentMemory native library ${resolved.path} on ${platform()} ${arch()}, Node ${process.versions.node}: ` +
        `${(error as Error).message}.${builtFor} Install the agentmemory-native package built for this platform, or set ` +
        `AGENTMEMORY_NATIVE_LIBRARY to a library built for it.`,
    );
  }
  // Before any of its functions is called: once its .NET runtime has started, its threads must never lose their code.
  const pinned = pinLibrary(resolved.path);
  const optional = (definition: string): KoffiCall | undefined => {
    try {
      return lib.func(definition) as KoffiCall;
    } catch {
      return undefined;
    }
  };
  const version = optional("uint32_t am_v1_abi_version(void)");
  if (!version) throw new NativeLibraryError(`${resolved.path} is not an AgentMemory native library (no am_v1_abi_version export)`);
  const major = (version() as number) >>> 16;
  if (major !== ABI_MAJOR) {
    throw new NativeLibraryError(`${resolved.path} implements ABI ${major}.x; this binding needs ${ABI_MAJOR}.x`);
  }
  const poll = lib.func("int32_t am_v1_poll(uint64_t, uint64_t, uint32_t, _Out_ uint32_t *, _Out_ void **, _Out_ size_t *)") as KoffiCall;
  const wait = optional("int32_t am_v1_wait(uint64_t, uint32_t, _Out_ uint64_t *)") as NativeFunctions["am_v1_wait"];
  const functions: NativeFunctions = {
    path: resolved.path,
    pinned,
    am_v1_abi_version: version as () => number,
    am_v1_open: lib.func("int32_t am_v1_open(uint32_t, const uint8_t *, size_t, _Out_ uint64_t *, _Out_ void **, _Out_ size_t *)") as any,
    am_v1_submit: lib.func("int32_t am_v1_submit(uint64_t, const uint8_t *, size_t, uint32_t, _Out_ uint64_t *)") as any,
    am_v1_poll: poll as any,
    am_v1_cancel: lib.func("int32_t am_v1_cancel(uint64_t, uint64_t)") as any,
    am_v1_job_release: lib.func("int32_t am_v1_job_release(uint64_t, uint64_t)") as any,
    am_v1_buffer_free: lib.func("void am_v1_buffer_free(void *)") as any,
    am_v1_state: lib.func(
      "int32_t am_v1_state(uint64_t, _Out_ uint64_t *, _Out_ uint64_t *, _Out_ uint64_t *, _Out_ uint64_t *, _Out_ uint32_t *)",
    ) as any,
    am_v1_close: lib.func("int32_t am_v1_close(uint64_t)") as any,
    am_v1_wait: wait,
  };
  loaded.set(resolved.path, functions);
  return functions;
}

/** Whether the library offers am_v1_wait (ABI 1.1). */
export function hasWait(functions: NativeFunctions): boolean {
  return functions.am_v1_wait !== undefined;
}

/** Copies a library-owned buffer into a JS Buffer and frees it exactly once; null when there is none. */
export function takeBuffer(functions: NativeFunctions, pointer: unknown, length: unknown): Buffer | null {
  if (pointer === null || pointer === undefined || pointer === 0n || pointer === 0) return null;
  try {
    const size = Number(length ?? 0);
    return Buffer.from(size > 0 ? koffi.view(pointer, size).slice(0) : new ArrayBuffer(0));
  } finally {
    functions.am_v1_buffer_free(pointer);
  }
}

/** A handle as the BigInt it is: koffi gives a Number when the value fits one. */
export function handle(value: unknown): bigint {
  return typeof value === "bigint" ? value : BigInt(value as number);
}
