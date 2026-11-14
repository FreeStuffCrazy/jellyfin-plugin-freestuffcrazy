# Connector protocol, version 1

The FreeStuffCrazy Player connector (this Jellyfin plugin) keeps one outbound
WebSocket open to the relay. The relay sends it HTTP requests from signed-in
FreeStuffCrazy Player viewers; the connector forwards each one to its own
Jellyfin server and streams the response back. Many requests share the one
socket as numbered streams.

The connector never trusts the relay. It forwards only the Jellyfin
endpoints the player uses (see "What the connector forwards"), only to its
own server, and never follows redirects.

## Connection

- URL: `wss://relay.freestuffcrazy.com/connect`, subprotocol `fsc-relay.1`.
- Every WebSocket message is binary and holds exactly one frame.
- The relay sends `ping` at least every 20 seconds. The connector closes the
  socket if it hears nothing for 60 seconds.
- After a disconnect the connector waits a random time between 0 and the
  current backoff (starting at 1 second, doubling to at most 60 seconds)
  before reconnecting. A connection that stayed up for 60 seconds resets the
  backoff.

## Frames

```
+--------+----------------------+-------------------+
| type   | stream ID            | payload           |
| 1 byte | 4 bytes, big-endian  | rest of message   |
+--------+----------------------+-------------------+
```

| Type | Name | Direction | Stream | Payload |
| ---: | --- | --- | --- | --- |
| 0 | `CONTROL` | both | 0 | UTF-8 JSON control message |
| 1 | `OPEN` | relay → connector | new ID | JSON request: `{"method","path","headers":[[name,value],...],"window"}` |
| 2 | `HEADERS` | connector → relay | stream | JSON response: `{"status","headers":[[name,value],...]}` |
| 3 | `DATA` | both | stream | body bytes, at most 65,536 per frame |
| 4 | `END` | both | stream | empty: the sender's body is complete |
| 5 | `WINDOW` | relay → connector | stream | 4-byte big-endian count of more response bytes the connector may send |
| 6 | `CANCEL` | both | stream | empty: abandon the stream |
| 7 | `ERROR` | connector → relay | stream | JSON `{"status","message"}`, sent instead of `HEADERS` when the request is refused or the server can't be reached |

Stream IDs are chosen by the relay, start at 1, and are never reused on a
connection.

### A request

1. The relay sends `OPEN`, then any request body as `DATA` frames, then
   `END`. Request bodies are at most 1 MiB; requests without a body send
   `END` straight after `OPEN`.
2. The connector answers with `HEADERS` (or `ERROR`), then the response
   body as `DATA` frames, then `END`.
3. Either side may send `CANCEL` at any time. The connector then aborts its
   request to Jellyfin; the relay ends the viewer's response.

### Flow control

`OPEN` carries the response window: how many body bytes the connector may
send before waiting. Each `WINDOW` frame adds to it. The relay grants more
only as the viewer reads, so a slow viewer slows the server instead of
filling the relay's memory. Request bodies are small and are not
flow-controlled.

## Control messages

Every control message is a JSON object with a `type`.

### Connector → relay

| `type` | Fields | Meaning |
| --- | --- | --- |
| `hello` | `protocol` (1), `plugin`, `jellyfin`, `serverId`, `serverName`, and `connectorId` + `secret` once linked | First message on every connection |
| `saved` | `connectorId` | The credential from `linked` or `rotate` is stored; the relay may forget the previous secret |
| `pong` | `t` | Reply to `ping`, echoing `t` |

### Relay → connector

