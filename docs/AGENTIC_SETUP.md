# Agentic setup

This repository is configured for AI coding agents and Meta Quest / Horizon OS development.

## Recommended VS Code / Cursor extension

Install the Meta Horizon extension:

https://marketplace.visualstudio.com/items?itemName=meta.meta-vr-dev

The repository also recommends this extension through `.vscode/extensions.json`.

## Meta Quest Agentic Tools

Install or inspect the Meta Quest Agentic Tools:

https://github.com/meta-quest/agentic-tools

## Generic MCP server

```sh
npx -y @meta-quest/hzdb mcp server
```

## Client setup

| Client         | Repo file                                                                        | Recommended setup                                                                        |
| -------------- | -------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| VS Code        | `.vscode/extensions.json`, `.vscode/mcp.json`, `.github/copilot-instructions.md` | Install `meta.meta-vr-dev`; optionally run `npx -y @meta-quest/hzdb mcp install vscode`  |
| Cursor         | `.vscode/extensions.json`, `.cursor/mcp.json`, `.cursor/rules/*`                 | Install `meta.meta-vr-dev`; optionally run `npx -y @meta-quest/hzdb mcp install cursor`  |
| Claude Code    | `.mcp.json`, `CLAUDE.md`                                                         | `/plugin marketplace add meta-quest/agentic-tools`; `/plugin install meta-vr@meta-quest` |
| Gemini CLI     | `GEMINI.md`                                                                      | `gemini extensions install https://github.com/meta-quest/agentic-tools`                  |
| GitHub Copilot | `.github/copilot-instructions.md`, `.github/instructions/*`, `.github/prompts/*` | Use the repository instructions and install the recommended VS Code extension            |
| Cline          | `.clinerules/*`, `AGENTS.md`                                                     | Use `AGENTS.md`; configure MCP if supported                                              |
| Roo Code       | `.roo/rules/*`, `AGENTS.md`                                                      | Use `AGENTS.md`; configure MCP if supported                                              |
| Windsurf       | `.windsurfrules`                                                                 | `npx -y @meta-quest/hzdb mcp install windsurf`                                           |
| OpenCode       | `opencode.jsonc`, `.opencode/*`                                                  | `npx -y @meta-quest/hzdb mcp install open-code`                                          |
| Codex          | `AGENTS.md`                                                                      | `npx -y @meta-quest/hzdb mcp install codex`                                              |

## Suggested first prompt

```text
Read AGENTS.md, detect what type of Meta Quest sample this is, enable the hzdb MCP server if available, and explain how to build, run, and debug this sample on a Quest device.
```
