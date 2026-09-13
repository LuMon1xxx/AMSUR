---
name: Fluent Algorithmic Workbench
colors:
  surface: '#faf8ff'
  surface-dim: '#d2d9f4'
  surface-bright: '#faf8ff'
  surface-container-lowest: '#ffffff'
  surface-container-low: '#f2f3ff'
  surface-container: '#eaedff'
  surface-container-high: '#e2e7ff'
  surface-container-highest: '#dae2fd'
  on-surface: '#131b2e'
  on-surface-variant: '#434655'
  inverse-surface: '#283044'
  inverse-on-surface: '#eef0ff'
  outline: '#737686'
  outline-variant: '#c3c6d7'
  surface-tint: '#0053db'
  primary: '#004ac6'
  on-primary: '#ffffff'
  primary-container: '#2563eb'
  on-primary-container: '#eeefff'
  inverse-primary: '#b4c5ff'
  secondary: '#4b41e1'
  on-secondary: '#ffffff'
  secondary-container: '#645efb'
  on-secondary-container: '#fffbff'
  tertiary: '#006058'
  on-tertiary: '#ffffff'
  tertiary-container: '#007b71'
  on-tertiary-container: '#b3fff3'
  error: '#ba1a1a'
  on-error: '#ffffff'
  error-container: '#ffdad6'
  on-error-container: '#93000a'
  primary-fixed: '#dbe1ff'
  primary-fixed-dim: '#b4c5ff'
  on-primary-fixed: '#00174b'
  on-primary-fixed-variant: '#003ea8'
  secondary-fixed: '#e2dfff'
  secondary-fixed-dim: '#c3c0ff'
  on-secondary-fixed: '#0f0069'
  on-secondary-fixed-variant: '#3323cc'
  tertiary-fixed: '#89f5e7'
  tertiary-fixed-dim: '#6bd8cb'
  on-tertiary-fixed: '#00201d'
  on-tertiary-fixed-variant: '#005049'
  background: '#faf8ff'
  on-background: '#131b2e'
  surface-variant: '#dae2fd'
typography:
  display-lg:
    fontFamily: Geist
    fontSize: 1.75rem
    fontWeight: '600'
    lineHeight: 2.25rem
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Geist
    fontSize: 1.375rem
    fontWeight: '600'
    lineHeight: 1.875rem
    letterSpacing: -0.015em
  headline-md:
    fontFamily: Geist
    fontSize: 1.125rem
    fontWeight: '600'
    lineHeight: 1.625rem
    letterSpacing: -0.01em
  headline-sm:
    fontFamily: Geist
    fontSize: 0.9375rem
    fontWeight: '600'
    lineHeight: 1.375rem
    letterSpacing: -0.005em
  body-lg:
    fontFamily: JetBrains Mono
    fontSize: 0.875rem
    fontWeight: '400'
    lineHeight: 1.375rem
  body-md:
    fontFamily: JetBrains Mono
    fontSize: 0.8125rem
    fontWeight: '400'
    lineHeight: 1.25rem
  body-sm:
    fontFamily: JetBrains Mono
    fontSize: 0.75rem
    fontWeight: '400'
    lineHeight: 1.125rem
  label-md:
    fontFamily: JetBrains Mono
    fontSize: 0.75rem
    fontWeight: '500'
    lineHeight: 1rem
    letterSpacing: 0.02em
  label-sm:
    fontFamily: JetBrains Mono
    fontSize: 0.6875rem
    fontWeight: '500'
    lineHeight: 0.875rem
    letterSpacing: 0.04em
  code-cell:
    fontFamily: JetBrains Mono
    fontSize: 0.75rem
    fontWeight: '400'
    lineHeight: 1rem
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
  space-sm: 0.5rem
  space-md: 0.75rem
  space-lg: 1rem
  space-xl: 1.5rem
---

## Brand & Style

This design system establishes a high-density, authoritative desktop environment designed specifically for educational administrators orchestrating complex combinatorial scheduling. It merges the tactile precision of Windows 11 Fluent Design (Mica/Acrylic layering, crisp hairline strokes) with the disciplined speed and typographic rigor of Linear.

