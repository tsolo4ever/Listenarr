# Plan: Series-Aware Library Management

## Problem

The left nav already has Books / Authors / Series tabs, and the Series view shows collection cards.
But there are no management features on top of it — you can't monitor/unmonitor a whole series,
see at a glance how many books in a series are missing, or take any bulk action from the series card.

Users have to go into each book individually to toggle monitored status even when they want to
apply the same setting to everything in a series.

---

## Current State

- Series tab exists in left nav → shows grid of series collection cards
- Each card shows cover art mosaic + series name + book count
- Clicking a card navigates to `CollectionView` (filtered list of books in that series)
- `CollectionView` has bulk edit/delete but you have to select books manually first
- `Monitored` is a per-book boolean — no series-level concept exists
- `BulkEditModal` already supports setting monitored across a list of IDs

---

## Proposed Changes

### 1. Series Card — add health indicators

Each series card in the grid gets a small status row beneath the title:

```
Divine Apostasy
1 book  •  ✓ monitored  •  ⚠ 0 missing
```

Or with colour-coded badges when things need attention:
```
The Stormlight Archive
8 books  •  6 monitored  •  ⚠ 2 missing
```

### 2. Series Card — quick-action button

A "Monitor All" / "Unmonitor All" button on hover (or always visible) that calls the existing
`POST /library/bulk-update` endpoint with the IDs of all books in that series.

### 3. CollectionView — series header actions

When viewing a series collection, add to the existing toolbar:
- "Monitor All" / "Unmonitor All" button (acts on all books in the series, no selection needed)
- Series health summary line: "8 books — 6 monitored — 2 missing — 1 downloading"

---

## What Needs a New Backend Endpoint

The series cards need health data (monitored count, missing count) without fetching all book
details. A lightweight summary endpoint makes sense:

```
GET /api/v1/library/series-summary
```

Returns:
```json
[
  {
    "series": "The Stormlight Archive",
    "totalBooks": 8,
    "monitoredBooks": 6,
    "missingBooks": 2,
    "ids": [1, 2, 3, 4, 5, 6, 7, 8]
  }
]
```

The `ids` array is what gets passed to `bulk-update` when Monitor All is clicked — no extra
endpoint needed for the action itself.

---

## Files to Touch

| File | Change |
|---|---|
| `listenarr.api/Controllers/LibraryController.cs` | Add `GET /library/series-summary` endpoint |
| `fe/src/services/api.ts` | Add `getSeriesSummary()` call |
| `fe/src/types/index.ts` | Add `SeriesSummary` interface |
| `fe/src/views/library/AudiobooksView.vue` | Pass summary data to collection cards |
| `fe/src/views/library/CollectionView.vue` | Add Monitor All / series health bar to toolbar |

**Total: 5 files (0 new)**

---

## Out of Scope (v1)

- Auto-adding missing books in a series from Audible
- Series as a first-class DB entity
- Author tab getting the same treatment (follow-up)
- Per-series quality profile or root folder override
