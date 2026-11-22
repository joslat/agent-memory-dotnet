/** R-WAITER-CLOSE's other thread: blocks in am_v1_wait for up to 20 s on a context the main thread then closes. */
import { parentPort, workerData } from "node:worker_threads";
import { handle, load } from "../src/library.js";

const fn = load(workerData.library as string);
const out: unknown[] = [0];
parentPort!.postMessage({ kind: "waiting" });
const started = Date.now();
const status = fn.am_v1_wait!(BigInt(workerData.context as string), 20_000, out);
parentPort!.postMessage({ kind: "woken", status, job: handle(out[0]).toString(), ms: Date.now() - started });