| `type` | Fields | Meaning |
| --- | --- | --- |
| `pairing` | `code`, `url`, `expiresAt` | Not linked yet. An administrator opens `url` to link this server; only an administrator can see the link on the plugin page, so opening it is the proof. A new `pairing` replaces an expired one. The connector shows `url` only when it is an `https://player.freestuffcrazy.com` address |
| `linked` | `connectorId`, `secret`, `label` | Linking finished. Store the credential, then send `saved` |
| `ready` | `connectorId`, `label` | Credential accepted; requests may follow |
| `rotate` | `secret` | Replace the stored secret, then send `saved`. Sent after each `ready` |
| `ping` | `t` | Liveness check |
| `revoked` | `reason` | This server was unlinked. Delete the credential and reconnect without one to get a new `pairing` |
| `error` | `message` | Shown to the administrator on the plugin page |

### Credentials

The secret changes on every connection (`rotate`). The relay accepts the
previous secret only until it receives `saved` for the new one, so a
connector that crashes mid-save can still reconnect. If a secret the relay
already replaced is presented again, the credential has been copied: the
relay revokes it and the server must be linked again.

### Close codes

| Code | Meaning |
| ---: | --- |
| 4001 | Credential not accepted; the connector deletes it and reconnects unlinked |
| 4002 | Protocol version not supported; the connector stops and asks for an update |
| 4003 | Credential reused after rotation; revoked |

## Limits and errors

| Limit | Value | When exceeded |
| --- | --- | --- |
| WebSocket message | 256 KiB | The connection is closed |
| Concurrent streams | 32 | `ERROR` 503 for the new stream |
| Request body | 1 MiB | `ERROR` 413 |
| Request body completion | 30 seconds after `OPEN` | `ERROR` 408 |
| Jellyfin response headers | 60 seconds | `ERROR` 502 |

Other `ERROR` statuses: 403 for a request outside the list below, 400 for a
request that is malformed or can't be forwarded, and 502 when the Jellyfin
server can't be reached. A stream that fails after `HEADERS` was sent ends
with `CANCEL` instead.

## What the connector forwards

Relayed requests reach Jellyfin from the same machine, so Jellyfin treats
them as local network traffic. To keep that from exposing anything a remote
viewer couldn't otherwise reach, the connector forwards only:

| Method | Path |
| --- | --- |
| GET | `/System/Info/Public` |
| GET | `/QuickConnect/Enabled`, `/QuickConnect/Connect` |
| POST | `/QuickConnect/Initiate`, `/QuickConnect/Authorize` |
| POST | `/Users/AuthenticateByName`, `/Users/AuthenticateWithQuickConnect` |
| GET | `/Users/Me`, `/UserViews`, `/UserItems/Resume` |
| GET | `/Items`, `/Items/Latest`, `/Items/{id}` |
| POST | `/Items/{id}/PlaybackInfo` |
| GET | `/Items/{id}/Images/{Primary,Backdrop,Thumb}` (optionally `/{index}`) |
| GET | `/Shows/NextUp`, `/Shows/{id}/Seasons`, `/Shows/{id}/Episodes` |
| POST, DELETE | `/UserPlayedItems/{id}` |
| POST | `/Sessions/Playing`, `/Sessions/Playing/Progress`, `/Sessions/Playing/Stopped`, `/Sessions/Logout` |
| DELETE | `/Videos/ActiveEncodings` |
| GET | `/Videos/{id}/master.m3u8`, `/Videos/{id}/main.m3u8`, `/Videos/{id}/hls1/{playlist}/{n}.{ts,mp4,m4s}` |

`{id}` is a Jellyfin item ID (32 hex digits, with or without dashes). Paths
are matched case-insensitively and must not contain percent-encoding,
backslashes, `..` or `//`. A query string is passed through unchanged.
Anything else gets `ERROR` with status 403.

Only these request headers are forwarded: `Accept`, `Accept-Language`,
`Authorization`, `Content-Type`, `If-Modified-Since`, `If-None-Match`,
`Range`, `User-Agent`, `X-Emby-Authorization`. Only these response headers
are returned: `Accept-Ranges`, `Cache-Control`, `Content-Length`,
`Content-Range`, `Content-Type`, `ETag`, `Last-Modified`.
