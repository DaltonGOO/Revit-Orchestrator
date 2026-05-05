# MCP Connections

Revit Orchestrator can talk to **other** MCP servers and use their tools
inside Revit. This page shows you how to add one in under a minute.

## Quickstart

1. Open Revit and click the **Orchestrator** panel.
2. Switch to the **Connections** tab and click **+ Add Connection**.
3. Pick a **Preset** at the top of the dialog. The fields below auto-fill.
4. Click **Save**.

That's it. The new tools show up in the **Tools** tab as
`mcp.{name}.{tool}`.

### Available presets

| Preset | What it does | Extra setup |
|---|---|---|
| **Filesystem** | Lets the LLM read/write files inside one folder. | Pick a folder when prompted. |
| **Fetch** | Lets the LLM fetch HTTP URLs. | None. |
| **Memory** | Scratch key/value store the LLM can use as a notepad. | None. |
| **Time** | Current time and timezone math. | None. |
| **Everything** | Reference test server — best smoke test. | None. |

All presets above run via `npx`, which means **you need [Node.js](https://nodejs.org)
installed**. The first connect downloads the server package on demand and
caches it.

> If you don't see a Node-based preset working, open a terminal and run
> `node --version`. If that fails, install Node.js LTS and try again.

### Try it now (recommended path)

1. **+ Add Connection** → preset **Everything** → **Save**.
2. Watch the row in the Connections list flip from `connecting` to
   `connected`. You should see a tool count appear.
3. Open the **Tools** tab. You'll see entries like `mcp.everything.echo`.
4. In chat, type *"call the echo tool with message 'hello'"* and watch
   the LLM invoke it.

If that round-trips, your setup is good. Now repeat with **Filesystem**
pointed at a folder of Revit exports, or **Fetch** for HTTP calls, etc.

---

## Manual configuration (Custom preset)

If a preset doesn't cover what you need — for example, a hosted MCP
server, an internal one, or one with auth — pick the **Custom** preset
and fill in the fields yourself.

### Fields

- **Name** — short identifier. Becomes the namespace in tool names
  (`mcp.{name}.{tool}`). Letters, digits, and underscores only.
- **Transport**
  - **STDIO** — a local command (e.g. `npx`, `python`).
  - **SSE** or **Streamable HTTP** — a hosted URL.
- **Command / Arguments** (STDIO) — the executable plus arguments.
  Quote paths with spaces (`"C:\Program Files\my-server"`).
- **URL** (SSE / HTTP) — full endpoint URL.
- **Auth Type** — `None`, `API Key (X-API-Key)`, `Bearer`, or
  `Custom Header` (format `HeaderName: value`).
- **Credential** — encrypted with Windows DPAPI before it leaves the
  dialog. Stored encrypted on disk.

### Storage

- Connection records: `src/mcp-server/data/connections.json`
- Lifecycle code: `src/mcp-server/orchestrator/connections/manager.py`

---

## Security notes

Adding an MCP connection lets the LLM call that server's tools on your
behalf. A few things to keep in mind:

- **Credentials are encrypted at rest** with DPAPI (per-Windows-user
  scope). They never appear in the Tools tab or audit log.
- **Tool names are namespaced** — an external server can't shadow a
  built-in `revit.*` tool.
- **Disable is a real off-switch** — disabling a connection unregisters
  its tools immediately.
- **The server process is not sandboxed.** STDIO presets run `npx`,
  which downloads and executes the package's code with your user's
  permissions. Stick to the official
  [`modelcontextprotocol/servers`](https://github.com/modelcontextprotocol/servers)
  packages unless you have a reason to trust a third-party one.
- **Pin filesystem servers to a narrow folder.** Never give the
  Filesystem preset the root of `C:\` or your home directory.
- **Use least-privilege credentials.** If a server takes an API token,
  scope it as narrowly as possible and rotate it when you stop using
  the server.

---

## Troubleshooting

**Status shows `error` and `last_error` mentions `npx` or `node`.**
Install [Node.js LTS](https://nodejs.org) and click **Test** on the
connection. The first `npx` invocation may take 10–30 seconds while it
downloads the server package.

**Filesystem preset works but tools fail with permission errors.**
The server only has access to the folder you picked. Move the file you
want it to read into that folder, or edit the connection and pick a
different folder.

**Editing a connection seems to lose its credential.**
The credential field is a password box, which can't be pre-populated.
If you edit a connection that uses authentication, **re-enter the
credential** before clicking Save — otherwise it's saved as empty.

**Two tools collided.**
Two connections registered a tool with the same name. Rename one of
them (the namespace `mcp.{name}.{tool}` must be unique).
