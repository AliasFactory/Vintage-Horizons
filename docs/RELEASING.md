# Release procedure

Do these steps in order for each release. Read all of them one time before you start the
first release.

## 1. Examine the tree

```sh
git status                 # clean, with nothing staged by accident
scripts/check.sh           # all three tiers, approximately 25 minutes
```

Do this before you change the version numbers. If a check fails, do not continue.

## 2. Select the version

This project uses version numbers with the shape of semantic versioning. Version 0.1.0 to
0.1.1 contained corrections only, and no new function. A release that adds a function
increases the second number. One example is the server assist. No special meaning is given
to version 1.0.0 yet.

## 3. Update CHANGELOG.md

Move the text below `## [Unreleased]` into a new heading `## [X.Y.Z] - YYYY-MM-DD`. Put
the new heading below `## [Unreleased]`. Then leave `## [Unreleased]` empty for the next
changes.

Write in the same style as the remainder of the file. Give the changes and their effect on
the user. Do not copy the subject lines of the commits.

NOTE: The `[Unreleased]` section can be short, because changes are added to it after they
land. If it is short, read `git log vX.Y.Z..HEAD` and write the full entry now.

## 4. Update the description

Two locations hold a description of the mod. Neither location updates automatically.

- The field `"description"` in `VintageHorizons/modinfo.json`. The game shows this text in
  the mod list, and ModDB reads it for the listing.
- The ModDB page at mods.vintagestory.at/vintagehorizons. You must change this page by
  hand. This repository has no API or script that can change it.

Compare the current text with the functions of the release. Do not assume that the text is
still correct.

NOTE: This text became incorrect before. The description said "fully client-side" from
version 0.1.0. That became incomplete when the server assist was released. No check found
this, because no check compares the text with the functions of the mod.

## 5. Change the version number

Put the same version string in both of these locations:

- `VintageHorizons/modinfo.json`, the field `"version"`
- `VintageHorizons/VintageHorizons.csproj`, the element `<Version>`

If the two strings do not agree, the check `StaticAssetChecks` in the fast tier fails.

## 6. Run the fast tier again

```sh
scripts/check.sh fast
```

This shows that the new version string did not break the check that reads it. It takes a
few seconds. For a change of the version only, do not run the smoke tier or the matrix
tier again.

## 7. Build the package

```sh
scripts/package.sh
```

This makes `dist/vintagehorizons_X.Y.Z.zip` from a Release build. Then examine the
contents with `unzip -l dist/vintagehorizons_X.Y.Z.zip`, and make sure that:

- The file `LICENSE` is in the zip. Version 0.1.0 did not include it.
- No `.pdb` file is in the zip.
- The file `modinfo.json` in the zip gives the new version.

## 8. Test the zip file

Extract the zip into a temporary mods folder. Do not use the development symlink. Then
start a client with that folder and connect to a vanilla server one time.

```sh
mkdir -p /tmp/vh-release-check && cd /tmp/vh-release-check
unzip -o ~/Projects/VintageHorizons/dist/vintagehorizons_X.Y.Z.zip -d vintagehorizons
# give this path to a temporary client with addModPath, then make sure that
# the mod loads and captures terrain
```

NOTE: This step is necessary because no check runs the zip. The script
`deploy-sandbox.sh` always builds and installs the **Debug** configuration. Thus the
Release build that you send to users has no automatic test.

## 9. Commit and tag

Make one commit. Write the message in the same style as `CHANGELOG.md` and the earlier
release commits. For examples, read `git show v0.1.1` and `git show v0.1.0`. Give the
content of the release and the reason for its shape. Do not copy the changelog.

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

CAUTION: This step makes the release visible to all persons who watch the repository. If
you do not have permission to publish, get permission before this step.

## 11. Publish

Do these steps by hand on mods.vintagestory.at:

1. Upload `dist/vintagehorizons_X.Y.Z.zip`.
2. Copy the new entry from `CHANGELOG.md` into the changelog field of the version.
3. If step 4 changed the description, update the page description also.

## Not in this procedure

There is no automatic upload to ModDB. ModDB has no API for it. A person must also decide
to make a build public.

There are no version suffixes for development builds or prereleases. This project does not
use them.
