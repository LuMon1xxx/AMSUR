---
name: Mission Control Timetable
colors:
  surface: '#031427'
  surface-dim: '#031427'
  surface-bright: '#2a3a4f'
  surface-container-lowest: '#000f21'
  surface-container-low: '#0b1c30'
  surface-container: '#102034'
  surface-container-high: '#1b2b3f'
  surface-container-highest: '#26364a'
  on-surface: '#d3e4fe'
  on-surface-variant: '#c6c6cd'
  inverse-surface: '#d3e4fe'
  inverse-on-surface: '#213145'
  outline: '#909097'
  outline-variant: '#45464d'
  surface-tint: '#bec6e0'
  primary: '#bec6e0'
  on-primary: '#283044'
  primary-container: '#0f172a'
  on-primary-container: '#798098'
  inverse-primary: '#565e74'
  secondary: '#7bd0ff'
  on-secondary: '#00354a'
  secondary-container: '#00a6e0'
  on-secondary-container: '#00374d'
  tertiary: '#ffb95f'
  on-tertiary: '#472a00'
  tertiary-container: '#251400'
  on-tertiary-container: '#b47300'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#dae2fd'
  primary-fixed-dim: '#bec6e0'
  on-primary-fixed: '#131b2e'
  on-primary-fixed-variant: '#3f465c'
  secondary-fixed: '#c4e7ff'
  secondary-fixed-dim: '#7bd0ff'
  on-secondary-fixed: '#001e2c'
  on-secondary-fixed-variant: '#004c69'
  tertiary-fixed: '#ffddb8'
  tertiary-fixed-dim: '#ffb95f'
  on-tertiary-fixed: '#2a1700'
  on-tertiary-fixed-variant: '#653e00'
  background: '#031427'
  on-background: '#d3e4fe'
  surface-variant: '#26364a'
typography:
  display-lg:
    fontFamily: Hanken Grotesk
    fontSize: 32px
    fontWeight: '700'
    lineHeight: 40px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Hanken Grotesk
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.015em
  headline-md:
    fontFamily: Hanken Grotesk
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 24px
    letterSpacing: -0.01em
  headline-sm:
    fontFamily: Hanken Grotesk
    fontSize: 15px
    fontWeight: '600'
    lineHeight: 20px
    letterSpacing: -0.005em
  body-lg:
    fontFamily: Geist
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
    letterSpacing: 0em
  body-md:
    fontFamily: Geist
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 18px
    letterSpacing: 0em
  body-sm:
    fontFamily: Geist
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 16px
    letterSpacing: 0.01em
  label-md:
    fontFamily: Geist
    fontSize: 11px
    fontWeight: '600'
    lineHeight: 14px
    letterSpacing: 0.04em
  label-sm:
    fontFamily: Geist
    fontSize: 10px
    fontWeight: '700'
    lineHeight: 12px
    letterSpacing: 0.06em
  data-mono:
    fontFamily: Geist
    fontSize: 12px
    fontWeight: '500'
    lineHeight: 16px
    letterSpacing: -0.01em
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  gutter: 0.75rem
  margin: 1rem
  space-xs: 0.25rem
  space-sm: 0.375rem
  space-md: 0.75rem
  space-lg: 1rem
  space-xl: 1.5rem
---

## Brand & Style

This design system establishes a high-density, mission-control operational interface engineered for complex academic scheduling, cohort load tracking, and real-time conflict mitigation. Built to support heavy cognitive workflows under tight administrative deadlines, the interface marries the precision of flight-operations telemetry with the structural dependability required by modern institutional software.

The aesthetic leverages high-contrast tactical dark surfaces, crisp micro-borders, and disciplined data density. Visual noise is rigorously eliminated; chromatic accents are reserved strictly for operational status indicators, structural validation warnings, and scheduling locks. The interface evokes unyielding stability, micro-precision, and technical mastery, empowering operators to navigate thousands of resource allocations with immediate clarity and zero fatigue.

## Colors

The palette is anchored by `#0F172A`, establishing an immersive, tactical slate baseline that minimizes eye strain across 10-hour administrative shifts. Secondary electric cobalt-cyan (`#38BDF8`) provides high-contrast operational focus, selection outlines, and active state indicators. Tertiary amber (`#F59E0B`) serves as an immediate attention trigger for constraint violations, teacher over-allocation, and pending timetable collision states. 

Surfaces progress upward through calculated luminosity tiers:
- **Base Canvas (`#0B0F19` / `#0F172A`)**: The deep foundation for framing panes, splitters, and application chrome.
- **Surface Level 1 (`#1E293B`)**: Standard data grid containers, inspector panels, and secondary toolbars.
- **Surface Level 2 (`#334155`)**: Active slot headers, floating tooltips, popover selectors, and table headers.
- **Borders & Gridlines (`#1E293B` to `#334155`)**: Crisp, 1px mathematical separators defining absolute boundaries between timetable blocks without relying on ambient blurs.
- **Feedback Accents**: Emerald green (`#10B981`) indicates validated, error-free schedule solutions; crimson (`#EF4444`) denotes hard constraint violations (e.g., room double-booking).

## Typography

Typography prioritizes tabular alignment, compact horizontal economy, and rapid optical scanning. Headings rendered in **Hanken Grotesk** bring a crisp, architectural geometry to cockpit status headers, modal dialogs, and workspace titles. 

