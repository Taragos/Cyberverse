#!/usr/bin/env bash
# Prints the log of the latest local server run (tools/linux/run-server.sh), e.g. to check a test session.
#
#   tools/linux/last-server-log.sh             the whole log
#   tools/linux/last-server-log.sh -w          only warnings and errors (with their stack traces)
#   tools/linux/last-server-log.sh -n 50       the last 50 lines
#   tools/linux/last-server-log.sh -f          follow it while the server runs
#   tools/linux/last-server-log.sh -p          only print the file's path
#   tools/linux/last-server-log.sh -r 2        the run before the latest (1 = latest)
#
# The folder is CYBERVERSE_LOG_DIR, by default server/run/logs/ (where run-server.sh writes).
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dir="${CYBERVERSE_LOG_DIR:-$repo/server/run/logs}"
warnings=0 lines="" follow=0 path_only=0 run=1

while getopts "wn:fpr:h" opt; do
    case "$opt" in
        w) warnings=1 ;;
        n) lines="$OPTARG" ;;
        f) follow=1 ;;
        p) path_only=1 ;;
        r) run="$OPTARG" ;;
        *) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
    esac
done

# The run files are named server-<yyyy-MM-dd_HH-mm-ss>.log, so the name sorts by time.
file="$(ls -1 "$dir"/server-*.log 2>/dev/null | sort -r | sed -n "${run}p" || true)"
[[ -n "$file" ]] || { echo "No server logs in $dir; start the server with tools/linux/run-server.sh first." >&2; exit 1; }

if (( path_only )); then
    echo "$file"
    exit 0
fi

echo "== $file" >&2

# A log entry starts with a timestamp; lines without one (stack traces) belong to the entry above.
filter() {
    if (( warnings )); then
        awk '/^[0-9]{4}-[0-9]{2}-[0-9]{2} / { keep = ($0 ~ /\|(WARN|ERROR|FATAL)\|/) } keep'
    else
        cat
    fi
}

if (( follow )); then
    tail -n "${lines:-+1}" -F "$file" | filter
elif [[ -n "$lines" ]]; then
    filter < "$file" | tail -n "$lines"
else
    filter < "$file"
fi
