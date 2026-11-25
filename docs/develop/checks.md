# Checks

```sh
dotnet run --project tests/Tereditor.Checks -c Release
```

`-- --editor`, `-- --terminals`, `-- --ghostty-skia`, `-- --sync` and `-- --workspaces` run focused
subsets. `scripts/mise run check:terminal`, `-- --native-terminal` and `-- --native-texture` exercise real
Ghostty windows and open test windows. Set `TEREDITOR_TEST_GIT_SOURCE` to a local
clone of this repository to include the Git fixtures. See `src/Tereditor.Scintilla/README.md` for the
editor control's architecture and limits.

The full list of check modes is in [Agent reference](agent-reference.md).

## What CI runs

CI runs the argument-free gate (three shards of the same `full` run) and the focused modes `--sync`, `--workspaces` and
`--terminals`. The packaged jobs run `--packaged`, `--linux` and `--windows`, the macOS and Windows jobs
`--plugin-channel`. Everything else reaches CI as a `Gate.Case`: a focused mode that has no case is never run, and the
mode itself is the only place that would have caught a break.

Every mode has to say how it reaches CI. The `ci-coverage` case (also run by `-- --conformance`) reads the mode names from
`Program.cs` and the runner's `Main` and compares them with the table in `CiCoverageChecks.cs`: each mode lists the gate cases
that run it, the workflow job that runs it (the file must contain that step), or an exemption with its reason. A mode with no
entry, an entry for a mode that is gone, a case that nothing registers and a job that does not mention its mode all fail the
gate. A new mode therefore needs a `Gate.Case` (cheap), a workflow step (platform specific or slow) or a reason to stay out.
A gate case marked `slow: true` runs outside the merge gate (`ci.yml` takes `--lane merge`) in the nightly and in dispatchable `slow.yml` (`--lane slow`); the case still counts as the mode's CI coverage, and `ci-coverage` fails if those workflows lose their lane. The table pins a decision per mode; it does not prove that the listed cases run every line of the mode, so extend the case when
you extend the mode.

Exempt on purpose: modes that open a real window and take focus (`--native-*`, run by hand on macOS or from the dispatch-only
`slow.yml`), benchmarks and measurements (`--rendering-bench`, `--native-frame-pump`, `--native-surface-memory`), wall-clock
budgets that a shared runner stretches (`--switch-latency`, `--workspace-create-stall`), modes that need tools or artifacts no
job provides (`--lsp-real`, `--lsp-real-csharp`, `--native-fsevents`, `--android-package`, `--headscale`), and the fixture
children and flags that are not modes.

New gate cases are appended after the last case, so no earlier case changes its shard (cases are dealt to shards by position,
`ordinal % shards`). `--list-cases --shard k/3` shows where a case landed.
