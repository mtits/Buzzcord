# Security

Buzzcord holds people's Discord sign-ins, so security reports are very welcome.

**Please don't open a public issue for a vulnerability.** Report it privately through
[GitHub's private vulnerability reporting](https://github.com/mtits/Buzzcord/security/advisories/new),
with steps to reproduce if you can. I'll reply as soon as I can and credit you in the fix unless you'd rather I didn't.

## What Buzzcord promises

- Sign-in tokens are stored only on the user's PC, encrypted with Windows DPAPI (`%APPDATA%\Buzzcord\accounts.dat`), and never logged.
- Buzzcord sends tokens only to Discord. It contacts no server of its own: no telemetry, analytics or update checks.
- Game detection only compares process names. Buzzcord never opens, reads or injects into other processes.
- Official builds are signed by **Marc Turtal** and published only on [GitHub Releases](https://github.com/mtits/Buzzcord/releases)
  and [app.marcturtal.com/buzzcord](https://app.marcturtal.com/buzzcord/). Each release lists the zip's SHA-256.

Anything that breaks one of these promises is a security bug.

## Fake copies

If you find a "Buzzcord" download anywhere else, or one that isn't signed by Marc Turtal, don't run it, and please report it.
