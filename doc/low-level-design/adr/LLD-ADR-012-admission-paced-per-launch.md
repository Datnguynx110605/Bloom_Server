# LLD-ADR-012: Admission is paced per launch, not per replica

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-12 |
| **Deciders** | Project owner |
| **Related** | LR-06, HLD-ADR-012, HLD-ADR-014, [01-launch.md §4.2](../01-launch.md#42-redis-launchs-own-instance-aof--replica), [§6.4](../01-launch.md#64-admission-tick-worker-every-1-s-per-launch-in-preorder_open-and-not-closed), [§7](../01-launch.md#7-concurrency-and-consistency) |

## Context
01-launch §6.4 takes `pg_try_advisory_xact_lock` per tick. That only stops ticks that overlap in time. Each replica runs its own 1 s timer, and a tick commits in about 10 ms. So N replicas whose timers are out of phase each take the lock in turn, and together admit N × `admission_rate` per second. Passes are still capped at the units left (`available − outstanding`), so LR-01 and LR-02 still hold; what breaks is the pacing that protects checkout (LR-06, HLD-ADR-012). HLD-ADR-014 autoscales workers, so more than one replica is expected.

## Decision drivers
- Admission never exceeds the configured rate (LR-06), however many replicas run.
- No new table or lock.
- The pacing check and the pop must be one atomic step.

## Considered options
1. Tick key in the Lua script: the script sets `wr:{L}:tick NX PX 1000` and pops nothing when the key already exists
2. Last-admission timestamp: add `launches.last_admission_at`, checked and set inside the locked tick transaction
3. One admitting replica: run admission on a single replica, through leader election or a session-scoped advisory lock

## Decision
Chosen: **tick key in the Lua script**.

- The admission script first does `SET wr:{L}:tick 1 NX PX 1000`. If the key exists, it pops nothing and returns 0.
- The advisory lock stays; the tick key adds the pacing across ticks and replicas.

## Pros and cons of the options
### Tick key in the Lua script
- ✅ One atomic step with the pop.
- ✅ Adds no new table or lock.
- ➖ One more Redis key per active launch (1000 ms TTL).

### Last-admission timestamp
- ✅ Pacing state lives in Postgres, next to the lock.
- ❌ A new column on `launches` and a write on every tick.

### One admitting replica
- ✅ No pacing state.
- ❌ Needs leader election or a session-scoped lock, and a failover path.

## Consequences
- ✅ Admitted per second ≤ `admission_rate` for any number of replicas, including ticks out of phase.
- ➖ New Redis key `wr:{launchId}:tick` (01-launch §4.2); §6.4 step 7 gains the guard; §7 row "Two admission workers" and test L-07 are extended.

## Links
- [01-launch.md §4.2](../01-launch.md#42-redis-launchs-own-instance-aof--replica)
- [01-launch.md §6.4](../01-launch.md#64-admission-tick-worker-every-1-s-per-launch-in-preorder_open-and-not-closed)
- [01-launch.md §7](../01-launch.md#7-concurrency-and-consistency)
- [HLD-ADR-012](../../high-level-design/adr/HLD-ADR-012-launch-path.md)
