# twelve-dollar-challenge-Http11Probe

A stripped-down fork of [MDA2AV/Http11Probe](https://github.com/MDA2AV/Http11Probe)
(rebased on the original's main), pared back to a fuzz test of the five official
endpoints of the
[twelve-dollar-server-challenge](https://github.com/arjaythedev/twelve-dollar-server-challenge)
feed API (`SPEC.md` + `test/test.sh` at the challenge repo root are the contract).

This fork is customised to test a valid submission: point it at any server that
contains a valid implementation of the challenge endpoints — locally on the
developer machine or inside the evaluation lima VM — and its PASS/FAIL verdict
covers each named endpoint being fuzz-first compliant with the official contract.

## What it does

Five endpoints, in order: `/health`, `/feed`, `GET /posts/:id`, `POST /posts`,
`POST /posts/:id/like`. Per endpoint, all bad requests go first (invalid ids,
tokens, bodies, plus the transport cases), the good request comes last. The
first failure aborts that endpoint only; the probe attempts all five. A probe
knows nothing about the seed database: it takes `--token` and `--known-post-id`.
PASS iff every endpoint is fully green.

Uncovered official cases (need `JWT_SECRET` to re-sign tokens) are reported as
SKIP: expired token, `sub` not an integer, missing `username`.

## Transport profiles

The four transport fuzz cases per endpoint are judged through a selectable
profile lens:

- `generic` (default) — the original probe's RFC allow-lists, judging any
  HTTP/1.1 server: a valid single-chunk request body is served (2xx, RFC 9112
  §7.1), a plain HTTP/1.0 request with Host is served (2xx) and the connection
  closes (RFC 9112 §9.3), a pipelined GET pair is answered 2xx + 2xx, a
  malformed request line closes the connection.
- `subset` — the lunet transport subset's exact answers: `TE: chunked` → 501,
  HTTP/1.0 → 400, pipelined pair → hard 400 + connection closed, malformed
  request line → connection closed.

The active profile is printed in the run's header line and carried as the
`profile` field of the `--output` JSON report.

## Build and run

```sh
dotnet publish src/Http11Probe.Cli -c Release
dotnet src/Http11Probe.Cli/bin/Release/net10.0/publish/Http11Probe.Cli.dll \
    --host 127.0.0.1 --port 3000 \
    --token "$(jq -r '.["1"].token' seed/tokens.json)" --known-post-id 1
```

`--category <name>` limits to one endpoint; `--test <step-id>` (repeatable)
limits to specific steps; `--output <file>` writes a JSON report;
`--profile generic|subset` picks the transport lens; `-v` prints raw
request/response per step.