The aesthetic philosophy centers on:
- **Algorithmic Transparency:** Surfacing dense constraint metrics, room availability, teacher workloads, and SANPiN compliance rules clearly without overwhelming the user.
- **Calm Authority:** A soothing slate canvas punctuated by deep digital cobalt accents conveys system stability and computational rigor.
- **Architectural Clarity:** Strict layout boundaries, compact control heights, and high tabular data legibility suitable for extended periods of analytical desk work.
- **WPF / XAML Ergonomics:** Components match native desktop paradigms (Sidebar Navigation, Docked Toolbars, DataGrids, Split Panes, Property Sheets) while maintaining contemporary software elegance.

## Colors

The palette is engineered for prolonged desktop screen exposure and multi-state scheduling matrices.

### Color Roles & Semantics
- **Primary (`#2563EB`):** High-precision digital cobalt used for execution triggers, key generation actions, active selection rings, and primary focus boundaries.
- **Secondary (`#4F46E5`):** Deep indigo reserved for institutional context badges, highlighted schedule groups, and secondary action accents.
- **Tertiary (`#0D9488`):** Deep algorithmic teal applied to automated verification tags, valid constraint slots, and structural analytics.
- **Neutral Surface Palette:**
  - **Window Canvas:** `#F8FAFC` (App background layer).
  - **Panel / Card Surface:** `#FFFFFF` with hairline contrast.
  - **Subtle Fill / Row Hover:** `#F1F5F9`.
  - **Active Selection Fill:** `#EFF6FF`.
- **System Boundaries:**
  - **Border Base:** `#E2E8F0` (Hairline container separator).
  - **Border Muted:** `#CBD5E1` (Interactive control boundaries).
- **Diagnostics & Status:**
  - **Success (`#10B981`):** Slot validated, no schedule conflicts.
  - **Warning (`#F59E0B`):** Teacher double-window, pedagogical gap.
  - **Danger / Conflict (`#EF4444`):** Room collision, SANPiN violation, teacher overlap.

## Typography

The typographic hierarchy implements an algorithmic workbench configuration:

1. **Headlines (`Geist`):** Delivers clean neo-grotesque authority for application framing, module titles, summary metrics, and wizard step labels.
2. **Body & Controls (`JetBrains Mono`):** Delivers absolute tabular fidelity. Monospaced rendering ensures room numbers (`304-A`), class labels (`10-B`), teacher initials (`Иванова Е.В.`), hour allocations, and time ranges (`08:30 - 09:15`) stay vertically aligned across massive data sheets without horizontal drift.
3. **Tabular Figures:** All numeric displays (windows count, conflict counters, weekly lesson sums) align to standard character widths to preserve readability in dense XAML DataGrids.

## Layout & Spacing

The layout utilizes a structured desktop dock-and-panel architecture built for widescreen displays (1920x1080 and above) while remaining usable down to 1280x720.

### Structural Zones
- **Navigation Dock (Left):** Fixed width of `240px` (collapsible to `64px` icon rail). Provides direct access to primary modules.
- **Main Canvas (Center):** Fluid viewport hosting modular dashboard cards, wizard banners, or infinite-scroll grid matrices.
- **Inspector Rail (Right):** Contextual tool window width `320px` to `380px` containing configuration toggles, generation presets, conflict checkers, and SANPiN quality meters.
- **Status & Control Bar (Bottom):** Fixed height `32px` desktop utility strip indicating solver state, engine iterations, active profile, and memory footprint.

### Density Rules
- Component paddings default to compact desktop increments (`space-sm` and `space-md`).
- Grids and schedule matrix cells use a standardized cell-height minimum of `36px` to maximize visible periods per screen without vertical clutter.

## Elevation & Depth

This system avoids heavy drop shadows, relying instead on Fluent-inspired micro-layering, crisp border hierarchies, and tonal surfaces:

