#!/usr/bin/env bash
# Usage: callers.sh <dumpdir> <Type.Method>  -- list methods whose ISIL calls Type.Method
d=$1; s="Call $2,"
grep -lF -- "$s" "$d"/*.txt 2>/dev/null | while read -r f; do
  awk -v s="$s" '/^=== /{m=$0; sub(/   RVA.*/,"",m); sub(/^=== [^ ]+ /,"",m)} index($0,s){print "   " m}' "$f"
done | sort -u
