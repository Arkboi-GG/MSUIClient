#!/usr/bin/env python3
"""Regenerate MSUIClient/Data/vmangos-commands.tsv from the Core's Chat.cpp.

The Macro Book's command reference and linter read that TSV (embedded at build
time). Run this whenever the Core's command table changes:

    ssh 192.168.0.2 'python3 -' < tools/macro-commands/export-commands.py \
        > MSUIClient/Data/vmangos-commands.tsv

Columns: name (full dotted path without the leading dot), security (SEC_*),
runnable (1 = the node has its own handler), has_subcommands (1 = a table hangs
off it). vmangos lists some groups twice (a bare handler row and a table row);
the client merges them by name.
"""
import os
import re

SOURCE = os.path.expanduser("~/vmangos/src/game/Chat/Chat.cpp")

src = open(SOURCE, encoding="utf-8", errors="replace").read()
tables = {}
for m in re.finditer(r"static ChatCommand (\w+)\[\]\s*=\s*\{(.*?)\n\s*\};", src, re.S):
    name, body = m.group(1), m.group(2)
    entries = []
    for e in re.finditer(
        r"\{\s*\"([^\"]*)\"\s*,\s*(SEC_\w+)\s*,\s*(true|false)\s*,\s*([^,]+),"
        r"\s*\"((?:[^\"\\]|\\.)*)\"\s*,\s*(\w+)\s*\}", body):
        entries.append((e.group(1), e.group(2), e.group(4).strip(), e.group(6)))
    tables[name] = entries


def walk(table, prefix, out):
    for n, sec, handler, child in tables.get(table, []):
        full = (prefix + " " + n).strip()
        out.append((full, sec,
                    "1" if handler not in ("nullptr", "NULL") else "0",
                    "1" if child != "nullptr" else "0"))
        if child != "nullptr":
            walk(child, full, out)


rows = []
walk("commandTable", "", rows)
print("name\tsecurity\trunnable\thas_subcommands")
for r in rows:
    print("\t".join(r))
