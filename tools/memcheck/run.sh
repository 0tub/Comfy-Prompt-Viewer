#!/usr/bin/env bash
# Memory regression check (Linux, needs a display). Opens a generated folder in the real app with an empty cache,
# scrolls the whole grid, and fails if RSS exceeds the budget. Usage: tools/memcheck/run.sh [count] [limit_mb]
set -euo pipefail
count=${1:-3700}; limit=${2:-220}
root=$(cd "$(dirname "$0")/../.." && pwd); cd "$root/rust"
cargo build -q --release -p comfy-prompt-viewer --example genfolder 2>/dev/null || cargo build -q --release -p cpv-core --example genfolder
cargo build -q --release -p comfy-prompt-viewer
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
./target/release/examples/genfolder "$work/images" "$count" 512
rows=$((count / 7 + 1)); steps="select:0"
for ((r = 0; r < rows; r += 20)); do steps+=",scrollrow:$r"; done
steps+=",report,quit"
# A private data dir keeps the cache cold and leaves the user's settings alone.
out=$(XDG_DATA_HOME="$work/data" CPV_STEP_MS=500 CPV_SCRIPT="$steps" timeout 600 ./target/release/comfy-prompt-viewer "$work/images" | grep RSS_MB)
echo "$out (limit ${limit} MB)"
peak=$(sed -E 's/.*PEAK_MB=([0-9]+).*/\1/' <<<"$out")
[ "$peak" -le "$limit" ] || { echo "FAIL: peak RSS $peak MB exceeds $limit MB"; exit 1; }
