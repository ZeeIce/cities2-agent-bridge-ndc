# Compatibility matrix

Recorded 2026-10-17 on the development machine. The installation type does not matter: point `-GamePath` (or `CIAB_GAME_PATH`) at the folder that contains `Cities2_Data`, or let `find-game.ps1` read it from the game's `Player.log`.

| Component | Version / ID | Install path type | Bridge use | Verified at |
|---|---|---|---|---|
| Cities: Skylines II | 1.6.2f1 (767.21d1) | Non-Steam (`C:\Games\Cities - Skylines II`) | Required | Compile + `verify-api.ps1` |
| Unity | 2022.3.71f1 | — | — | From `Player.log` |
| Traffic (krzychu124) | pdx_mods 80095 rev 29 | Paradox Mods | Optional, read-only adapter | Types reflected offline |
| Road Builder | pdx_mods 87190 rev 42 | Paradox Mods | Optional; configs read from `ModsData\RoadBuilder` | 51 configs parsed offline |
| C2VM Traffic Tool Essentials | pdx_mods 125342 rev 20 | Paradox Mods | Detected only | — |
| CS2-Mod-Suite | — | Not installed | None | — |
| .NET SDK | 8.0 / 9.0 | — | Build (Roslyn `csc`), tests with `-p:BridgeTestFramework=net9.0` | Build + tests |
| PowerShell | Windows PowerShell 5.1 | — | `build`, `verify-api`, `bridge.ps1` | Build + verify |
| PowerShell 7 (`pwsh`) | Not installed | — | Only `MailboxClientTests` | — |

Each integration is optional and can be disabled independently: if a mod is absent, or its types change, its adapter reports `readSupport: false` and lists the missing types, while every other command keeps working.
