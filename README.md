# Rewind

![Rewind](Assets/readme/banner-960.png)

Rewind captures unhandled exceptions in an ASP.NET Core app — the HTTP request that
triggered them, the stack trace, and a **masked** copy of the request — and packages
each one into a `.rewind` file. The `rewind` CLI can then inspect that file, replay
the exact request against your app running locally, or turn it into a failing xUnit
test, so you can reproduce a production bug and hit breakpoints in your IDE without
ever touching real user data.

```
Prod app throws  →  RewindExceptionMiddleware  →  masked snapshot  →  {id}.rewind file
                                                                          │
                                                           rewind CLI ───┘
                                     list / inspect / replay / export-http / generate-test
```

## How it works

| Project | Responsibility |
|---|---|
| `src/Rewind.Core` | Data models, the masking engine, and the ZIP archive reader/writer. No ASP.NET Core dependency. |
| `src/Rewind.AspNetCore` | The exception middleware, DI wiring (`AddRewind`/`UseRewind`), and the background capture pipeline. |
| `src/Rewind.Cli` | The `rewind` command-line tool (also packaged as a `dotnet tool`). |
| `samples/Rewind.SampleApi` | A minimal API wired up with Rewind, used for manual testing. |
| `tests/Rewind.Core.Tests` | Masking and archive reader/writer tests. |
| `tests/Rewind.AspNetCore.Tests` | In-memory `TestServer` tests of the middleware, queue, rate limiter and retention. |
| `tests/Rewind.Cli.Tests` | Replay, `.http` export and test-generator tests. |

**Capture flow, in order:**

1. `RewindExceptionMiddleware` enables request-body buffering (only for text-like
   bodies under the size limit) and calls the next middleware. Nothing is read or
   copied on the happy path.
2. If an unhandled exception bubbles up, it checks your `ShouldCapture` filter and the
   per-exception rate limit, re-reads the buffered body, builds a `RewindSnapshot`
   (method, path, route pattern and values, query string, headers, body, exception
   chain, trace id) and puts it on a bounded in-memory queue. Then it **rethrows the
   original exception** — Rewind never swallows or alters your app's error handling,
   and if capturing itself fails, that failure is logged and ignored.
3. A background hosted service (`RewindCaptureBackgroundService`) drains the queue
   off the request thread, masks sensitive data, writes a `{id}.rewind` file, and
   deletes the oldest files beyond `MaxSnapshotFiles`. On shutdown it finishes
   writing whatever is still queued.

**Masking**, done by `JsonDataMasker` before anything is written to disk:
- Matches sensitive **keys** by name (`password`, `secret`, `token`, `ssn`, `cvv`,
  `cvc`, `creditcard`, `cardnumber`, `authorization`, `apikey`, `cookie`, `session`,
  `credential` by default — configurable). Matching ignores case, `-` and `_`, so
  `apikey` also catches `X-Api-Key` and `api_key`.
- Also scans every string **value** with regex, independent of key name, so a field
  like `taxId` still gets caught if it holds an SSN-shaped value.
- Replacements stay format-valid so deserialization/validation won't break on replay:
  - Credit card → `4111-XXXX-XXXX-1111` (first 4 + last 4 digits kept, middle masked)
  - SSN → `XXX-XX-6789`
  - Email → `j***@example.com`
  - Password → fixed valid-looking fake
  - CVV/CVC → same length, digits replaced (`123` → `111`)
  - `Authorization` → scheme kept, credential faked (`Bearer d4b2907e…`)
  - `Cookie` → cookie names kept, each value faked
  - Other secrets/tokens → deterministic fake string
  - Sensitive numeric JSON values stay numbers with the same digit count (`"cvv": 123` → `111`)
- What gets masked: headers, query-string parameters, route values and the matching
  path segments (`/reset/{token}`), JSON bodies, form-encoded bodies (by field name),
  other text bodies (by value pattern), and exception messages (by value pattern).

**The `.rewind` file** is a ZIP containing:
- `manifest.json` — format version, id, timestamp, environment, application name,
  trace id, exception type/message
- `http_context.json` — method, path, query string, route pattern/values, masked
  headers, masked body, and why the body was left out (if it was)
- `payload.json` — the masked request body on its own
- `stacktrace.txt` — full exception text, recursing through inner exceptions

## Requirements

- .NET 8 SDK (the packages also run on .NET 9 and later)

## Build & test

From the repo root:

```bash
dotnet build Rewind.sln
dotnet test Rewind.sln
```

