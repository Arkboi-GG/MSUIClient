# Gameplay interaction discovery

Run from the repository, with local `GameData/Data` available:

```powershell
dotnet run --project tools/gameplay-interaction-audit
```

An optional positional argument chooses the JSON output path. Default:
`docs/current/gameplay-interaction-audit.json` (ignored, regenerated locally).
The maintained findings and reproduction checklist live in
`shared_docs/GAMEPLAY_INTERACTION_CHECKLIST.md`.

The tool uses the shipping `MpqMount` (including numeric and locale patch priority)
and `SoundEntriesCatalog`. It does not launch the game, open an audio device, connect
to the server, or change gameplay state. It scans mounted FrameXML Lua/XML for:

- Literal `PlaySound` calls, with archive supplier and line locations.
- Pickup/place/cursor calls and selected pressed, checked, disabled, hover and
  cooldown hooks, to seed interaction and visual reviews.
- The four native spell/ability icon sound kits and every item-material gesture
  from `ItemGroupSounds.dbc`, including explicitly silent entries.
- Matching quoted sound names in client C# files (including comments and Dev code).
- The kit id, volume, resolved variant paths, archive suppliers and readable byte sizes.

The JSON contains stable, sorted cue records and reference sites. Compare reports
after an asset or source update to find new leads. The tool does not rewrite manual
statuses, extract proprietary source into tracked files, or declare features working.

Limits: regex is discovery, not Lua execution. Comments, templates and unused scripts
may be included; computed strings, numeric sound ids, native APIs and inherited
behavior require manual tracing. A missing literal match is not a bug. A readable
WAV is not proof of decoding, audible playback, correct timing or correct event routing.
Review the handler and its callers before promoting a lead to a confirmed gap.
