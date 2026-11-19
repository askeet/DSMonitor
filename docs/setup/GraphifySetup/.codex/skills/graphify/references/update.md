# Graphify reference: update and clustering

Use the executable resolved in the main skill. The examples below show arguments after that executable, for both PowerShell and macOS/Linux shells. Follow the main skill's freshness, maintenance and read-only rules.

## Incremental update

Run `update .` from the repository root when an update is required and writes are permitted. It handles incremental extraction and graph maintenance; do not manually reproduce the legacy intermediate-file and merge pipeline. Check the command result and report failures or incomplete verification. `check-update .` checks the non-code pending marker and does not establish code freshness.

If a graph is missing or unusable, `extract . --code-only` can build it when permitted. Keep extraction local and respect `.graphifyignore`. Check the installed CLI help for supported options before using additional flags.

## Clustering an existing graph

`cluster-only .` re-clusters the existing graph and regenerates its report and graph outputs. It writes files and does not re-extract changed source, so it is not a substitute for an update. Do not run it in strict read-only work or rerun the old intermediate-file pipeline afterward.