## Try it end-to-end with the sample API

```bash
cd samples/Rewind.SampleApi
dotnet run
```

Note the port it prints (e.g. `http://localhost:5057`), then in another terminal:

```bash
curl -X POST http://localhost:5057/boom \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer supersecrettoken123" \
  -d '{"orderId":"ORD-42","cardNumber":"4111111111111111","password":"hunter2"}'
```

This deliberately throws, so you'll get a `500` back — that's expected. A `.rewind`
file will appear under `samples/Rewind.SampleApi/App_Data/rewind/`. You can also
trigger it from the browser via Swagger at `http://localhost:5057/swagger`.

## Integrating Rewind into your own project

Once the packages are published to NuGet:

```bash
dotnet add package Rewind.AspNetCore
```

Until then, reference the project directly:

```bash
dotnet add YourApi.csproj reference /path/to/rewind/src/Rewind.AspNetCore/Rewind.AspNetCore.csproj
```

Then in `Program.cs`:

```csharp
using Rewind.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRewind(options =>
{
    options.OutputDirectory = "/var/data/rewind";   // defaults to ./App_Data/rewind
    options.Masking.SensitiveKeyWords.Add("iban");  // extend the default keyword list
    options.ShouldCapture = (context, ex) => ex is not ValidationException; // skip expected errors
});

var app = builder.Build();

app.UseRewind(); // register before your other exception-handling middleware runs the response,
                  // so Rewind still sees the original exception

app.Run();
```

`AddRewind` registers the archive writer, the capture queue, the rate limiter and the
background writer service as singletons. `UseRewind` adds `RewindExceptionMiddleware`
to the pipeline. Nothing else is required — snapshots start appearing in
`options.OutputDirectory` the first time an unhandled exception occurs.

### Options

| Option | Default | What it does |
|---|---|---|
| `Enabled` | `true` | `false` turns the middleware into a pass-through. |
| `OutputDirectory` | `./App_Data/rewind` | Where `.rewind` files are written. |
| `Masking.EnableMasking` | `true` | Turn masking off (local debugging only). |
| `Masking.SensitiveKeyWords` | see above | Key-name fragments that are always masked. |
| `MaxBodySizeBytes` | `65536` | Larger bodies aren't captured; the snapshot records why. |
| `ShouldCapture` | `null` | `(HttpContext, Exception) => bool` filter. |
| `MaxSnapshotsPerMinutePerException` | `10` | Per exception type + throw site; `0` = unlimited. |
| `QueueCapacity` | `100` | Snapshots waiting to be written; extras are dropped (and logged), never blocking requests. |
| `MaxSnapshotFiles` | `500` | Oldest files beyond this are deleted; `0` = keep everything. |

Request bodies are only captured for text-like content types (JSON, XML, `text/*`,
form-encoded, GraphQL). Exceptions caused by the client disconnecting
(`OperationCanceledException` with `RequestAborted` signalled) are never captured.

## Using the CLI

Install it as a .NET tool once it's published:

```bash
dotnet tool install -g Rewind.Cli
rewind help
```

Or, from this repo, pack and install it locally:

```bash
dotnet pack src/Rewind.Cli -o artifacts
dotnet tool install -g Rewind.Cli --add-source ./artifacts
```

Or just run it from source: `dotnet run --project src/Rewind.Cli -- <command> [args...]`.

### `rewind list [directory]`

Lists the snapshots in a directory (default: the current one), newest first, with the
request and exception for each.

```bash
rewind list samples/Rewind.SampleApi/App_Data/rewind
```

### `rewind inspect <file.rewind>`

Prints the manifest, request line, route, headers, masked payload, and stack trace. Read-only.

### `rewind replay <file.rewind> [--target <baseUrl>]`

Rebuilds the exact HTTP request (method, path, query string, headers, masked body)
and sends it to `--target` (default `http://localhost:5000`). Prints the response
status and body — point it at your app running locally to reproduce the failure live.

```bash
rewind replay samples/Rewind.SampleApi/App_Data/rewind/<id>.rewind --target http://localhost:5057
```

### `rewind export-http <file.rewind> [--target <baseUrl>] [--output <path.http>]`

Writes a `.http` file with the request pre-filled (output defaults to `<file>.http`
next to the snapshot; target defaults to `http://localhost:5000`). Open it in
VS Code (REST Client extension) or Visual Studio 2022 17.8+ and click "Send Request".

### `rewind generate-test <file.rewind> [--output <path.cs>] [--namespace <ns>] [--entry-point <type>]`

