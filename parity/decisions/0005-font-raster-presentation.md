# 0005 — Preserve MSUI anti-aliased font rasterization

- Registry entry: ui/fonts
- Date: 2026-08-23
- Decided by: Nico

## Benilla behavior
Benilla reproduces the frozen monochrome raster treatment for NumberFontNormalSmall.

## MSUI behavior (preserved)
MSUI keeps anti-aliased glyph rasterization while retaining the authored font object, face, size, color, and THICK outline.

## Why
Nico explicitly placed the established MSUI font presentation on today's no-touch list.

## Boundaries
Font-object identity, measurement, wrapping, overflow, alignment, scale response, colors, outlines, and routing remain must-match. This decision covers raster treatment only.
