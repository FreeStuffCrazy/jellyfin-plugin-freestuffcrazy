# FreeStuffCrazy Player Connector for Jellyfin

A Jellyfin plugin that lets people watch your server from
[FreeStuffCrazy Player](https://player.freestuffcrazy.com), including from the tesla car
browser, without port forwarding or remote access.

The plugin keeps one **outbound** connection from your server to the player's
relay. Requests from signed-in viewers travel back over that connection, and the
plugin forwards them to your own Jellyfin server and streams the answers back.
The plugin needs no open port or remote-access setup, and viewers still sign in
with their own Jellyfin accounts.

## Using it

1. Install the plugin (see **Installing**) and restart Jellyfin.
2. Open **Dashboard → Plugins → FreeStuffCrazy Player Connector**. When the
   status reads *Connected, waiting to be linked*, click **Link this server**.
   It opens the linking page; click **Link server** there. (To link from another
   device, go to <https://player.freestuffcrazy.com/link> and enter the code the
   plugin page shows. Codes expire after a few minutes and a new one appears
   automatically.)
3. No FreeStuffCrazy Player account is needed. Only a Jellyfin administrator can
   see the plugin page, so opening its link is the proof that you own the server.
4. The plugin page now shows your **server name**. Share it with the people who
   use your server. In FreeStuffCrazy Player they choose **Sign in with
   Jellyfin** and enter that name.

**Unlink this server** disconnects it. Viewers lose access until you link it
again, and the server gets a new name. To pause without unlinking, clear
**Connect this server to FreeStuffCrazy Player**.

The status line on the plugin page tells you where things stand: *Turned off*,
*Connecting*, *Connected, waiting to be linked*, *Linked*, *Not connected*, or
*This plugin needs an update* (the relay no longer supports this plugin version).

## Installing

Once a release is published, add this repository in Jellyfin under **Dashboard →
Plugins → Repositories → +**:

```
https://github.com/FreeStuffCrazy/jellyfin-plugin-freestuffcrazy/releases/latest/download/manifest.json
```

Then open **Catalog**, find **FreeStuffCrazy Player Connector** under General,
install it, and restart Jellyfin. Or build it yourself (see **Building**).
Each release has one zip per Jellyfin line; Jellyfin picks the right one for your
server.

## Compatibility

Each Jellyfin release line runs on its own .NET version, so releases contain a
build per line:

| Jellyfin | .NET |
|---|---|
| 12.4 and newer | net10.0 |
| 10.11.x | net9.0 |
| 10.9.x and 10.10.x | net8.0 |

## Building

You need the .NET SDK for the line you are building for.

```sh
# Jellyfin 12.4 (the default)
dotnet build src/Jellyfin.Plugin.FreeStuffCrazy -c Release

# Another line: pass its Jellyfin version
dotnet build src/Jellyfin.Plugin.FreeStuffCrazy -c Release -p:JellyfinVersion=10.11.11
```

To try your own build, copy `Jellyfin.Plugin.FreeStuffCrazy.dll` into its own new
folder inside your Jellyfin plugins directory and restart Jellyfin. Installing a
release from the catalog is the supported route.

The tests need the .NET 10 SDK:

```sh
dotnet test tests/Jellyfin.Plugin.FreeStuffCrazy.Tests
```

## What it can and can't do

The plugin does not trust the relay. It forwards only the Jellyfin endpoints the
player uses (sign-in, browsing, playback, and watch state), only to its own
server, and never follows redirects. Everything else is refused, including
Jellyfin's administration endpoints. The exact list is in
[`ForwardPolicy.cs`](src/Jellyfin.Plugin.FreeStuffCrazy/Relay/ForwardPolicy.cs)
and the wire protocol is documented in [`PROTOCOL.md`](PROTOCOL.md).

The link credential is stored in the plugin's configuration file, which only the
Jellyfin service account can read, and the relay rotates it on every connection.

## Privacy and security

- **The relay sees your viewers' traffic.** Viewers' requests and your server's
  responses pass through FreeStuffCrazy Player's relay, which ends the encrypted
  connection to your server. That includes sign-in requests, Jellyfin access
  tokens, and the media itself. Use this plugin only if you are comfortable with
  that.
- **What the plugin tells the relay about your server:** its name and ID, its
  Jellyfin version, and the plugin version, plus the link credential once
  linked.
- **Jellyfin sees relayed requests as local.** They reach Jellyfin from the same
  machine, so Jellyfin treats viewers as local: its remote-access setting and
  remote streaming bitrate limit don't apply to them. FreeStuffCrazy Player
  limits each of these streams to about 2 Mbps on its side. This is also why only
  a fixed list of endpoints is forwarded; see [`PROTOCOL.md`](PROTOCOL.md).
- To report a vulnerability, see [`SECURITY.md`](SECURITY.md).

## Development

For testing against a relay on your own network, set the
`FREESTUFFCRAZY_DEV_RELAY` environment variable (or `DevRelayUrl` in the plugin's
configuration file) to a `ws://` address. It is honored only for this machine or
private network addresses, never for a public host.

## License

[MIT](LICENSE)
