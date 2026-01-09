# UI Asset Audit

A Unity Editor toolset for auditing UI sprite/texture quality. Detects **blurry** (undersized) and **heavy** (oversized) assets based on actual display usage in UGUI prefabs.

## Installation

### Via Unity Package Manager (Local)

1. Copy the `com.coachcraft.ui-audit` folder to your project's `Packages/` directory
2. Unity will automatically detect and import the package

### Via Git URL

Add to your `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.coachcraft.ui-audit": "https://github.com/coachcraft/ui-audit.git"
  }
}
```

### Targeting Specific Versions

Use Git tags to lock to a specific version:
```json
{
  "dependencies": {
    "com.coachcraft.ui-audit": "https://github.com/coachcraft/ui-audit.git#1.0.0"
  }
}
```

## Features

- **Height + Width Analysis**: Evaluates both dimensions to catch portrait/landscape image issues
- **Local Scale Detection**: Accounts for scaled transforms in the hierarchy
- **Actual Usage Scanning**: Analyzes prefabs to measure real display sizes
- **Impact Scoring**: Prioritizes issues by severity × usage count × display area
- **Memory Waste Estimation**: Shows approximate KB wasted by oversized textures
- **CSV Export**: Export results for external analysis (properly escaped)

## Tools

### 1. Label-Based Audit (`Tools > UI Audit > Label-Based Audit`)

Quick categorization of sprites using Unity asset labels (`UI/Icon/M`, `UI/Logo`, etc.).

**Best for**: Initial tagging and rough estimation based on intended usage.

### 2. UGUI Usage Audit (`Tools > UI Audit > UGUI Usage Audit`)

Deep scan of UGUI prefabs measuring actual `RectTransform` sizes at runtime.

**Best for**: Accurate detection of resolution mismatches in real layouts.

## Configuration

### UIElementSizingPolicy (ScriptableObject)

Create via `Assets > Create > Config > UI Element Sizing Policy`

| Field | Description |
|-------|-------------|
| **topWidth/Height** | Maximum target device resolution (e.g., 3840×2160 for 4K) |
| **referenceWidth/Height** | Design reference resolution (e.g., 1920×1080) |
| **match** | Width/height blend (0=width, 1=height, 0.5=balanced) |
| **oversample** | Quality multiplier (2.0 recommended for retina) |
| **oversizePixelFactor** | Threshold for flagging heavy assets (2.0 = 2× required) |

### Rules

Define categories with logical sizes at reference resolution:

```
UI/Icon/XS → 16px
UI/Icon/S  → 24px
UI/Icon/M  → 32px
UI/Icon/L  → 48px
UI/Logo    → 400px
UI/Hero    → 600px
```

## Result Types

| Type | Description |
|------|-------------|
| **Blurry** | Image has fewer pixels than required — will appear fuzzy |
| **Heavy** | Image has more pixels than needed — wastes memory/bandwidth |
| **Context** | Stretched/sliced elements that can't be accurately measured |
| **OK** | Within acceptable range |

## Severity Levels

| Severity | Blurry (ratio) | Heavy (factor) |
|----------|---------------|----------------|
| **CRITICAL** | < 50% | ≥ 10× |
| **HIGH** | < 75% | ≥ 5× |
| **MED** | < 90% | ≥ 3× |
| **LOW** | < 100% | ≥ 2× |

## Formula

```
DisplayPx@Top = DisplayPx@Ref × ScaleToTop
RequiredPx = DisplayPx@Top × Oversample × LocalScale

ScaleToTop = Lerp(topW/refW, topH/refH, match)
```

## Changelog

### v1.0.0
- Initial release
- Height + width analysis (fixes portrait image false positives)
- Local scale factor detection
- CSV escaping for proper export
- Standalone package structure

## License

MIT
