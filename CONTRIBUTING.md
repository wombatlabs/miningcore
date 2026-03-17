# Contributing to Miningcore

Miningcore is a long-term technical continuation of Miningcore, with major
refactors, new live metrics pipelines, job manager improvements and modern APIs.

Contributions are welcome — but must follow the rules below to ensure stability,
code quality and long-term maintainability.

---

## Branch Model

Miningcore uses a **two-branch model**:

- **`production`** → stable, reviewed, tagged releases  
  *Never commit directly here.*

- **`dev`** → active development  
  All PRs must target this branch.

Fork the repo, create a feature branch, and open a PR into `dev`.

Recommended naming:

feature/live-engine-improvements
fix/kawpow-nullref
refactor/equihash-job-cleanup
docs/update-readme


---

## Code Requirements

### 1. Code Quality

All contributions must:

- compile successfully (`dotnet build`)
- pass basic runtime smoke tests
- avoid adding new warnings
- follow the existing C# style conventions
- include comments in **English only**

### 2. Performance Matters

Miningcore's hot paths include:

- Stratum message handling
- JobManager pipelines
- ShareRecorder
- LiveHashrateEngine ring updates

Do **not** introduce unnecessary allocations, locks, or per-share overhead.

If you touch hot paths, include:

- a brief explanation of the performance impact
- reasoning for any structural changes

### 3. Breaking Changes

Changes to:

- Stratum protocol behavior  
- job templates  
- DB schema  
- configuration format  
- API responses  

…must be justified and discussed in the PR.

No breaking changes merged without review.

### 4. Tests (Recommended)

If you submit logic-heavy changes (VarDiff, hashing, share validation, job builder),
include at least minimal tests, even if simple console tests.

A full automated suite is planned (see ROADMAP), but not yet present.

---

## Live API Contributions

If your PR touches the **Live API**, ensure:

- LITE endpoints remain **zero DB**
- HEAVY endpoints clearly documented
- `windowSec` handling stays consistent across endpoints
- output shape stays stable unless justified

Update `live-api.md` if new endpoints are added.

---

## Native Code Changes

If your PR touches:

- `libkawpow`
- `libmerakipow`
- Equihash solvers
- RandomX flags

You MUST:

- rebuild the native libs  
- test on Linux  
- document the change  

Do not commit broken `.so` files.

---

## Coin Integrations

When adding or fixing a coin:

- update `coins.json`
- provide a minimal config example
- ensure job manager path works (Equihash/ProgPow/etc.)
- confirm payout handler correctness
- update README supported-coins link

---

## Documentation

If you add a feature:

- update README if relevant  
- update `live-api.md` for new endpoints  
- add/update architecture docs if structural  

Good documentation is part of the PR.

---

## Pull Request Checklist

Before opening a PR:

- [ ] Code compiles  
- [ ] No unnecessary commits/squashed history  
- [ ] PR targets `dev`  
- [ ] Comments are in English  
- [ ] No secrets/keys in commits  
- [ ] LITE endpoints touch zero DB  
- [ ] README or docs updated if needed  
- [ ] Native libs rebuilt (if applicable)  

---

## Communication

Feature discussions and major proposals should be opened as GitHub issues or
GitHub Discussions. Small fixes can go directly to PR.

---

## License & Attribution

Miningcore is MIT licensed.  
Large portions derive from Miningcore, also MIT.

All contributions are treated as MIT-licensed.

By contributing, you agree your code is licensed under MIT.

---

Thank you for helping evolve Miningcore into the next generation mining pool engine.
