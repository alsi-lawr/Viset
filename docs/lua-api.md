# Trusted Lua API

Viset capture files are trusted, unsandboxed Lua 5.2 programs. Standard Lua
libraries are open, local modules may be loaded from the capture-file directory,
and the API can start processes and make network requests. Viset does not expose
an implicit CLR bridge, but it is not a security boundary. Do not run untrusted
capture files.

Each expanded output runs in a fresh Lua state after Viset has validated every
declared output path and started an isolated browser session.

## Context

```lua
viset.api_version
viset.script.directory
viset.context.script_path
viset.context.output
viset.context.device
viset.context.axes
viset.context.data
```

`api_version` is `1`. Paths in `script` and `context` are absolute. The device
contains `name`, `mobile`, `touch`, `device_scale`, `viewport`, and an optional
`frame`. TOML matrix and data values become ordinary Lua scalars and tables.

Durations accepted by the API are positive numbers in milliseconds or strings
ending in `ms` or `s`, such as `250`, `"250ms"`, or `"1.5s"`.

## Page control

### `viset.page.navigate(url)`

Navigate to an absolute URL and wait for the CDP navigation command to finish.

### `viset.page.evaluate(script, arguments?)`

Evaluate JavaScript and return its JSON-shaped result as Lua values. Without
`arguments`, `script` is evaluated directly. With an argument table, `script`
must evaluate to a function; Viset calls it with the JSON-shaped array or object.
Argument tables cannot mix array and object entries, contain non-string object
keys, non-finite numbers, functions, or more than 64 nested tables.

```lua
local title = viset.page.evaluate("document.title")
local text = viset.page.evaluate(viset.javascript [=[
  ({ selector }) => document.querySelector(selector).textContent
]=], { selector = "h1" })
```

### `viset.page.wait_for(expression, timeout)`

Poll a JavaScript expression until it returns Boolean `true` or the timeout
expires.

### `viset.page.animate(options)`

Run a browser-side `requestAnimationFrame` animation. `options` contains:

- `duration`: a Viset duration;
- `update`: a synchronous JavaScript function receiving frozen
  `{ progress, linear_progress, elapsed_ms, duration_ms }` state;
- `easing`: optional `linear`, `in_sine`, `out_sine`, `in_out_sine`, or a custom
  JavaScript easing function.

The update function must not return a promise.

### `viset.javascript(source)`

Return a string unchanged as an identity marker. It does not execute or validate
JavaScript.

## PNG snapshots

For `.png` output, call `viset.snapshot()` exactly once:

```lua
viset.page.navigate("https://example.com")
viset.snapshot()
```

Calling it for WebP output or more than once is an error.

## WebP recording

For `.webp` output, create exactly one recording:

```lua
local recording = viset.record()

recording:start()
recording:during("500ms", function()
  viset.page.animate({
    duration = "400ms",
    easing = "in_out_sine",
    update = viset.javascript [=[
      ({ progress }) => {
        document.documentElement.scrollTop = progress * 600
      }
    ]=],
  })
end)
recording:stop()

viset.sleep("250ms")
recording:start()
recording:during("250ms")
recording:stop()
```

- `recording:start()` begins or resumes visible recording.
- `recording:stop()` pauses it. Repeated visible segments concatenate.
- `recording:during(duration, callback?)` requires an active recording, runs the
  optional callback, and waits long enough to guarantee the requested minimum
  visible duration. The callback must not stop the recording.
- `viset.sleep(duration)` waits without changing recording visibility.

## HTTP

```lua
local response = viset.http.get({
  url = "https://example.com/health",
  headers = { Accept = "text/plain" },
  timeout = "5s",
})

viset.http.wait({ url = "http://127.0.0.1:8080", timeout = "10s" })
```

`viset.http.get(options)` performs one GET and returns `status`, `headers`, and
`body`. `viset.http.wait(options)` retries until the endpoint returns a 2xx
response. Both default to a 30-second timeout.

## Processes

```lua
local handle = viset.process.start({
  file = "python3",
  arguments = { "-m", "http.server", "8080" },
  working_directory = viset.script.directory,
  environment = { MODE = "capture" },
})

local result = viset.process.stop(handle)
```

- `viset.process.start(options)` returns an integer handle.
- `viset.process.wait(handle, timeout?)` waits for normal exit and defaults to
  30 seconds.
- `viset.process.stop(handle)` kills the process tree when still active, waits,
  and returns `exit_code`, `stdout`, and `stderr`.

Viset attempts to stop every still-active managed process when a capture ends or
fails.

## Emulation

`viset.emulation.apply(device)` applies a device-shaped table containing a
viewport and optional `device_scale`, `mobile`, and `touch` values.
`viset.emulation.touch(x, y)` dispatches one touch at viewport coordinates.

The selected `viset.context.device` is already applied when the capture starts;
these functions are available for explicit changes during a script.
