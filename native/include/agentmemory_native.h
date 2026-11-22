/*
 * AgentMemory native library, C ABI version 1.0.
 *
 * One shared library (agentmemory_native.so / .dll / .dylib) built with .NET Native AOT. It carries its own runtime:
 * the host needs no .NET installation. Any language with a C foreign-function interface can use it; the Python package
 * agentmemory_native wraps it for agents, agent frameworks and the speech-to-speech server.
 *
 * Shape of the contract
 *   - Every export uses the C calling convention and fixed-width types. No managed object, task, exception or
 *     callback crosses this boundary.
 *   - Requests and results are UTF-8 JSON documents, versioned by "schemaVersion". Operations are named like the
 *     AgentMemory MCP tools (for example "memory_get_context").
 *   - Work is asynchronous: am_v1_submit schedules a job and returns at once; am_v1_poll delivers its result.
 *   - Three status layers stay separate: the int32 return value describes the call itself; the poll disposition says
 *     whether a result is available; the result JSON carries the operation's own status.
 *   - Handles are opaque 64-bit values that are never reused within a process, so a released or stale handle can
 *     only ever be refused, never mistaken for a newer one.
 *   - Every buffer the library returns is owned by the caller and must be freed exactly once with am_v1_buffer_free.
 *   - The library is never unloaded. am_v1_close disposes a logical context only.
 *   - All functions are thread-safe. None calls back into the host.
 */
#ifndef AGENTMEMORY_NATIVE_H
#define AGENTMEMORY_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define AM_V1_ABI_MAJOR 1u
#define AM_V1_ABI_MINOR 1u
#define AM_V1_ABI_VERSION ((AM_V1_ABI_MAJOR << 16) | AM_V1_ABI_MINOR)

typedef uint64_t am_v1_context_t;
typedef uint64_t am_v1_job_t;

/* Return values of every int32_t export: the boundary call only, never the operation's outcome. */
#define AM_V1_OK                0 /* The call did what it says. */
#define AM_V1_INVALID_ARGUMENT  1 /* Null or bad pointer/length, malformed UTF-8 or JSON, or a request the schema refuses. */
#define AM_V1_UNSUPPORTED_ABI   2 /* The requested ABI major version is not this library's. */
#define AM_V1_INVALID_HANDLE    3 /* Unknown, closed, released or foreign context or job handle. */
#define AM_V1_OVERLOADED        4 /* No capacity: the job was not admitted. Nothing ran. */
#define AM_V1_CLOSING           5 /* The context is closing and admits no new work. */
#define AM_V1_INTERNAL_ERROR    6 /* An unexpected failure inside the library; details in the error buffer where one exists. */
#define AM_V1_BUSY              7 /* open: the authority is already held. close: work has not settled yet; call again. */

/* Poll dispositions. */
#define AM_V1_PENDING  0u /* No result yet; the result pointer is NULL and the length 0. */
#define AM_V1_TERMINAL 1u /* A result is available; the caller owns the returned buffer. */

/* State flags of am_v1_state. */
#define AM_V1_READ_ALLOWED      1u
#define AM_V1_ADMISSION_ALLOWED 2u
#define AM_V1_WRITE_QUIESCENT   4u
#define AM_V1_CLOSING_FLAG      8u
#define AM_V1_ERASE_PENDING    16u

/* (major << 16) | minor of this library's ABI. Cheap; call it first. */
uint32_t am_v1_abi_version(void);

/*
 * Opens a logical context from a UTF-8 JSON configuration (store connection, owner binding, profile, limits).
 * On AM_V1_OK *out_context holds the new handle. On failure *out_context is 0 and, when out_error_utf8 is not NULL,
 * *out_error_utf8 receives a JSON error document the caller frees with am_v1_buffer_free (NULL if none).
 * May connect to the store: call it from a worker thread, not from a realtime loop.
 */
int32_t am_v1_open(
    uint32_t requested_abi,
    const uint8_t *config_utf8, size_t config_len,
    am_v1_context_t *out_context,
    uint8_t **out_error_utf8, size_t *out_error_len);

/*
 * Schedules one operation. The request bytes are copied before the call returns. remaining_timeout_ms bounds the
 * operation (0 = the context's default). AM_V1_OK means the job was admitted and scheduled, nothing more.
 * AM_V1_INVALID_ARGUMENT only for what cannot become a job: a NULL pointer, a zero length or a request over the size
 * limit. A request whose content is wrong (not UTF-8, not JSON, a repeated property, a schema error) is admitted and
 * ends at once with status "invalid" and a message, having done nothing.
 */
int32_t am_v1_submit(
    am_v1_context_t context,
    const uint8_t *request_utf8, size_t request_len,
    uint32_t remaining_timeout_ms,
    am_v1_job_t *out_job);

/*
 * Returns a job's result when it is ready. wait_ms = 0 returns at once; otherwise the call waits at most wait_ms for the
 * job to finish, and returns early when it does or when the context starts closing. Every terminal poll returns a new
 * buffer that the caller owns, independent of the job and the context.
 */
int32_t am_v1_poll(
    am_v1_context_t context, am_v1_job_t job,
    uint32_t wait_ms,
    uint32_t *out_disposition,
    uint8_t **out_result_utf8, size_t *out_result_len);

/*
 * ABI 1.1. Waits up to wait_ms for any job of this context to reach its terminal state that this function has not
 * reported before. On AM_V1_OK, *out_job is that job, or 0 when wait_ms passed with none, or when the context started
 * closing (then it returns at once). Each job is reported at most once; take its result with am_v1_poll(context, job, 0,
 * ...). A released job is never reported. Thread-safe; normally one waiter per context, so a binding needs one thread
 * per context instead of one per job in flight. A 1.0 library has no such export: look it up before calling it.
 */
int32_t am_v1_wait(am_v1_context_t context, uint32_t wait_ms, am_v1_job_t *out_job);

/* Asks the job to stop. Cooperative: the work may still finish; the capacity it holds is returned only when it does. */
int32_t am_v1_cancel(am_v1_context_t context, am_v1_job_t job);

/* Gives up the caller's handle. Unfinished work stays the library's responsibility and keeps its capacity. */
int32_t am_v1_job_release(am_v1_context_t context, am_v1_job_t job);

/* Frees a buffer returned by this library. NULL is ignored. Any other pointer, or a second free, is undefined. */
void am_v1_buffer_free(void *buffer);

/* One coherent snapshot of the context's revisions and flags. No I/O. */
int32_t am_v1_state(
    am_v1_context_t context,
    uint64_t *out_state_revision,
    uint64_t *out_policy_epoch,
    uint64_t *out_deletion_epoch,
    uint64_t *out_memory_revision,
    uint32_t *out_flags);

/*
 * Starts closing: new submits are refused, running jobs are asked to stop. Returns AM_V1_BUSY while any job's work is
 * still running; call again. AM_V1_OK means the context is disposed and its handle is no longer valid.
 */
int32_t am_v1_close(am_v1_context_t context);

#ifdef __cplusplus
}
#endif

#endif /* AGENTMEMORY_NATIVE_H */
