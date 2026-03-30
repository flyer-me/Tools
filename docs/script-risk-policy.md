# Script Risk Policy

This repository contains script tools that may perform privileged or security-sensitive operations.

## Classification

- Low risk: read-only or non-privileged script behavior.
- Elevated risk: modifies system configuration, boot settings, or auth-related binaries.

## Validation behavior

- `scripts/validate-tools.ps1` performs:
  - `ps1` syntax parse checks.
  - basic `bat/cmd` executable-line checks.
  - warning output for risky command patterns.

## Maintainer requirements

- Every elevated-risk tool README must include a `Risk Notice` section.
- Run scripts only in authorized environments.
- Treat warnings from validator as manual review gates.
