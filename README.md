# twelve-dollar-challenge-Http11Probe

A stripped-down fork of [uNetworkingAB/Http11Probe](https://github.com/uNetworkingAB/Http11Probe),
pared back to a fuzz test of the five official endpoints of the
[twelve-dollar-server-challenge](https://github.com/arjaythedev/twelve-dollar-server-challenge)
feed API (`SPEC.md` + `test/test.sh` at the challenge repo root are the contract).

This fork is customised to test a valid submission: point it at any server that
contains a valid implementation of the challenge endpoints — locally on the
developer machine or inside the evaluation lima VM — and its PASS/FAIL verdict
covers each named endpoint being fuzz-first compliant with the official contract.

## What it does

Five endpoints, in order: `/health`, `/feed`, `GET /posts/:id`, `POST /posts`,
`POST /posts/:id/like`. Per endpoint, all bad requests go first (invalid ids,
tokens, bodies, plus the transport cases — TE: chunked → 501, HTTP/1.0 → 400,
malformed request line → connection closed, pipelined pair → hard 400),
the good request comes last. The first failure aborts that endpoint only;
the probe attempts all five. A probe knows nothing about the seed database:
it takes `--token` and `--known-post-id`. PASS iff every endpoint is fully green.

Uncovered official cases (need `JWT_SECRET` to re-sign tokens) are reported as
SKIP: expired token, `sub` not an integer, missing `username`.

## Build and run

```sh
dotnet publish src/Http11Probe.Cli -c Release
dotnet src/Http11Probe.Cli/bin/Release/net10.0/publish/Http11Probe.Cli.dll \
    --host 127.0.0.1 --port 3000 \
    --token "$(jq -r '.["1"].token' seed/tokens.json)" --known-post-id 1
```

`--category <name>` limits to one endpoint; `--test <step-id>` (repeatable)
limits to specific steps; `--output <file>` writes a JSON report; `-v` prints
raw request/response per step.
