"""Validate additive mouse receipt ABI without connecting or sending input."""
import argparse
import ctypes
import os
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dll", type=Path)
    args = parser.parse_args()
    path = args.dll.resolve(strict=True)
    with os.add_dll_directory(str(path.parent)):
        native = ctypes.CDLL(str(path))
        enqueue = native.ycn_rdp_send_mouse_tracked
        enqueue.argtypes = [ctypes.c_int, ctypes.c_uint32, ctypes.c_uint16, ctypes.c_uint16, ctypes.POINTER(ctypes.c_uint64)]
        enqueue.restype = ctypes.c_int
        receipt = native.ycn_rdp_mouse_receipt
        receipt.argtypes = [ctypes.c_int, ctypes.c_uint64, ctypes.POINTER(ctypes.c_int)]
        receipt.restype = ctypes.c_int
        ticket, outcome = ctypes.c_uint64(999), ctypes.c_int(99)
        # An isolated process has no connected sessions. No input can be emitted.
        assert enqueue(0, 0x9800, 1, 1, ctypes.byref(ticket)) == -1
        assert enqueue(0, 0x0800, 1, 1, None) == -1
        assert enqueue(0, 0x0800, 1, 1, ctypes.byref(ticket)) == -8
        assert ticket.value == 0
        assert receipt(0, 0, ctypes.byref(outcome)) == -1
        assert receipt(0, 1, None) == -1
        assert receipt(0, 1, ctypes.byref(outcome)) == -4
        assert outcome.value == 0
    print("PASS 8 native mouse ABI assertions (no connection or input)")


if __name__ == "__main__":
    main()
