# Buzzcord: notes for Claude

- Two apps, one version number: Windows in `src/` (C#/WinForms, `build.ps1`), Mac in `mac/`
  (Swift, `mac/build.sh`). A feature added to one usually belongs in the other; `mac/PARITY.md`
  tracks the differences.
- **Releases carry both apps.** Never publish a release with only one platform's zip. Prepare it as
  a draft, attach both zips and checksums, and publish only when Marc says so. The full steps are
  in [RELEASING.md](RELEASING.md).
- Work on a branch and open a PR into `main`; don't push to `main` directly. The GitHub account is
  `mtits`.
