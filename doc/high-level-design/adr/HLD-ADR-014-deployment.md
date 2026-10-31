# HLD-ADR-014: Kubernetes with API/worker split, HA data tier, monorepo and Sealed Secrets

[Docs](../../README.md) › [High-level design](../high-level-design.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | QA-01, HG-2, HG-4, [high-level-design.md §9.5](../high-level-design.md#95-deployment-topology) |

## Context
The launch path must scale independently, and 99.9% availability needs failover in the data tier. One person builds and runs everything.

## Decision drivers
- Independent scaling of gateway, launch and workers.
- Automatic failover for stateful components.
- Simple for a solo developer and for reviewers.

## Considered options
- **Platform:** Kubernetes (local + cloud-ready) · Docker Compose only · managed cloud
- **Workers:** separate deployment · inside the API process
- **Broker / Redis HA:** RabbitMQ 3 nodes, Redis by role · all single · all HA
- **Repo:** monorepo with per-service pipelines · repo per service
- **Secrets:** Sealed Secrets · external vault
- **Worker autoscaling:** KEDA · HPA on CPU · fixed replicas
- **Backups:** WAL archiving + daily base backup · daily snapshot only · none
- **CI/CD:** GitHub Actions only · GitHub Actions + Argo CD

## Decision
Chosen:
- **Kubernetes** as the target (kind/k3d locally, cloud-ready), **Docker Compose** for development.
- Each service: **API deployment + worker deployment**. gateway and launch API autoscale horizontally.
- **RabbitMQ 3-node cluster with quorum queues.** Redis replica + failover for gateway and launch; single instances elsewhere.
- **Monorepo**: a folder per service, shared `.proto`, a shared plumbing-only library; CI builds and deploys only changed services.
- **Sealed Secrets** for secrets in git.
- Workers autoscale on RabbitMQ queue length with **KEDA**.
- **Backups**: CloudNativePG WAL archiving + daily base backup to object storage; RPO ≤ 5 min, RTO ≤ 1 h.
- **CI/CD**: GitHub Actions builds, tests, pushes and applies manifests for changed services (push-based).

## Pros and cons of the options
### Kubernetes
- ✅ Real autoscaling; cloud-ready.
- ❌ Steeper setup than Compose.

### API/worker split
- ✅ Independent scaling; consumer backlogs don't slow requests.
- ❌ Twice the deployments.

### Monorepo
- ✅ Easy for one person and for reviewers.
- ❌ Needs path-filtered CI.

## Consequences
- ✅ The launch path scales alone.
- ❌ Push-based deploys can drift from the repo if the cluster is changed by hand (R-09). Argo CD (GitOps) was considered and not chosen.

## Links
- [high-level-design.md §9.5](../high-level-design.md#95-deployment-topology)
