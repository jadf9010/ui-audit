using UnityEngine;
using System;
using UnityEngine.UIElements;

namespace UIAudit.Runtime
{
    [CreateAssetMenu(fileName = "UIElementSizingPolicy", menuName = "Config/UI Element Sizing Policy")]
    public sealed class UIElementSizingPolicy : ScriptableObject
    {
        [Header("Top target (device)")]
        [Tooltip("Maximum target screen width in pixels (e.g., 3840 for 4K).")]
        public int topWidth = 3840;
        [Tooltip("Maximum target screen height in pixels (e.g., 2160 for 4K).")]
        public int topHeight = 2160;

        [Header("Reference & match (fallback if no PanelSettings)")]
        [Tooltip("Reference design width in pixels.")]
        public int referenceWidth = 1920;
        [Tooltip("Reference design height in pixels.")]
        public int referenceHeight = 1080;
        [Range(0f, 1f)]
        [Tooltip("0 = scale by width, 1 = scale by height, 0.5 = blend.")]
        public float match = 0.5f;

        [Header("Quality & memory")]
        [Range(1.0f, 3.0f)]
        [Tooltip("Oversample factor for crisp rendering (2x recommended for retina).")]
        public float oversample = 2.0f;
        [Range(1.0f, 10.0f)]
        [Tooltip("Threshold multiplier for flagging oversized assets.")]
        public float oversizePixelFactor = 2.0f;

        [Serializable]
        public class Rule
        {
            [Tooltip("Label: UI/Icon/M, UI/Logo, ...")]
            public string label;
            [Tooltip("Logical width at reference (px).")]
            public int logicalRefWidthPx = 32;
            [Tooltip("Logical height at reference (px). 0 = use width (square).")]
            public int logicalRefHeightPx = 0;
            [Tooltip("Per-rule oversample (0 = use global).")]
            [Range(0f, 3f)] public float oversampleOverride = 0f;
            [Tooltip("Minimum authored width override (0 = auto).")]
            public int minAuthoredPixelsOverride = 0;

            /// <summary>
            /// Returns the effective logical height, defaulting to width if 0.
            /// </summary>
            public int EffectiveLogicalHeight => logicalRefHeightPx > 0 ? logicalRefHeightPx : logicalRefWidthPx;
        }

        [Header("Rules (edit in asset)")]
        public Rule[] rules = new[]
        {
            new Rule { label = "UI/Icon/XS", logicalRefWidthPx = 16 },
            new Rule { label = "UI/Icon/S",  logicalRefWidthPx = 24 },
            new Rule { label = "UI/Icon/M",  logicalRefWidthPx = 32 },
            new Rule { label = "UI/Icon/L",  logicalRefWidthPx = 48 },
            new Rule { label = "UI/Button/H", logicalRefWidthPx = 56 },
            new Rule { label = "UI/Logo",    logicalRefWidthPx = 400 },
            new Rule { label = "UI/Hero",    logicalRefWidthPx = 600 },
        };

        public bool TryGetRule(string label, out Rule rule)
        {
            if (!string.IsNullOrEmpty(label))
            {
                foreach (var r in rules)
                    if (!string.IsNullOrEmpty(r.label) && r.label == label)
                    { rule = r; return true; }
            }
            rule = null; return false;
        }

        public float ComputeScaleMax(PanelSettings panel = null)
        {
            Vector2 refRes; float m;
            if (panel != null && panel.scaleMode == PanelScaleMode.ScaleWithScreenSize)
            {
                refRes = panel.referenceResolution; m = panel.match;
            }
            else { refRes = new Vector2(referenceWidth, referenceHeight); m = match; }
            float sW = topWidth / Mathf.Max(1f, refRes.x);
            float sH = topHeight / Mathf.Max(1f, refRes.y);
            return Mathf.Lerp(sW, sH, Mathf.Clamp01(m));
        }

        /// <summary>
        /// Computes required authored pixels for width dimension.
        /// </summary>
        public int RequiredAuthoredPixelsW(string label, PanelSettings panel = null)
        {
            if (!TryGetRule(label, out var r)) return 0;
            float scale = ComputeScaleMax(panel);
            float os = (r.oversampleOverride > 0f ? r.oversampleOverride : oversample);
            int displayMax = Mathf.CeilToInt(Mathf.Max(0, r.logicalRefWidthPx) * scale);
            int required = Mathf.CeilToInt(displayMax * os);
            if (r.minAuthoredPixelsOverride > 0) required = Mathf.Max(required, r.minAuthoredPixelsOverride);
            return required;
        }

        /// <summary>
        /// Computes required authored pixels for height dimension.
        /// </summary>
        public int RequiredAuthoredPixelsH(string label, PanelSettings panel = null)
        {
            if (!TryGetRule(label, out var r)) return 0;
            float scale = ComputeScaleMax(panel);
            float os = (r.oversampleOverride > 0f ? r.oversampleOverride : oversample);
            int displayMax = Mathf.CeilToInt(Mathf.Max(0, r.EffectiveLogicalHeight) * scale);
            int required = Mathf.CeilToInt(displayMax * os);
            if (r.minAuthoredPixelsOverride > 0) required = Mathf.Max(required, r.minAuthoredPixelsOverride);
            return required;
        }

        // Legacy compatibility
        public int RequiredAuthoredPixels(string label, PanelSettings panel = null) => RequiredAuthoredPixelsW(label, panel);
    }
}
