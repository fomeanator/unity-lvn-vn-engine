# Contributing to LVN

Thanks for your interest. LVN is a small, sharp toolkit; contributions that keep
it that way are very welcome.

## The shape of the project

- **`.lvn` is the contract.** Everything compiles to it; the runtime plays it.
  Keep the container small and declarative — new capability is a new command or
  staging tag, documented in `docs/`, not a special case in a player.
- **Unknown is an error.** A new op or tag must be registered (Go `KnownOps`,
  C# `StagingOps`, the docs) — never silently ignored.
- **Stable ids.** Don't change how labels/choices/endings are identified; saves
  and analytics depend on them.

## Adding a new authoring front-end

The point of LVN is that formats plug in without touching the runtime:

1. Add a package under `tools/lvnconv/internal/<format>/` exposing
   `Convert(...) (*Doc, error)`.
2. Wire it into `main.go`'s `convert` dispatch and `detectFormat`.
3. Emit only registered ops/tags; lean on `lvn.Validate` in tests.
4. Document the mapping in `docs/staging-tags.md`.

## Dev loop

```sh
# Go (each module is standalone; the root go.work is a convenience)
cd tools/lvnconv && go test ./... && gofmt -l .
cd server        && go vet ./...  && go build .

# end-to-end
cd tools/lvnconv && go run . convert -i ../../examples/hello.ink -o /tmp/h.lvn && go run . validate /tmp/h.lvn
```

CI runs `gofmt`, `go vet`, `go build`, `go test` on every module plus a convert
→ validate smoke test. Keep it green; format with `gofmt -w` before pushing.

The Unity package (`unity/Packages/com.lvn.engine`) isn't covered by CI yet —
if you change it, build it once in Unity (2021.3+, with
`com.unity.nuget.newtonsoft-json`) and say so in the PR.

## Pull requests

Keep them focused, describe the user-visible change, and update the docs and
`CHANGELOG.md` when behavior changes. By contributing you agree your work is
licensed under the repository's MIT license.
