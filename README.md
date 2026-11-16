# FreeStuffCrazy Player Connector for Jellyfin

Watch your Jellyfin server in [FreeStuffCrazy Player](https://player.freestuffcrazy.com),
including a Tesla's browser, with no port forwarding, remote access or SSL. The
plugin makes one **outbound** connection from your server to the player's relay,
and viewers still sign in with their own Jellyfin accounts.

Viewers' traffic, including sign-ins and media, passes through the relay, which
can see it. See [Privacy and security](#privacy-and-security).

## Install

Requires Jellyfin 10.9 or newer.

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → +** and add:

   ```
   https://github.com/FreeStuffCrazy/jellyfin-plugin-freestuffcrazy/releases/latest/download/manifest.json
   ```

2. Open **Catalog**, install **FreeStuffCrazy Player Connector** (under General),
   and restart Jellyfin.
3. Open **Dashboard → Plugins → FreeStuffCrazy Player Connector**. When the status
   reads *Connected, waiting to be linked*, click **Link this server**, then
   **Link server** on the page that opens. To link from another device, go to
   <https://player.freestuffcrazy.com/link> and enter the code on the plugin page
   (codes expire after a few minutes; a new one appears automatically).
4. The plugin page now shows your **server name**. Give it to the people who use
   your server. In FreeStuffCrazy Player they choose **Sign in with Jellyfin** and
   enter that name.

Only a Jellyfin administrator can see the plugin page, so linking proves you own
the server. No FreeStuffCrazy account is needed.

To stop, click **Unlink this server** (viewers lose access, and linking again
gives a new name), or clear **Connect this server to FreeStuffCrazy Player** to
pause. The status line shows where things stand: *Turned off*, *Connecting*,
*Connected, waiting to be linked*, *Linked*, *Not connected*, or *This plugin needs
an update* (the relay no longer supports this version).

## Compatibility

Jellyfin 12.4 and newer (.NET 10), 10.11 (.NET 9), and 10.9 to 10.10 (.NET 8).
Each release has one zip per line, and Jellyfin picks the right one.

## Privacy and security

- **The relay sees your viewers' traffic.** Requests and responses pass through
  the relay, which ends the encrypted connection to your server. That includes
  sign-in requests, Jellyfin access tokens, and the media itself. Use this plugin
  only if you are comfortable with that.
- **What the plugin tells the relay:** your server's name and ID, its Jellyfin
  version, the plugin version, and the link credential once linked.
- **Jellyfin sees relayed requests as local,** so its remote-access setting and
  remote bitrate limit don't apply. The player limits each stream to about 2 Mbps
  on its side.
- **The plugin does not trust the relay.** It forwards only the endpoints the
  player uses (sign-in, browsing, playback, watch state), only to its own server,
  and never follows redirects. Everything else is refused, including Jellyfin's
  administration endpoints. See
  [`ForwardPolicy.cs`](src/Jellyfin.Plugin.FreeStuffCrazy/Relay/ForwardPolicy.cs)
  and [`PROTOCOL.md`](PROTOCOL.md). The link credential is stored in the plugin's
  configuration file, readable only by the Jellyfin service account, and the relay
  rotates it on every connection.
- To report a vulnerability, see [`SECURITY.md`](SECURITY.md).

## Building and development

You need the .NET SDK for the line you are building for. Installing a release from
the catalog is the supported route; to try your own build, copy
`Jellyfin.Plugin.FreeStuffCrazy.dll` into its own new folder in Jellyfin's plugins
directory and restart Jellyfin.

```sh
# Jellyfin 12.4 (the default)
dotnet build src/Jellyfin.Plugin.FreeStuffCrazy -c Release

# Another line: pass its Jellyfin version
dotnet build src/Jellyfin.Plugin.FreeStuffCrazy -c Release -p:JellyfinVersion=10.11.11

# Tests (need the .NET 10 SDK)
dotnet test tests/Jellyfin.Plugin.FreeStuffCrazy.Tests
```

To test against a relay on your own network, set the `FREESTUFFCRAZY_DEV_RELAY`
environment variable (or `DevRelayUrl` in the plugin's configuration file) to a
`ws://` address. It is honored only for this machine or private network addresses,
never for a public host.

## License

Copyright (C) 2026 FreeStuffCrazy Player contributors.

This plugin is licensed under the [GNU General Public License, version 3](LICENSE).
It is built against Jellyfin's own libraries (`Jellyfin.Controller` and
`Jellyfin.Model`), which are GPL-3.0, and runs inside the Jellyfin server. Each
release zip includes the licence text, and this repository is the corresponding
source.
