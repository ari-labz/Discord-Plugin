# ARI Discord plugin

Reads and replies to messages in Discord servers and DMs, and joins voice channels.

A plugin for [ARI](https://github.com/ari-labz/A.R.I). Install it from ARI's Plugins page (Browse), or download `discord.ari` from the latest release here and drop it onto ARI.

## Build it yourself
This repo builds against ARI's source, expected next to it (`../A.R.I/Server`), or pass `-p:AriHost=<path to A.R.I/Server>`.

```
python3 ../A.R.I-Plugins/tools/pack_ari.py --id discord --name Discord --description "…" --version 1.0.0 \
  --project ARI.Plugin.Discord.csproj --entry ARI.Plugin.Discord.dll --icon icon.png \
  --host ../A.R.I/Server/ARI.LLM/ARI.LLM.csproj --out discord.ari
```

## Release
Tag a version and push the tag; `.github/workflows/release.yml` builds `discord.ari` and attaches it to the release. Then add the new version to the catalog in [A.R.I-Plugins](https://github.com/ari-labz/A.R.I-Plugins).
