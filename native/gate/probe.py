"""Calls the feasibility probe export of the native library. Usage: python3 probe.py <library>."""
import ctypes
import sys

library = ctypes.CDLL(sys.argv[1])
probe = library.am_probe_recall
probe.argtypes = []
probe.restype = ctypes.c_int
result = probe()
print(f"am_probe_recall returned {result}", flush=True)
sys.exit(0 if result >= 0 else 1)
