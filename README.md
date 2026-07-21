# Rewind

![Rewind](Assets/readme/banner-960.png)

Rewind captures unhandled exceptions in an ASP.NET Core app — the HTTP request that
triggered them, the stack trace, and a **masked** copy of the request body/headers —
and packages each one into a downloadable `.rewind` file. A companion CLI can then
inspect that file or replay the exact request against your app running locally, so
you can reproduce a production bug and hit breakpoints in your IDE without ever
touching real user data.

```
Prod app throws  →  RewindExceptionMiddleware  →  masked snapshot  →  {id}.rewind file
                                                                          │
                                                           rewind CLI ───┘
                                                     inspect / replay / export-http
```

## How it works

| Project | Responsibility |
|---|---|
| `src/Rewind.Core` | Data models, the masking engine, and the ZIP archive reader/writer. No ASP.NET Core dependency. |
| `src/Rewind.AspNetCore` | The exception middleware, DI wiring (`AddRewind`/`UseRewind`), and the background capture pipeline. |
| `src/Rewind.Cli` | Standalone console tool (`rewind`) for inspecting and replaying `.rewind` files. |
| `samples/Rewind.SampleApi` | A minimal API wired up with Rewind, used for manual testing. |
| `tests/Rewind.Core.Tests` | xUnit tests for masking and archiving. |

**Capture flow, in order:**

1. `RewindExceptionMiddleware` buffers the incoming request body and calls the next
   middleware in the pipeline.
2. If an unhandled exception bubbles up, it builds a `RewindSnapshot` (method, path,
   query string, headers, raw body, and the exception + inner exceptions), enqueues
   it on an in-memory `System.Threading.Channels` channel, and **rethrows the
   original exception** — Rewind never swallows or alters your app's normal error
   handling.
3. A background hosted service (`RewindCaptureBackgroundService`) drains that
   channel off the request thread, masks sensitive data, and writes a `{id}.rewind`
   ZIP file to disk. Because this happens asynchronously, capturing a snapshot never
   adds latency to the failing request or its error response.

**Masking**, done by `JsonDataMasker` before anything is written to disk:
- Matches sensitive **keys** by name (`password`, `secret`, `token`, `ssn`, `cvv`,
  `creditcard`, `authorization`, `apikey` by default — configurable).
- Also scans every string **value** with regex, independent of key name, so a field
  like `taxId` still gets caught if it holds an SSN-shaped value.
- Replacements stay format-valid so JSON deserialization/validation won't break on
  replay:
  - Credit card → `4111-XXXX-XXXX-1111` (first 4 + last 4 digits kept, middle masked)
  - SSN → `XXX-XX-6789`
  - Email → `j***@example.com`
  - Password → fixed valid-looking fake
  - Other secrets/tokens → deterministic fake string, same length as the original
- Falls back to whole-text regex masking if a payload isn't valid JSON (e.g. a raw
  text or form-encoded body).
- Request headers (e.g. `Authorization`) are masked the same way.

**The `.rewind` file** is a ZIP containing:
- `manifest.json` — id, timestamp, environment, exception type/message
- `http_context.json` — method, path, query string, masked headers, masked body
- `payload.json` — the masked request body on its own
- `stacktrace.txt` — full exception text, recursing through inner exceptions

## Requirements

- .NET 8 SDK

## Build & test

From the repo root:

```bash
# build everything
dotnet build Rewind.sln

# run the Rewind.Core test suite (masking + archive reader/writer)
dotnet test tests/Rewind.Core.Tests/Rewind.Core.Tests.csproj
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

There's no published NuGet package yet, so reference the project directly:

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
    options.Masking.EnableMasking = true;           // defaults to true
    options.Masking.SensitiveKeyWords.Add("iban");  // extend the default keyword list
});

var app = builder.Build();

app.UseRewind(); // register before your other exception-handling middleware runs the response,
                  // so Rewind still sees the original exception

app.Run();
```

`AddRewind` registers the archive writer, the capture channel, and the background
writer service as singletons. `UseRewind` adds `RewindExceptionMiddleware` to the
pipeline. Nothing else is required — snapshots start appearing in
`options.OutputDirectory` the first time an unhandled exception occurs.

## Using the CLI

Run it from source (rebuilds each time — fine for occasional use):

```bash
dotnet run --project src/Rewind.Cli -- <command> [args...]
```

Or build once and run the binary directly (faster for repeated use):

```bash
dotnet build src/Rewind.Cli
dotnet src/Rewind.Cli/bin/Debug/net8.0/Rewind.Cli.dll <command> [args...]
```

### `rewind inspect <file.rewind>`

Prints the manifest, request line, headers, masked payload, and stack trace. Read-only.

```bash
dotnet run --project src/Rewind.Cli -- inspect samples/Rewind.SampleApi/App_Data/rewind/<id>.rewind
```

### `rewind replay <file.rewind> [--target <baseUrl>]`

Rebuilds the exact HTTP request (method, path, query string, headers, masked body)
and sends it to `--target` (default `http://localhost:5000`). Prints the response
status and body — point it at your app running locally to reproduce the failure live.

```bash
dotnet run --project src/Rewind.Cli -- replay samples/Rewind.SampleApi/App_Data/rewind/<id>.rewind --target http://localhost:5057
```

### `rewind export-http <file.rewind> [--target <baseUrl>] [--output <path.http>]`

Writes a `.http` file with the request pre-filled (output defaults to `<file>.http`
next to the snapshot; target defaults to `http://localhost:5000`). Open it in
VS Code (REST Client extension) or Visual Studio 2022 17.8+ and click "Send Request".

```bash
dotnet run --project src/Rewind.Cli -- export-http samples/Rewind.SampleApi/App_Data/rewind/<id>.rewind \
  --target http://localhost:5057 \
  --output /tmp/replay.http
```

### `rewind help`

Prints command usage.

> **Note:** each `dotnet run --project ...` invocation triggers a fresh MSBuild. If
> you're issuing several CLI commands back-to-back against an app that's also
> running locally, prefer the built-binary form above so the two processes aren't
> competing for CPU.

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

## Known limitations

- No published NuGet package yet — integrate via project reference (see above).
- No automated tests yet for `Rewind.AspNetCore` (the middleware, capture channel,
  and background service) or for `Rewind.Cli` — only `Rewind.Core` has test coverage
  today.
- `replay` / `export-http` are tuned for JSON bodies; form-encoded and multipart
  bodies aren't specially handled.
- There's no hosted portal — snapshots are local files on the machine running the
  app; distributing them to developers is currently a manual step.
