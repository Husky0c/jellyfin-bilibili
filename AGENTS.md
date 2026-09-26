# Repository workflow

- After each update to this Jellyfin plugin, run relevant build/tests, commit the scoped changes, and push them to the GitHub `origin` remote. Report the commit and push result to the user. If pushing fails, say so explicitly; never imply the repository is synchronized.
- For versioned updates, build and test every supported Jellyfin ABI, then push the matching `v<version>` tag so CI can publish the ZIPs and checksums to GitHub Release. Verify the Release exists before reporting success.
- Keep credentials, Bilibili cookies, downloaded media, NAS data, and local build artifacts out of Git. Do not force-push or overwrite remote changes.
