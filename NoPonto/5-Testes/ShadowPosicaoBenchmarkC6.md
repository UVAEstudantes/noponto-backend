# Shadow pipeline C6 — disposable local run

Date: 2026-09-21. Branch: `auditoriaBack`; baseline HEAD: `26079d6`.
Environment: .NET 9, Docker Redis 7 and PostGIS 16-3.4 on isolated localhost ports, temporary PostgreSQL schema. No production data or services were used. The C6 harness is `ShadowPosicaoBenchmarkTests.C6_disposable_pipeline_benchmark`; run it only with disposable `REDIS_TEST_CONNECTION` (port 6397) and `POSTGIS_TEST_CONNECTION` (port 55439). The test rejects other ports.

Configuration: ChannelCapacity 10000, publisher and worker batches 100, retention disabled in the primary run. Synthetic t0 is fixed at 2026-09-21 12:00 UTC. Origins are built with the real `ShadowPosicaoFactory` and contain four candidate horizons. Warm-up of 20 origins was excluded. Each volume has three repetitions, with Stream keys and the PostgreSQL table cleared between repetitions. T0 is immediately before the first ingress offer; end is N rows plus lag=0 and pending=0. The harness uses the real hosted publisher and worker and the real PostgreSQL repository.

| Profile | Causal samples | JSON envelope bytes min / median / p95 / max / mean | Approx. Redis field bytes mean* |
|---|---:|---:|---:|
| SMALL | 2 | 2903 / 2931 / 2931 / 2931 / 2930.09 | 3010.09 |
| TYPICAL | 12 | 3512 / 3580 / 3580 / 3580 / 3577.79 | 3657.79 |
| NEAR_CAP | 30 | 4691 / 4831 / 4831 / 4831 / 4826.45 | 4906.45 |

*Approximation adds 64 bytes of origin ID and 16 bytes of Redis field names; excludes Stream/allocator/protocol overhead. Payload is well under the configured 65536-byte limit. Variation comes from deterministic IDs and numeric formatting. p95 here describes 100 synthetic items, not production payloads.

| N | E2E ms median (min–max) | Median events/s | Channel max occupancy | Drops | Published | PG batches | PG duration ms median | Pending / lag / DLQ / failures final |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 197.0 (194.4–212.2) | 507.6 | 100 | 0 | 100 | 2 | 140.6 | 0 / 0 / 0 / 0 |
| 500 | 620.9 (545.7–812.8) | 805.3 | 400 | 0 | 500 | 6 | 447.4 | 0 / 0 / 0 / 0 |
| 1000 | 1269.7 (1207.5–1271.3) | 787.6 | 900 | 0 | 1000 | 11 | 999.5 | 0 / 0 / 0 / 0 |
| 3000 | 3594.8 (3289.1–3722.8) | 834.5 | 2900 | 0 | 3000 | 31 | 2913.1 | 0 / 0 / 0 / 0 |

Rows in PostgreSQL equaled N in every run. Invalid/retry/DLQ/worker failures and publisher failures were zero. XADD success equaled N; expected XADD commands are approximately N. The typical run published about 3586 bytes per origin according to publisher metrics. Redis publish duration totals were 6–17 ms (100), 17–20 ms (500), 54–77 ms (1000), and 180–321 ms (3000) across repetitions. PostgreSQL batch processing dominated measured duration. With only three repetitions, p95 is not statistically meaningful and is omitted.

Additional runs: replaying 100 origin IDs offered/published/consumed 200 events but left 100 physical rows, 100 duplicate results and pending=0; E2E was 162.8 ms in the recorded run. Two hosted workers processed 500 events with 500 rows, zero physical duplicates, two consumer registrations, pending=0 and no observed `40P01`; E2E was 469.4 ms in the latest recorded run. This is not proof of production-scale multi-instance performance or of balanced distribution between workers.

With retention enabled during a separate 500-origin run, E2E was 540.2 ms, 500 rows were committed, pending and lag ended at zero, the explicit retention cycle returned `OK` and removed no recent entries, and the reporter returned an available, consistent backlog snapshot. This is one run only, not a comparative performance estimate. Existing C5 tests separately cover old/recent, pending, multi-group and TrimLimit behavior.

Failure-path run on disposable resources: five messages were read by a worker whose **real repository** pointed to an unavailable local PostgreSQL port. Failure took about 1.03 s; all five remained pending, five retry keys were incremented and given TTL, and the approximate retry command count was 10 (`INCR` + `EXPIRE` per item). A healthy worker claimed them, committed five rows and ACKed them; pending and retry keys returned to zero. This N+1 pattern is **ATTENTION**, not a blocker at this small failure-path volume. One poison message alongside a valid message yielded one DLQ entry, one additional row, and pending zero. A hosted publisher with an unavailable local Redis port recorded one failure and zero published events after about 5 s; the host stayed running. No durable event is promised before successful XADD.

Build: `dotnet build NoPonto.sln --no-restore` passed with 0 errors and the same five pre-existing warnings. Final full suite: **770/770**, 0 failed, 0 skipped. An intermediate full-suite run had two failures because the new benchmark and pre-existing integration tests raced on the same canonical Redis keys; the tests were put in one serial xUnit collection, with no runtime change, and the subsequent full suite passed. `git diff --check` passed.

Limits of this run: the harness has no direct per-item latency trace or reliable local CPU/memory series. The failure-path Redis and PostgreSQL endpoints were deliberately unavailable local ports; the disposable containers themselves were not stopped. Retry command count is inferred from the code, not measured with Redis MONITOR. The two-worker test confirms two consumer registrations, but does not quantify each worker's share. Local performance figures are not production capacity estimates. The final C6 verdict must consider these limitations and the final full-suite regression result.
