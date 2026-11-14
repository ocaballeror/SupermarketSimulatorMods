#!/usr/bin/env bash
# Usage: isil.sh <dumpfile.txt> <Type::Method>   -- prints condensed ISIL of all matching overloads
f=$1; m=$2
awk -v s="$m" 'index($0, "=== ")==1 && index($0, s"(")>0 {p=1; print; next} p && /^=== /{p=0; q=0} p && /^--- ISIL/{q=1; next} p && q' "$f" \
 | grep -vE "ShiftStack|Move stack:0x0, r|Move r[a-z0-9]+, stack:0x0$|il2cpp_codegen_initialize_runtime_metadata|LoadAddress rcx, \[0x18|Compare r[a-z0-9]+, 0$|Nop $|Interrupt|^\s*$"
