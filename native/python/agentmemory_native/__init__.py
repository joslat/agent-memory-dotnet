"""AgentMemory for Python through the AgentMemory native library (C ABI 1.0, no .NET installation needed).

    from agentmemory_native import MemoryClient

    with MemoryClient(config) as memory:
        result = memory.get_context(session_id="s1", query_vector=vector)

Every request carries its query vector; owner binding, limits and the store come from the configuration
(see native/CONTRACT.md). The async client runs every native call off the event loop.
"""
from ._library import ABI_MAJOR, ABI_MINOR, library_file_name, resolve_library_path
from .client import AsyncMemoryClient, Job, MemoryClient, get_context_request
from .errors import NativeCallError, OpenError
from .results import ContextResult, ContextState, Fact, OperationError, Preference

__all__ = [
    "ABI_MAJOR", "ABI_MINOR", "AsyncMemoryClient", "ContextResult", "ContextState", "Fact", "Job", "MemoryClient",
    "NativeCallError", "OpenError", "OperationError", "Preference", "get_context_request", "library_file_name",
    "resolve_library_path",
]
__version__ = "0.1.0"