Writes an xUnit test that sends the captured request through `WebApplicationFactory`
and asserts the response isn't a 5xx. It fails while the bug reproduces and passes
once it's fixed, so it doubles as a regression test — and you can debug it to step
through the original failure.

```bash
rewind generate-test samples/Rewind.SampleApi/App_Data/rewind/<id>.rewind \
  --namespace SampleApi.Tests --output tests/SampleApi.Tests/RewindReplayTests.cs
```

The test project needs `xunit` and `Microsoft.AspNetCore.Mvc.Testing`, plus a
reference to your API project. If the API uses top-level statements, add
`public partial class Program { }` to it so the test can see `Program` (the sample
API already does this).

### `rewind help`

Prints command usage.

## File icon for `.rewind` files

`Assets/icons/` includes a dedicated `.rewind` file glyph, and `Assets/file-association/`
has setup scripts for the three places you're likely to see the file listed. All are
optional and per-user — none require touching the repo itself.

### VS Code Explorer

**If you use [vscode-icons](https://marketplace.visualstudio.com/items?itemName=vscode-icons-team.vscode-icons):**

1. Install the vscode-icons extension if you don't already have it.
2. Copy the two settings from
   `Assets/file-association/vscode-icons/settings-snippet.json` into your
   `settings.json`, replacing `<absolute-path-to-repo>` with this repo's actual path.
3. Run **VSIcons: Regenerate Icons** from the Command Palette.

**If you use [Material Icon Theme](https://marketplace.visualstudio.com/items?itemName=PKief.material-icon-theme):**

Material Icon Theme's `files.associations` setting only remaps to icon names from
its own built-in set — it can't point at an arbitrary custom image. Instead:

```bash
cd Assets/file-association/vscode-material-icon-theme
./patch-material-icon-theme.sh
```

This drops the icon into the installed extension's own `icons/` folder and adds one
entry to its generated theme JSON (backing up the original first). Reload VS Code
(**Developer: Reload Window**) afterward. Run `restore-material-icon-theme.sh` to
revert. **Caveat:** a Material Icon Theme extension update will overwrite the patch
— just re-run the script to reapply it.

### Windows Explorer

```powershell
cd Assets\file-association\windows
.\register-rewind-icon.ps1
```

Registers `.rewind` under `HKEY_CURRENT_USER` with a `DefaultIcon` pointing at
`Assets\icons\rewind-file.ico` — no admin rights needed, and it doesn't change what
app opens the file, only the icon shown. Run `unregister-rewind-icon.ps1` to revert.

### macOS Finder

```bash
cd Assets/file-association/macos
./register-rewind-icon.sh
```

Builds a minimal helper app bundle (never launched) that declares ownership of the
`.rewind` type and its icon, and registers it with Launch Services. Run
`unregister-rewind-icon.sh` to revert. Must be run on macOS itself — it relies on
`sips` and `iconutil`.

### Linux (GNOME/Nautilus, KDE/Dolphin, etc.)

```bash
cd Assets/file-association/linux
./register-rewind-icon.sh
```

Registers `.rewind` as a freedesktop.org shared-mime-info type
(`application/x-rewind-snapshot`) and installs the icon into
`~/.local/share/icons/hicolor` at all standard sizes — the same mechanism every
Linux file manager uses to pick file icons. Per-user, no root needed. Run
`unregister-rewind-icon.sh` to revert.

## Releasing

`.github/workflows/ci.yml` builds, tests and packs `Rewind.Core`, `Rewind.AspNetCore`
and `Rewind.Cli` on every push and pull request (the `.nupkg` files are uploaded as a
build artifact). Pushing a tag like `v0.1.0` also publishes those packages to NuGet
with that version, using a `NUGET_API_KEY` repository secret.

## Known limitations

- Masking is pattern- and name-based. A sensitive value under an innocuous key name
  that doesn't look like a card, SSN or email (e.g. `"notes": "my PIN is 1234"`) isn't
  caught — add your own key names to `Masking.SensitiveKeyWords`.
- Multipart and binary bodies aren't captured (the snapshot notes that the body was
  left out), so replays of file uploads are sent without a body.
- Bodies sent with chunked encoding (no `Content-Length`) are buffered by ASP.NET Core
  in full while your app reads them (to a temp file once large), even though Rewind
  only keeps them if they fit within `MaxBodySizeBytes`.
- Snapshots are local files on the machine running the app. There's no hosted portal
  or upload to shared storage yet, so getting them to developers is a manual step.