Data values, dense timetable matrices, inspector property sheets, and system status readouts are driven by **Geist**. Geist's neutral, monolinear construction and balanced x-height ensure that numbers, room codes, cohort initials (e.g., "10-А", "каб. 402"), and time ranges remain legible at small point sizes without horizontal distortion. 

All timetable slot cells, duration badges, and constraint metrics employ `data-mono` or `label-sm` with tabular numerical figures (`tnum`) enabled by default to prevent layout shifting during real-time calculation passes.

## Layout & Spacing

The layout model reflects native high-performance desktop productivity software, operating on a 4px baseline rhythm optimized for screen estates from 1080p upward:
- **Left Command Rail / Sidebar**: Fixed at 240px width. Houses institutional taxonomy, grade-level pickers, teacher manifests, and simulation controls. Collapsible to a 48px micro-icon rail.
- **Top Command Bar**: 44px fixed height containing quick search, simulation run triggers, view toggles (Teacher View / Class View / Room Matrix), and system error diagnostics.
- **Main Timetable Matrix**: A flexible grid conforming to strict scheduling time columns (e.g., Period 1 through Period 8) and row grouping by day and classroom. Cells dynamically fill horizontal space while respecting a minimum column width of 120px.
- **Right Inspector Drawer**: 320px fixed panel displaying contextual properties of the active schedule token, conflict details, and algorithmic suggestions.

Gutters inside data grids drop to `0.25rem` to `0.375rem`, guaranteeing maximum visual payload within standard viewport bounds without requiring excessive vertical scrolling.

## Elevation & Depth

Depth is established primarily through tonal layering and hard 1px tactical borders rather than diffused ambient shadows. Diffuse shadows are completely omitted in high-density grid regions to prevent visual blur.

- **Level 0 (Floor Canvas)**: `#0B0F19` base canvas. Grid backplanes sit here.
- **Level 1 (Docked Panels & Workspaces)**: `#0F172A` with a 1px solid border of `#1E293B`. Used for primary schedule boards and sidebar panels.
- **Level 2 (Active Cards & Schedule Blocks)**: `#1E293B` surface with a 1px border of `#334155`. Placed blocks show subtle internal contrast.
- **Level 3 (Hovered / Dragged Tokens)**: `#334155` surface with an active `#38BDF8` 1px border and a directional 0px 4px 12px rgba(0, 0, 0, 0.45) drop shadow to signify elevation during drag-and-drop timetable reassignments.
- **Level 4 (Context Popovers & Dialogs)**: `#1E293B` background surrounded by a dual-line edge: an inner 1px `#334155` boundary and an outer 1px high-contrast `#000000` rim.

## Shapes

The interface adheres to an intentional, micro-calibrated shape scale (`roundedness: 1`). Radii are kept ultra-compact (`0.25rem` / 4px default) to preserve pixel real estate in compact tabular views and reinforce an engineered, instruments-first aesthetic.

- **Timetable Slot Cells**: 4px border radius with inset borders to maintain hard architectural boundaries across dense arrays.
- **Badges, Status Pills & Conflict Chips**: 4px radius, avoiding rounded pills to preserve text alignment discipline.
- **Buttons & Action Triggers**: 4px radius on all standard desktop action elements.
- **Flyout Tooltips & Modal Sheets**: 4px outer radius with matching inner corners to create structural consistency.

## Components

### Action Controls (Buttons & Toolbars)
- **Primary Action**: Background `#38BDF8`, text `#0F172A` (bold), 4px border-radius, height 30px, padding `0 12px`. Hover shifts to `#7DD3FC`.
- **Tactical Secondary**: Background `#1E293B`, border 1px solid `#334155`, text `#F8FAFC`. Active state shifts border to `#38BDF8`.
- **Destructive Action**: Background `#7F1D1D`, border 1px solid `#EF4444`, text `#FEE2E2`.

### Timetable Blocks (Lesson Tokens)
- **Compact Surface**: Rendered on `#1E293B` with a solid left accent border (3px) color-coded by subject domain (e.g., Blue for STEM, Amber for Humanities, Green for Physical Education).
- **Block Layout**: Displays room code (top right, `label-sm`), subject mnemonic (top left, `headline-sm`), and instructor surname (bottom left, `body-sm`).
- **Conflict State**: Flashing 1px perimeter border in `#EF4444` accompanied by a diagonal micro-hatch watermark pattern in the cell background.

### Metric Cards & Telemetry Chips
- **Status Cards**: Bordered tiles showing Teacher Load Index, Window Count (окна в расписании), and Room Capacity Metrics. Value rendered in `headline-lg` with Geist tabular numerals; subtitle in `label-md` uppercase `#64748B`.
- **Validation Badges**: `label-sm` font, padding `2px 6px`, 4px radius. Inactive: `#1E293B` with `#94A3B8` text. Collision: `#450A0A` surface with `#F87171` text.

### Inputs & Selectors
- **Micro Form Inputs**: Height 28px, background `#0B0F19`, border 1px solid `#334155`, text `#F8FAFC`. Focus ring is a crisp 1px `#38BDF8` with zero outer glow.
- **Checkbox & Radio**: 14x14px square inputs with 2px radius. Checkmark renders as an exact 1.5px geometric vector in `#0F172A` over `#38BDF8` fill when active.