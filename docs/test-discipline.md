# Test discipline

When extending test coverage, two non-negotiable rules apply:

- **Producer-first** - A mapping test must cite file + line + trigger of the real producer. If the producer cannot be located in `src/`, the test goes back to investigation before it is written.
- **Architecture-first** - Do not introduce a refactor solely to make a test reachable. If the test requires a new seam, decide the refactor explicitly as an architecture change on its own merits, not as a side effect of test coverage.