- **Level 0 (Base Layer):** Canvas background `#F8FAFC`. Zero elevation.
- **Level 1 (Docked Containers & Standard Cards):** Background `#FFFFFF`, border `1px solid #E2E8F0`, shadow `0 1px 2px 0 rgba(15, 23, 42, 0.04)`.
- **Level 2 (Interactive Floating Panels & Flyouts):** Background `#FFFFFF`, border `1px solid #CBD5E1`, shadow `0 4px 12px -2px rgba(15, 23, 42, 0.08)`.
- **Level 3 (Modal Dialogs & Command Palettes):** Background `#FFFFFF`, border `1px solid #94A3B8`, shadow `0 12px 28px -4px rgba(15, 23, 42, 0.16)`.
- **Level Active / Hero (Computation Banner):** Gradient fill `linear-gradient(135deg, #2563EB 0%, #4F46E5 100%)` with subtle internal inset highlight `inset 0 1px 0 0 rgba(255, 255, 255, 0.2)`.

## Shapes

In alignment with the selected configuration, this design system uses soft, precise geometry tailored for high-density Windows application shells:

- **Base Radius (`0.25rem` / `4px`):** Used for grid cells, input fields, inline tags, toolbar action buttons, and table check selectors.
- **Container Radius (`rounded-lg`: `0.5rem` / `8px`):** Used for standard cards, inspector group panels, and sub-module sections.
- **Window / Surface Radius (`rounded-xl`: `0.75rem` / `12px`):** Used for major workspace containers, dialog shells, and the hero generation card.

## Components

### 1. Buttons
- **Primary Trigger:** Solid `#2563EB` fill, white text, 1px `#1D4ED8` border. Hover: `#1D4ED8`. Active: `#1E40AF`. Height: `36px` (Default), `32px` (Compact Toolbar).
- **Secondary Action:** White surface, 1px `#CBD5E1` border, `#0F172A` text. Hover: `#F1F5F9`.
- **Ghost / Tool Button:** Transparent background, `#475569` text. Hover: `#F1F5F9`, `#0F172A` text.
- **Hero CTA:** White fill on primary hero gradient with `#2563EB` text and high-contrast chevron icon.

### 2. Schedule Grid Cell (Custom Specialized Component)
- Compact rectangular slot (`roundedness: 1`), bordered in `#E2E8F0`.
- Contains:
  - Subject code (bold `Geist`, `11px`).
  - Teacher monospaced tag (`JetBrains Mono`, `10px`, `#64748B`).
  - Room chip badge (`JetBrains Mono`, `9px`, rounded `2px`, `#F1F5F9` background).
- States: Empty (dashed border), Selected (ring 2px `#2563EB`), Conflict (soft `#FEF2F2` fill with `#DC2626` warning flag).

### 3. Selection Cards & Generation Presets
- Bordered panel (`1px solid #E2E8F0`) with 8px corner rounding.
- Hover transition to `#EFF6FF` border tint.
- Selected state activates a `2px solid #2563EB` outline and embeds a radio indicator with a 4px inner dot.

### 4. Input Fields & Dropdowns
- Height `32px` for high-density table parameters.
- Background `#FFFFFF`, border `1px solid #CBD5E1`.
- Focused state: `#FFFFFF` surface, border `1px solid #2563EB`, focus outline `2px solid rgba(37, 99, 235, 0.15)`.

### 5. Chips & Diagnostic Badges
- Height `20px`, padding `0 6px`, typography `label-sm`.
- **Neutral:** `#F1F5F9` bg, `#475569` text.
- **Success:** `#ECFDF5` bg, `#059669` text, `10B981` dot.
- **Warning:** `#FFFBEB` bg, `#D97706` text, `F59E0B` dot.
- **Conflict:** `#FEF2F2` bg, `#DC2626` text, `EF4444` dot.

### 6. Sidebar Navigation Items
- Vertical stacked list with 4px margin gaps.
- Inactive: `#475569` text, transparent background, 20px icon gutter.
- Active: `#EFF6FF` background, `#2563EB` text, left accent strip `3px solid #2563EB`.