# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-01-09

### Added
- Initial release
- **Label-Based Audit** (`Tools > UI Audit > Label-Based Audit`)
  - Quick sprite categorization using Unity asset labels
  - Supports custom sizing rules via UIElementSizingPolicy
- **UGUI Usage Audit** (`Tools > UI Audit > UGUI Usage Audit`)
  - Deep scan of UGUI prefabs measuring actual RectTransform sizes
  - Detects blurry (undersized) and heavy (oversized) assets
  - Impact scoring: severity × usage count × display area
  - Memory waste estimation in KB
- **UIElementSizingPolicy** ScriptableObject for configuration
  - Top target resolution (e.g., 4K)
  - Reference design resolution
  - Oversample factor for retina displays
  - Custom rules per UI category (Icon/S, Icon/M, Logo, etc.)
- Height + width analysis (evaluates both dimensions)
- Local scale factor detection in transform hierarchy
- CSV export with proper escaping
- Nine-sliced/tiled image detection (skips resolution check)
- Stretched element estimation mode

### Fixed
- Portrait images now correctly evaluated (was width-only)
- Scaled parent transforms now factored into required pixels
- CSV export handles commas and special characters

[Unreleased]: https://github.com/coachcraft/ui-audit/compare/1.0.0...HEAD
[1.0.0]: https://github.com/coachcraft/ui-audit/releases/tag/1.0.0
