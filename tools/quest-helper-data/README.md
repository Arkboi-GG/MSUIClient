# Native Quest Helper data generator

This tool converts the Vanilla location tables from the official pfQuest repository into the
small, indexed binary resource used by MSUIClient. The client never loads or executes Lua.

The currently committed bundle was generated from pfQuest commit
`104f35678ca39ab1fb78b655f815cc7016f5e0c8`:

```powershell
dotnet run --project tools\quest-helper-data\quest-helper-data.csproj -- `
  C:\path\to\pfQuest MSUIClient\Assets\QuestHelperData.bin.gz
```

Only quest objective units/objects, sources of quest items, quest turn-in relations, and their
zone-relative spawn coordinates are retained. Names and live progress still come from the server
and the client's normal query caches. When the authoritative realm database gains custom quest
content, add an importer for that source rather than hand-editing the generated binary.
