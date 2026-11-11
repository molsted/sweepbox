# Security policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately: use [GitHub's private vulnerability reporting](https://github.com/molsted/sweepbox/security/advisories/new) for this repository.

Do **not** open a public issue for anything security-sensitive. Include steps to reproduce and affected versions where possible. We aim to respond within a week.

## What we consider a vulnerability

- Anything that lets an attacker run or inject code in Sweepbox, its builds, or its releases
- Weaknesses in the update chain (appcast/signature verification, release assets)
- Secrets exposure in workflows or history

## How releases are protected

- Every release artifact is built only by GitHub Actions from tagged commits on `main`
- Update downloads (appcasts and installers) are Ed25519-signed; the private key lives only in CI as the `SPARKLE_PRIVATE_KEY` secret and is never exposed to pull requests
- Third-party actions are pinned to commit SHAs; Dependabot tracks action and NuGet updates
