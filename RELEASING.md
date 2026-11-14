# Releasing

1. Make sure `main` is green (the **CI** workflow builds every Jellyfin line and
   runs the tests).
2. Tag it and push the tag:

   ```sh
   git tag v0.1.0
   git push origin v0.1.0
   ```

3. The **Release** workflow tests, builds a zip for each Jellyfin line in
   [`lines.json`](lines.json) (each holds the plugin DLL, `meta.json` and the
   `LICENSE`), and creates a GitHub release with the zips and a
   `manifest.json`. It keeps the versions from earlier releases in that manifest.

To try the build without publishing, run **Actions → Release → Run workflow**; it
uploads the zips as a workflow artifact only.

## How installs find the plugin

Jellyfin users add this repository address under **Dashboard → Plugins →
Repositories**:

```
https://github.com/FreeStuffCrazy/jellyfin-plugin-freestuffcrazy/releases/latest/download/manifest.json
```

GitHub redirects `latest` to the newest non-pre-release release, whose
`manifest.json` lists every version. Nothing is committed to `main` on release.

## Versions and Jellyfin lines

Each release builds one zip per line in `lines.json`, versioned `A.B.C.N` where
`N` is the line's position (10.9 is `.0`, 10.10 is `.1`, 10.11 is `.2`, 12.4 is
`.3`). Jellyfin installs the highest version whose `targetAbi` it satisfies, so
newer lines must have higher versions:

- Add a new Jellyfin line by **appending** it to `lines.json`, never in the middle.
- `jellyfin` is the package version the plugin is compiled against; `targetAbi`
  is the lowest server version the zip is offered to.
- The project picks the .NET version from the Jellyfin version, so a new line
  may also need a case in the `.csproj`.
