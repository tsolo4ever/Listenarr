# Plan: Library Management Panel

## Problem

The current track/untrack (Monitored) system operates per-book. Since audiobooks exist in series,
users have to monitor/unmonitor each book individually. There is no way to say "follow this series"
and have all books in it monitored together.

The library view also has no sidebar — filtering and bulk actions are scattered across toolbar
dropdowns and modals.

---

## Goal

A collapsible left panel in the library view that surfaces series-aware library management:
- Browse library by Series or Author in the panel
- See series health at a glance (N books, N monitored, N missing)
- Monitor/unmonitor an entire series in one click
- Bulk actions scoped to a series without having to select books manually

---

## Current State

- `Monitored` — boolean field on `Audiobook`, controls automatic searching
- `Series` — plain string on `Audiobook`, no separate entity
- Series grouping exists in the grid view (Group By → Series) but has no management surface
- `BulkEditModal` exists and already supports setting monitored across selected IDs
- `CollectionView.vue` shows all books in a series when you click into one

---

## Proposed UI

```
┌──────────────────────┬────────────────────────────────────┐
│  PANEL               │  LIBRARY GRID (existing)           │
│                      │                                    │
│  [Series] [Authors]  │  ┌──────┐ ┌──────┐ ┌──────┐      │
│                      │  │      │ │      │ │      │      │
│  ▶ Stormlight (8)    │  └──────┘ └──────┘ └──────┘      │
│    ● 6 monitored     │                                    │
│    ○ 2 unmonitored   │  ┌──────┐ ┌──────┐ ┌──────┐      │
│    ! 1 missing       │  │      │ │      │ │      │      │
│    [Monitor All]     │  └──────┘ └──────┘ └──────┘      │
│                      │                                    │
│  ▶ Kingkiller (3)    │                                    │
│    ● 3 monitored     │                                    │
│    ! 2 missing       │                                    │
│    [Monitor All]     │                                    │
│                      │                                    │
│  ▶ Standalone (12)   │                                    │
│                      │                                    │
│  [← Collapse]        │                                    │
└──────────────────────┴────────────────────────────────────┘
```

- Clicking a series in the panel filters the grid to that series
- "Monitor All" / "Unmonitor All" uses the existing bulk-update API endpoint
- Standalone books (no series) grouped under a "Standalone" bucket
- Panel is collapsible — remembers state in localStorage

---

## What Needs Clarifying (open questions)

1. Should clicking a series row in the panel filter the grid, or navigate to CollectionView?
2. Should the panel show Authors tab too, or just Series for now?
3. Does "Monitor All" apply to books not yet in the library (i.e., auto-add missing series books)?
   — If yes, needs a separate "wanted series" concept (out of scope for v1)
   — For v1: only affects books already in the library
4. Panel width preference — fixed or resizable?

---

## Files That Would Be Touched

| File | Change |
|---|---|
| `fe/src/views/library/AudiobooksView.vue` | Add panel slot, wire filter state |
| `fe/src/components/library/SeriesPanel.vue` | **New** — the panel component |
| `fe/src/stores/library.ts` | Add series summary computed / panel state |
| `listenarr.api/Controllers/LibraryController.cs` | Possibly a `/series-summary` endpoint |

**Total: ~4 files (1 new)**

---

## Out of Scope (v1)

- Auto-adding missing books in a series from Audible
- Series as a first-class DB entity
- Author panel tab (add later)
- Resizable panel
