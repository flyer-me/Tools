# Script Risk Policy

Some tools in this repository modify the machine they run on. The point of this
policy is that "some tools are privileged" is not a disclaimer buried in a README —
it is a field in the manifest that the validator cross-checks against the code.

## Classification

Each `tool.manifest.json` declares `risk`:

- `low` — reads system state, or writes only inside its own directory. No admin
  rights, no registry writes, no boot configuration.
- `elevated` — needs administrator rights, writes `HKLM`, changes boot configuration
  (`bcdedit`), or replaces system binaries.

`risk` is independent of `licenseTier`. A free tool can still rewrite boot settings.

## What the validator does

`scripts/validate-tools.ps1`, per tool:

1. Parses the runtime entry if it is a `.ps1`.
2. Checks that a `.bat` / `.cmd` entry has at least one executable line.
3. Scans the runtime entry and every script under `scripts/` for privileged
   operations: `HKLM:` / `HKEY_LOCAL_MACHINE`, `bcdedit`, `reg add`, `reg delete`,
   `diskpart`, `utilman.exe`, `takeown`, `icacls`, `sc config`, `shutdown /r`.
4. Cross-checks the result against the declared risk:
   - `low` + a privileged operation → **error**. Either the code or the declaration
     is wrong; both need a human.
   - `elevated` + a privileged operation → reported as a count. This is the tool
     doing its job.
   - `elevated` without a `## Risk Notice` section in the tool README → **error**.

The pattern list is deliberately short. `format` and `Remove-Item` are not in it:
without context they match too much, and a check that cries wolf gets ignored. The
list is not a sandbox — it is a tripwire for the specific operations that make a tool
system-modifying.

## Maintainer requirements

- Every `elevated` tool must state, in its README `## Risk Notice`, what it changes,
  what is not reversible, and what the rollback is.
- Set `risk` by reading the scripts, not by reading the tool name.
- Run privileged tools only in environments you are authorized to change.
- A validator hit is a review gate, not a formality to be silenced by editing the
  pattern list.
