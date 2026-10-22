#!/usr/bin/env bash
# usage: timed.sh <stepsTsv> <name> <logFile> -- <command...>
# Runs the command, appends "name<TAB>exit<TAB>seconds" to stepsTsv, and returns the exit code.
set -u
tsv="$1"; name="$2"; logf="$3"; shift 4
start=$(date +%s.%N)
"$@" > "$logf" 2>&1
rc=$?
end=$(date +%s.%N)
printf '%s\t%s\t%s\n' "$name" "$rc" "$(echo "$end - $start" | bc)" >> "$tsv"
exit $rc
