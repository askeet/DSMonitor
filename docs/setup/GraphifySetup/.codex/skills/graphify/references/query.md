# Graphify reference: advanced navigation

Use the executable resolved in the main skill. The examples below show arguments after that executable, for both PowerShell and macOS/Linux shells. Follow its freshness and read-only rules first.

## Traversal

- `query "question"` uses breadth-first traversal for nearby context. `query "question" --dfs` follows a deeper chain; `path "A" "B"` finds a shortest path.
- `query "question" --budget 1500` limits output. Adjust the budget only when needed, and narrow the question if output is truncated.
- `explain "symbol"` examines one node; `affected "symbol"` explores impact.

If a query misses relevant nodes, inspect existing graph labels and retry with actual graph terms, including translated or alternate terminology. Do not invent nodes or edges. Check source locations and confidence, verify important relationships in source, and identify missing coverage. For a hub or bridge, source searches for distinctive operations can reveal bypasses with no graph edge; report those separately from graph relationships.

## Optional feedback

When writes are allowed and recording feedback is appropriate, `save-result` can record a question, answer, type and cited nodes. It supports `--outcome` values `useful`, `dead_end`, and `corrected`, with `--correction` for a correction. `reflect --if-stale` can refresh lessons under `graphify-out/reflections/`. These commands write files; do not run them in strict read-only work. Consult the installed CLI help for current arguments.
