# ADR-NNNN: Short title in the form "Verb the thing"

**Status:** Proposed | Accepted | Deprecated | Superseded by ADR-NNNN
**Date:** YYYY-MM-DD
**Deciders:** Who decided (Accepted) or who must decide (Proposed)

## Context

What is the situation? Which forces and limits apply? Write facts, not opinions.

## Decision

State the decision in one or two sentences. Start with "We will".

## Options Considered

| Dimension        | Option A | Option B | Option C |
|------------------|----------|----------|----------|
| Complexity       |          |          |          |
| Cost             |          |          |          |
| Scalability      |          |          |          |
| Team familiarity |          |          |          |

State why each rejected option lost.

## Trade-off Analysis

Name the main trade-off. Explain why this decision wins for this project.

## Consequences

- **Easier:** what this decision makes simple.
- **Harder:** what this decision makes difficult.
- **Revisit when:** the event that makes this ADR worth reading again.

## Verification

Write the decision as Gherkin scenarios. A scenario states a fact that must stay true.
Use the same tags as `docs/specs`: `@unverified`, `@backlog`, `@decision-needed`.

```gherkin
Feature: <what this decision guarantees>

  Scenario: <one guaranteed fact>
    Given <starting state>
    When <action or inspection>
    Then <observable result>
```

## Action Items

1. [ ] First follow-up step.

## Links

- Specs, C4 elements and code paths that this ADR affects.
