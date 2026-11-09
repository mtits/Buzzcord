# Releasing Buzzcord

Buzzcord has two apps, Windows (`src/`) and Mac (`mac/`), and **one version number**. Every public
release carries both apps, the way VS Code, Signal Desktop and OBS publish: one release page per
version, with every platform's download on it. GitHub's "latest release" then always has both.

## The rule

1. **Both apps share each version.** A release `vX.Y.Z` has exactly these four files:
   - `Buzzcord-X.Y.Z-windows.zip` and `Buzzcord-X.Y.Z-windows.zip.sha256`
   - `Buzzcord-X.Y.Z-mac.zip` and `Buzzcord-X.Y.Z-mac.zip.sha256`
2. **Nothing is published until both are attached.** Until then, the release stays a draft.
   Drafts are invisible to users and to both apps' updaters.
3. **When only one app changed, the other is still rebuilt at the new number.** Its notes say
   "No changes on Windows" (or Mac). A rebuild is quick and keeps the two in step.
4. **Merging to `main` is not releasing.** Feature branches and PRs merge as usual. Only
   publishing a release reaches users.
5. **Marc publishes.** Claude prepares the draft, shows Marc the notes, and publishes only when
   he says so.

## Steps

1. Do the work on branches with PRs into `main`, for whichever apps it touches. A feature that
   belongs in both apps (for example Block in 1.4.0) waits until both PRs are merged.
2. Set the version in both apps on `main`:
   - Windows: `[assembly: AssemblyVersion("X.Y.Z.0")]` in `src/Program.cs`
   - Mac: `VERSION=X.Y.Z` at the top of `mac/build.sh`
3. Tag `main` and push the tag:
   ```
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```
   A tag alone is not a release, so users see nothing yet. The push starts the Windows build in
   GitHub Actions (`.github/workflows/build.yml`).
4. Create the draft:
   ```
   gh release create vX.Y.Z --draft --verify-tag --title "Buzzcord X.Y.Z" --notes-file notes.md
   ```
5. Attach the Windows zip and its `.sha256`. Download the `Buzzcord-windows` artifact from that
   tag's Actions run (`gh run download`), then:
   ```
   gh release upload vX.Y.Z Buzzcord-X.Y.Z-windows.zip Buzzcord-X.Y.Z-windows.zip.sha256
   ```
6. Attach the Mac zip and its `.sha256`. On the Mac, check out the tag, run `./build.sh dist`
   in `mac/`, then upload the two files from `mac/dist/` the same way.
7. Check the draft:
   - all four files are attached;
   - each `.sha256` matches its downloaded zip;
   - each app's About box shows X.Y.Z.
8. Show Marc the notes. When he says so, publish:
   ```
   gh release edit vX.Y.Z --draft=false --latest
   ```
9. Update the website (app.marcturtal.com/buzzcord/). Start from a fresh download of the live
   page. Point both download buttons at X.Y.Z and add the changelog line.

Either machine can do steps 4 to 6 in any order. Whoever finishes first makes the draft, and the
other uploads to it.

## Release notes

Use one page with these sections:

- **New** / **Fixed**, each item marked (Windows), (Mac) or neither if it's in both apps.
- **Download**:
  - both zips with their size and SHA-256;
  - the "not code-signed yet" steps for each platform: Windows' More info > Run anyway, and the
    Mac's Open Anyway.
- The usual "Buzzcord is unofficial" warning.

## Updaters

Each app reads GitHub's release list and takes the newest release that has its own zip. This is
only a safety net for an old single-platform release. With this rule, the newest release always
has both.
