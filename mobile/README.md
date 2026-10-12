# MusicHoarder for iOS

The web app in a native shell: a [Capacitor](https://capacitorjs.com) web view that loads your
MusicHoarder instance, with a native audio player behind the transport.

It exists for one reason. An iOS web app (Safari, or the Home Screen app) cannot reliably start the
next song while the phone is locked: iOS gives a silent page seconds before it is suspended, and a
suspended page runs no script. Here the songs are played by an `AVQueuePlayer` that already holds
the tracks that follow, so the next one starts on the device whether the page is running or not.

Everything else is the web app as it is: same pages, same account, same session cookie.
Android has its own native client in [`android/`](../android) and does not use this.

## Layout

| Path | What |
| --- | --- |
| `capacitor.config.ts` | The shell's config. Reads `MH_SERVER_URL`. |
| `plugin/` | The native audio plugin (Swift, a local Swift package). |
| `xcode/` | The Xcode project. `xcode/App/App.xcodeproj` is what you open. |
| `www/` | The one bundled page, shown when the server cannot be reached. |

The web side of the plugin lives in the frontend: `frontend/src/lib/native/`. The two are one
protocol and change together.

## Building

You need a Mac with Xcode, [Bun](https://bun.sh), and an Apple ID (a free one runs the app on your
own phone for a week at a time; TestFlight needs the paid developer program).

```bash
cd ios
bun install
MH_SERVER_URL=https://music.example.com bun run sync   # your instance's public address
bun run open                                           # opens Xcode
```

In Xcode: select the **App** target → **Signing & Capabilities**, pick your team, and change the
bundle identifier to one of your own (`com.musichoarder.app` belongs to the project). Then run it
on your phone.

`MH_SERVER_URL` is the address your browser uses, with `https`: iOS refuses plain `http`. It is
written into generated, untracked files only, so run `bun run sync` again after a fresh clone, and
whenever the address or the plugin changes.

The server needs no configuration for the app. It has to run a MusicHoarder version that includes
`frontend/src/lib/native/`; an older one still loads, but plays through the web view like Safari.

## Signing in

- **Email link.** Asked for inside the app, the emailed link opens a page in Safari with an
  "Open the MusicHoarder app" button, which hands the sign-in over (`musichoarder://auth`).
- **Demo.** Works as on the web.
- **Passkeys do not work yet.** A web view only offers a site's passkeys to an app the site vouches
  for (an associated domain and an `apple-app-site-association` file naming your team id).

## How playback works

The web player (`player.svelte.ts`) is still the one that decides what plays. In a browser it
drives an `<audio>` element; in the shell it drives `NativeAudio`
(`frontend/src/lib/native/native-audio.ts`), an object with the same surface that forwards to the
plugin.

1. Loading a song sends one `loadTrack`: the stream URL, the lock screen's words for it, the start
   position, and whether to play.
2. After every queue change the page sends `setUpcoming`: up to 20 tracks that follow.
3. When a song ends the queue player moves to the next item by itself and reports the tracks it has
   started since the last load (its *trail*).
4. For each track on the trail the page has not taken yet, `NativeAudio` shows the store what it
   already reads as a song's end. The store advances as it always does and sets the `src` the device
   is already on, and that load is adopted instead of sent. So a page that slept through three
   songs wakes up, counts three listens, and shows the fourth.

Streams are authorised by the session cookie, which lives in the web view. The plugin copies the
cookies that match a URL's host onto its requests, and no others.

Lock screen, Control Center and headset commands go to the page first, since it knows what they
mean for the account's session ("Connect"). If the page does not answer within half a second, the
device carries the command out itself.

## Known limits

- Not yet run on a device by the project: the Swift compiles in CI (`.github/workflows/ios.yml`,
  a simulator build) and the web side is unit-tested against a stand-in for the plugin.
- Music videos and anything else the pages play through their own `<video>` or `<audio>` elements
  still go through the web view.
- Links that leave the server (Spotify's sign-in for the owner's sync, MusicBrainz) open in Safari.
- Radio top-ups are fetched by the page. If the page sleeps through the last queued track, the
  music stops there until the app is opened.
