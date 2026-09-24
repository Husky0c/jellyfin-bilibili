# Repository workflow

- After each update to this Jellyfin plugin, run relevant build/tests, commit the scoped changes, and push them to the GitHub `origin` remote. Report the commit and push result to the user. If pushing fails, say so explicitly; never imply the repository is synchronized.
- Keep credentials, Bilibili cookies, downloaded media, NAS data, and local build artifacts out of Git. Do not force-push or overwrite remote changes.
