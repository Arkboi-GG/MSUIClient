# MSUIClient

[Community Discord](https://discord.gg/AzCdnyPHPY) - Updates, questions, bugs, discussion, and development notes.

[YouTube](https://www.youtube.com/@Yafrovon) - Walkthroughs, feature demos, and progress updates.

MSUIClient is a standalone C# client for SuperUI-Core, a heavily modified 1.12.1 VMaNGOS fork. It can also connect to stock VMaNGOS for standard 1.12.1 play. It is not an addon, a wrapper around the original client, or just a replacement UI. It reads the user's own client data, renders the world itself through Silk.NET and OpenGL, speaks the 1.12.1 network protocol, and implements the game and interface directly.

I built it because MangosSuperUI and SuperUI-Core eventually reached a point where some of the things I wanted were not addon problems or server problems. I needed a client I could actually change.

It is playable, but it is not finished. It is esentially in an alpha stage, and has many rough edges. There are still rendering, animation, audio, collision, interface, gameplay, and protocol edge cases to find. This is also its own implementation. I am not going to claim perfect original-client parity where it does not exist.

<!-- SCREENSHOT PLACEHOLDER
Show current normal MMO gameplay in a recognizable Vanilla location, with the world, characters, equipment, and interface visible without debug clutter.
-->

## The Three Projects

MSUIClient is one part of the larger MangosSuperUI project:

- [MangosSuperUI](https://github.com/Yafrovon/MangosSuperUI) - Web operations, content authoring, bot management, diagnostics, generated patches, and the high-level C# bot brain.
- [SuperUI-Core](https://github.com/Yafrovon/SuperUI-Core) - A heavily modified 1.12.1 VMaNGOS fork that owns the authoritative world, persistent bots, gameplay hooks, and custom protocol.
- [MSUIClient](https://github.com/Yafrovon/MSUIClient) - The client where you enter that world, play it, edit it, possess characters, and direct a party.

They are separate programs with clear boundaries. You do not need all three for basic client development or normal VMaNGOS play. The deeper SuperUI features require compatible revisions of the projects involved, especially MSUIClient and SuperUI-Core when custom protocol messages change.

## What Works Today

- Login, realm and launch profiles, character selection, character creation, and world entry.
- Streamed Vanilla terrain, WMOs, doodads, characters, creatures, equipment, water, foliage, sky, lighting, weather, particles, collision, and spatial audio.
- Movement, camera control, targeting, combat, spells, auras, action bars, cooldowns, loot, quests, a live Quest Helper, vendors, trainers, inventory, equipment, talents, maps, mail, auction, guild, group, and chat interfaces.
- Portals, instances, transport, mounts, fishing, game objects, and the normal world interaction loop.
- Creator Mode for offline character, equipment, spell, creature, world, collision, and visual-effect work.
- Live NPC development and structured handoff workflows with MangosSuperUI.
- The verified start of the CRPG layer, including possession, free view, party following, bot bags, and character sheets.

RTS-style selection, control groups, formations, and party commands belong to the developing CRPG experience inside the normal MMO world. The separate RTS World concept, including Commander, Honor, Heroes, territory, and victory systems, is future work. It is not currently a playable game mode.

## Build and Run

The current supported and tested target is Windows.

You need:

- The .NET 8 SDK.
- An OpenGL-capable GPU and current drivers.
- Your own compatible WoW 1.12.1 client data.
- A VMaNGOS realm for networked play. Creator Mode can run without one.

```powershell
Copy-Item MSUIClient\client-config.json.example MSUIClient\client-config.json
dotnet build MSUIClient.sln
dotnet run --project MSUIClient
```

Set `clientDataPath` in `MSUIClient/client-config.json` to the `Data` directory containing your MPQ archives. Set the realmd host and port for your realm. The in-client profile managers can then store and switch launch configurations and connections.

For stock VMaNGOS, set `server.realPortals` to `false`. Leave it enabled only for a compatible SuperUI-Core revision.

`dataServiceUrl` is optional for basic play. When it points to MangosSuperUI, MSUIClient pulls current realm-backed data such as the Quest Helper export instead of packaging a static database dump.

## Game Assets and Disclaimer

This project is not affiliated with or endorsed by Blizzard Entertainment.

World of Warcraft is a registered trademark of Blizzard Entertainment, Inc.

MSUIClient does not distribute Blizzard game assets. Users supply their own compatible client data, and the client reads required data directly from local MPQ archives.

Contributors must not submit proprietary client assets, private server data, credentials, generated local caches containing protected material, or other files they do not have permission to distribute.

MSUIClient is intended for educational, research, archival, interoperability, and private emulator-development use.

## License

MSUIClient is free software licensed under the [GNU General Public License, version 2 or later](LICENSE).

Third-party components and adapted source remain under their respective licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the complete attribution and redistribution notices. No Blizzard game assets are covered by the MSUIClient license or distributed by this repository.

## Contributing

Bug reports and contributions are welcome. The most useful reports include the failed workflow, relevant logs, a screenshot when the problem is visual, and the smallest repeatable case.

Read [CODE_STRUCTURE_LAW.md](CODE_STRUCTURE_LAW.md) before making structural changes. The repository is under active development, so do not assume an old plan or parity note is still a current requirement.

## Acknowledgments

This project builds on years of work by the VMaNGOS and broader MaNGOS communities, along with the people who documented the 1.12 protocol and the MPQ, BLP, DBC, ADT, M2, and WMO formats. [Benilla](https://github.com/samwhosung/benilla) is both an open-source Vanilla client reference and the origin of specifically identified protocol code ports. [StormLib](https://github.com/ladislav-zezula/StormLib) is the origin of specifically identified managed MPQ and PKWARE ports. Their license notices, along with those for the client's packaged dependencies, are preserved in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
