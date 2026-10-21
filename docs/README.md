# Documentation index

| Document | Read it to learn |
|----------|------------------|
| [architecture/overview.md](architecture/overview.md) | How the system works, in one page |
| [architecture/diagrams.md](architecture/diagrams.md) | The C4 diagrams, rendered (generated) |
| [architecture/c4/](architecture/c4/) | The C4 model in LikeC4. This is the source of the diagrams |
| [adr/README.md](adr/README.md) | Every decision, its status and its reason |
| [specs/README.md](specs/README.md) | What the system does, as Gherkin scenarios, and how to tag them |
| [tech-debt.md](tech-debt.md) | Known problems, scores and the fix order |
| [runbooks/mod-export.md](runbooks/mod-export.md) | How to export mod data from a server container |
| [glossary.md](glossary.md) | The terms of the project |
| [security/audit-2026-10-21.md](security/audit-2026-10-21.md) | The security audit of the Api: findings, severity and fixes |
| [load/capacity-2026-10-21.html](load/capacity-2026-10-21.html) | How much load the stack holds: the curve, the CPU, the pool, and what breaks first (ADR-0039) |
| [research/document-store.md](research/document-store.md) | Whether DocumentDB and a `bson` column belong in our PostgreSQL, what they would cost, and what we do instead |
| [research/storage-engine.md](research/storage-engine.md) | PostgreSQL against CouchDB and Cassandra on the same 132 MB file: storage, read time, the filters each one refuses, and why the disk really grows (TD-71) |

## Reading order

- **New agent or developer:** `../AGENTS.md`, then the overview, the ADR index, the specs and the tech-debt register.
- **Maintainer:** the root `README.md` for setup and operations, then the overview.
- **Reviewer of a decision:** the ADR, then its Gherkin scenarios, then the C4 elements it names.

## C4 diagrams

The C4 model has four levels. This project uses three of them, plus dynamic and deployment views.

| View | C4 level | File |
|------|----------|------|
| `index` | 1. System context | `architecture/c4/views.c4` |
| `containers` | 2. Containers | `architecture/c4/views.c4` |
| `apiComponents`, `archiverComponents` | 3. Components | `architecture/c4/views.c4` |
| `archiveRun`, `queryCacheMiss`, `queryCacheHit` | Dynamic | `architecture/c4/views.c4` |
| `production` | Deployment | `architecture/c4/views.c4` and `deployment.c4` |

Level 4 (code) is not drawn. The code is the model.

Preview the diagrams with the LikeC4 tool:

```bash
npx likec4 start docs/architecture/c4      # live preview in the browser
npx likec4 validate docs/architecture/c4   # check the model
sh docs/architecture/generate-diagrams.sh  # rewrite architecture/diagrams.md
```

Learn more: [Gherkin reference](https://cucumber.io/docs/gherkin/reference), [C4 diagrams](https://c4model.com/diagrams), [LikeC4](https://likec4.dev).
