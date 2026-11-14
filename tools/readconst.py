#!/usr/bin/env python3
"""Read literal constants that ISIL only shows as addresses, e.g. `Move xmm1, [0x1838CEBA8]`.

Usage: tools/readconst.py <address> [more...] [--type f|d|i|q]   (default f = float32)
       GAME_ASSEMBLY=/path/GameAssembly.dll tools/readconst.py ...

Addresses are virtual addresses as printed in the dumps (image base 0x180000000).
Cpp2IL sometimes mislabels a float load as `Move xmm1, typeof(T)`; take the address from the
raw x86 line above the ISIL instead (e.g. `movss xmm1,dword ptr [1838CEBECh]`).
"""
import os
import struct
import sys

DEFAULT = os.path.expanduser(
    "~/.local/share/Steam/steamapps/common/Supermarket Simulator/GameAssembly.dll")
FMT = {"f": "<f", "d": "<d", "i": "<i", "q": "<q"}


def main(argv):
    kind = "f"
    if "--type" in argv:
        i = argv.index("--type")
        kind = argv[i + 1]
        del argv[i:i + 2]
    if not argv:
        print(__doc__)
        return 1
    data = open(os.environ.get("GAME_ASSEMBLY", DEFAULT), "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    optsz = struct.unpack_from("<H", data, pe + 20)[0]
    base = struct.unpack_from("<Q", data, pe + 24 + 24)[0]
    secs = []
    for s in range(nsec):
        o = pe + 24 + optsz + s * 40
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, o + 8)
        secs.append((va, vsize, rsize, raw))
    for a in argv:
        addr = int(a.rstrip("h"), 16)
        rva = addr - base if addr >= base else addr
        for va, vsize, rsize, raw in secs:
            if va <= rva < va + max(vsize, rsize):
                off = rva - va
                if off >= rsize:  # virtual-only tail: zero-filled at load, runtime-written data
                    print(f"{hex(addr)}  <zero-initialized (.bss-like); value only known at runtime>")
                else:
                    print(f"{hex(addr)}  {struct.unpack_from(FMT[kind], data, raw + off)[0]}")
                break
        else:
            print(f"{hex(addr)}  <not in any section>")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
