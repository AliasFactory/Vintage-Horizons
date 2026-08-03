# Release procedure

Followed in order, every release. Nothing here should be a surprise the first time you
hit it partway through - read it once before starting.

## 1. Verify the tree

```sh
git status                # clean, nothing stray staged
scripts/check.sh           # all three tiers; ~25 min
```

Do this before touching version numbers. A release built on a red check is not a release,
it is a bet.

## 2. Decide the version

This project has used semver-shaped bumps informally. 0.1.0 -> 0.1.1 was fixes only,
with no new capability. A release that adds a feature (server assist, savegame sweeping)
bumps the minor version instead. There is no 1.0.0 significance reserved yet - keep
doing what the history already does.

## 3. Update CHANGELOG.md

Move everything under `## [Unreleased]` to a new `## [X.Y.Z] - YYYY-MM-DD` heading below
it. Then leave `## [Unreleased]` empty above for whatever lands next. Write in the same
voice as the rest of the file and the tag messages below. Say what changed and why it
matters, not a bullet dump of commit subjects. Sometimes `[Unreleased]` is thin because
changes landed without notes. That is the moment to read `git log vX.Y.Z..HEAD` and
write the entry properly, rather than ship it thin.

## 4. Update the description

Two places carry a description of what the mod does, and neither updates itself:

- **`VintageHorizons/modinfo.json`**, the `"description"` field - shown in-game on the
  mod list and read by ModDB's own listing.
- **The ModDB page itself** (mods.vintagestory.at/vintagehorizons) - a separate, manual
  edit; there is no API or script for this repo to reach it.

Re-read the current text against what the release actually does before assuming it still
holds. It has gone stale before. The description said "fully client-side" from 0.1.0 on,
which stopped being the whole picture when server-assist shipped. Nothing caught that
automatically, because nothing checks prose against capability.

## 5. Bump the version

Both of these must carry the exact same version string, and a fast-tier check
(`StaticAssetChecks`) fails the build if they disagree:

- `VintageHorizons/modinfo.json` - `"version"`
- `VintageHorizons/VintageHorizons.csproj` - `<Version>`

## 6. Re-run the fast tier

```sh
scripts/check.sh fast
```

Confirms the version-string change did not break the one check that reads it, and costs
seconds. No need to repeat smoke/matrix for a version-only change.

## 7. Build and package

```sh
scripts/package.sh
```

Produces `dist/vintagehorizons_X.Y.Z.zip` from a Release build. Spot-check the zip before
trusting it - `unzip -l dist/vintagehorizons_X.Y.Z.zip`:

- `LICENSE` is present (0.1.0 shipped without it - this is the regression check for that).
- No `.pdb` file.
- `modinfo.json` inside the zip reports the new version.

## 8. Smoke-test the actual zip

Nothing in `scripts/check.sh` ever runs this file. `deploy-sandbox.sh` always builds and
deploys the **Debug** configuration, so the Release build that ships has no automated
coverage of its own. Unzip it into a scratch mods folder, not the dev symlink. Launch
it against a vanilla server at least once before publishing:

```sh
mkdir -p /tmp/vh-release-check && cd /tmp/vh-release-check
unzip -o ~/Projects/VintageHorizons/dist/vintagehorizons_X.Y.Z.zip -d vintagehorizons
# point a throwaway client's addModPath here and confirm it loads and captures
```

## 9. Commit and tag

One commit. Write the message in the same voice as `CHANGELOG.md` and the prior release
commits (`git show v0.1.1`, `git show v0.1.0` for reference). Say what is in the release
and why it is shaped this way, not a changelog copy-paste:

```sh
git add VintageHorizons/modinfo.json VintageHorizons/VintageHorizons.csproj CHANGELOG.md
git commit -m "Release X.Y.Z"
git tag -a vX.Y.Z -m "Vintage Horizons X.Y.Z"
```

## 10. Push

```sh
git push
git push --tags
```

Confirm with whoever is driving before this step, unless it is already understood to be
authorized. This is the point where the release becomes visible to anyone watching the
repo.

## 11. Publish

Manual, on mods.vintagestory.at:

- Upload `dist/vintagehorizons_X.Y.Z.zip`.
- Paste the new `CHANGELOG.md` entry into the version's changelog field.
- Update the page description if step 4 changed it.

## Not doing

No automated publish to ModDB. It has no API for this, and an upload to a public listing
should stay a deliberate human action regardless. No `-dev`/prerelease version suffixes,
because this project has not used them and there is no need invented here.
