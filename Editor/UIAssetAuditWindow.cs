#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UIAudit.Runtime;

namespace UIAudit.Editor
{
    public sealed class UIAssetAuditWindow : EditorWindow
    {
        private Vector2 _scroll;
        private string _filter = "";
        private UIElementSizingPolicy _policy;
        private PanelSettings _panel;
        private List<Row> _rows = new();

        private bool _includeUnlabeled = true;
        private string[] _labelValues = Array.Empty<string>();
        private string[] _labelDisplay = Array.Empty<string>();

        [MenuItem("Tools/UI Audit/Label-Based Audit")]
        public static void Open() => GetWindow<UIAssetAuditWindow>("UI Label Audit");

        private void OnEnable()
        {
            if (_policy == null)
            {
                var guids = AssetDatabase.FindAssets("t:UIElementSizingPolicy");
                if (guids.Length > 0)
                    _policy = AssetDatabase.LoadAssetAtPath<UIElementSizingPolicy>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            BuildLabelArrays();
            Scan();
        }

        private void BuildLabelArrays()
        {
            if (_policy == null || _policy.rules == null)
            {
                _labelValues = new[] { string.Empty };
                _labelDisplay = new[] { "— None —" };
                return;
            }
            var vals = new List<string> { string.Empty };
            foreach (var r in _policy.rules)
            {
                if (!string.IsNullOrEmpty(r.label) && !vals.Contains(r.label))
                    vals.Add(r.label);
            }
            _labelValues = vals.ToArray();
            _labelDisplay = _labelValues.Select(s => string.IsNullOrEmpty(s) ? "— None —" : s).ToArray();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var newPolicy = (UIElementSizingPolicy)EditorGUILayout.ObjectField("UI Policy", _policy, typeof(UIElementSizingPolicy), false);
                if (newPolicy != _policy) { _policy = newPolicy; BuildLabelArrays(); }
                _panel = (PanelSettings)EditorGUILayout.ObjectField(new GUIContent("PanelSettings (opt)"), _panel, typeof(PanelSettings), false);
                _includeUnlabeled = EditorGUILayout.ToggleLeft(new GUIContent("Include unlabeled", "Show sprites without UI/* label so you can tag them here"), _includeUnlabeled, GUILayout.Width(140));
                if (GUILayout.Button("Rescan", GUILayout.Width(80))) Scan();
                if (GUILayout.Button("Export CSV", GUILayout.Width(100))) ExportCsv();
            }
            _filter = EditorGUILayout.TextField("Filter", _filter);

            if (_policy == null)
            {
                EditorGUILayout.HelpBox("Assign a UIElementSizingPolicy asset.", MessageType.Info);
                return;
            }

            DrawHeader();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var r in _rows.Where(r => string.IsNullOrEmpty(_filter) ||
                                               r.Path.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               r.Sprite.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0))
                DrawRow(r);
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Asset / Sprite", GUILayout.Width(420));
                GUILayout.Label("Label", GUILayout.Width(160));
                GUILayout.Label("Pixels (WxH)", GUILayout.Width(100));
                GUILayout.Label("Logical@Ref", GUILayout.Width(100));
                GUILayout.Label("MaxDisplay", GUILayout.Width(100));
                GUILayout.Label("Required", GUILayout.Width(100));
                GUILayout.Label("Result", GUILayout.Width(320));
                GUILayout.Label("Assign", GUILayout.Width(160));
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawRow(Row r)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(r.Path + " :: " + r.Sprite, EditorStyles.label, GUILayout.Width(420)))
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(r.Path));

                GUILayout.Label(r.Label, GUILayout.Width(160));
                GUILayout.Label($"{r.PixelsW}x{r.PixelsH}", GUILayout.Width(100));
                GUILayout.Label($"{r.LogicalRefPxW}x{r.LogicalRefPxH}", GUILayout.Width(100));
                GUILayout.Label($"{r.MaxDisplayPxW}x{r.MaxDisplayPxH}", GUILayout.Width(100));
                GUILayout.Label($"{r.RequiredPxW}x{r.RequiredPxH}", GUILayout.Width(100));
                GUILayout.Label(string.IsNullOrEmpty(r.Result) ? "OK" : r.Result, GUILayout.Width(320));

                int idx = Array.IndexOf(_labelValues, r.Label); if (idx < 0) idx = 0;
                int nidx = EditorGUILayout.Popup(idx, _labelDisplay, GUILayout.Width(160));
                if (nidx != idx)
                {
                    string chosen = _labelValues[nidx];
                    ApplyUiLabel(r.Path, chosen);
                    r.Label = chosen;
                    RecomputeRowMetrics(r, chosen);
                }

                GUILayout.FlexibleSpace();
            }
        }

        private void RecomputeRowMetrics(Row r, string chosenLabel)
        {
            if (!string.IsNullOrEmpty(chosenLabel) && _policy != null && _policy.TryGetRule(chosenLabel, out var rule))
            {
                float scale = _policy.ComputeScaleMax(_panel);
                r.LogicalRefPxW = Mathf.Max(0, rule.logicalRefWidthPx);
                r.LogicalRefPxH = Mathf.Max(0, rule.EffectiveLogicalHeight);
                r.MaxDisplayPxW = Mathf.CeilToInt(r.LogicalRefPxW * scale);
                r.MaxDisplayPxH = Mathf.CeilToInt(r.LogicalRefPxH * scale);
                float os = (rule.oversampleOverride > 0 ? rule.oversampleOverride : _policy.oversample);
                r.RequiredPxW = Mathf.CeilToInt(r.MaxDisplayPxW * os);
                r.RequiredPxH = Mathf.CeilToInt(r.MaxDisplayPxH * os);

                r.Result = EvaluateResult(r.PixelsW, r.PixelsH, r.RequiredPxW, r.RequiredPxH);
            }
        }

        private string EvaluateResult(int haveW, int haveH, int reqW, int reqH)
        {
            // Use worst-case ratio (minimum of width and height ratios)
            float ratioW = reqW > 0 ? haveW / (float)reqW : 1f;
            float ratioH = reqH > 0 ? haveH / (float)reqH : 1f;
            float worstRatio = Mathf.Min(ratioW, ratioH);

            if (worstRatio < 1f - 0.001f)
            {
                string sev = worstRatio < 0.5f ? "CRITICAL" : worstRatio < 0.75f ? "HIGH" : worstRatio < 0.9f ? "MED" : "LOW";
                string dim = ratioW < ratioH ? "W" : "H";
                return $"Blurry {sev} ({dim}: have {(ratioW < ratioH ? haveW : haveH)}px, need {(ratioW < ratioH ? reqW : reqH)}px)";
            }

            // Heavy check
            float heavyRatioW = reqW > 0 ? haveW / (float)reqW : 1f;
            float heavyRatioH = reqH > 0 ? haveH / (float)reqH : 1f;
            float heavyX = Mathf.Max(heavyRatioW, heavyRatioH);

            if (heavyX > _policy.oversizePixelFactor)
            {
                string sev = heavyX >= 10 ? "CRITICAL" : heavyX >= 5 ? "HIGH" : heavyX >= 3 ? "MED" : heavyX >= 2 ? "LOW" : "INFO";
                return $"Heavy {sev} x{heavyX:0.0} (need {reqW}x{reqH}px)";
            }

            return string.Empty;
        }

        private void ApplyUiLabel(string assetPath, string chosenLabel)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (!obj) return;
            var labels = new HashSet<string>(AssetDatabase.GetLabels(obj));
            foreach (var l in labels.Where(l => l.StartsWith("UI/")).ToList()) labels.Remove(l);
            if (!string.IsNullOrEmpty(chosenLabel)) labels.Add(chosenLabel);
            AssetDatabase.SetLabels(obj, labels.ToArray());
        }

        private void Scan()
        {
            _rows.Clear();
            if (_policy == null) return;

            var guids = AssetDatabase.FindAssets("t:Texture2D");
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var lower = path.ToLowerInvariant();
                if (!lower.StartsWith("assets/")) continue;
                if (lower.Contains("/packages/") || lower.Contains("/packagecache/")) continue;

                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null || ti.textureType != TextureImporterType.Sprite) continue;

                var main = AssetDatabase.LoadMainAssetAtPath(path);
                var labels = main ? AssetDatabase.GetLabels(main) : Array.Empty<string>();
                var uiLabel = labels.FirstOrDefault(l => l.StartsWith("UI/")) ?? string.Empty;
                if (string.IsNullOrEmpty(uiLabel) && !_includeUnlabeled) continue;

                UIElementSizingPolicy.Rule rule = null;
                if (!string.IsNullOrEmpty(uiLabel))
                {
                    if (!_policy.TryGetRule(uiLabel, out rule))
                        rule = null;
                }

                var sprites = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToArray();
                if (sprites.Length == 0) continue;

                float scale = _policy.ComputeScaleMax(_panel);
                int logicalW = (rule != null) ? Mathf.Max(0, rule.logicalRefWidthPx) : 0;
                int logicalH = (rule != null) ? Mathf.Max(0, rule.EffectiveLogicalHeight) : 0;
                int displayMaxW = (rule != null) ? Mathf.CeilToInt(logicalW * scale) : 0;
                int displayMaxH = (rule != null) ? Mathf.CeilToInt(logicalH * scale) : 0;
                float os = (rule != null && rule.oversampleOverride > 0 ? rule.oversampleOverride : _policy.oversample);
                int requiredW = (rule != null) ? Mathf.CeilToInt(displayMaxW * os) : 0;
                int requiredH = (rule != null) ? Mathf.CeilToInt(displayMaxH * os) : 0;

                foreach (var sp in sprites)
                {
                    int haveW = Mathf.RoundToInt(sp.rect.width);
                    int haveH = Mathf.RoundToInt(sp.rect.height);
                    string result = rule == null ? "No UI label — pick one" : EvaluateResult(haveW, haveH, requiredW, requiredH);

                    _rows.Add(new Row
                    {
                        Path = path,
                        Sprite = sp.name,
                        Label = uiLabel,
                        PixelsW = haveW,
                        PixelsH = haveH,
                        LogicalRefPxW = logicalW,
                        LogicalRefPxH = logicalH,
                        MaxDisplayPxW = displayMaxW,
                        MaxDisplayPxH = displayMaxH,
                        RequiredPxW = requiredW,
                        RequiredPxH = requiredH,
                        Result = result
                    });
                }
            }
            Repaint();
        }

        private void ExportCsv()
        {
            var path = EditorUtility.SaveFilePanel("Export UI Audit CSV", Directory.GetCurrentDirectory(), "ui_audit.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            using var sw = new StreamWriter(path);
            sw.WriteLine("Path,Sprite,Label,PixelsW,PixelsH,LogicalRefW,LogicalRefH,MaxDisplayW,MaxDisplayH,RequiredW,RequiredH,Result");
            foreach (var r in _rows)
                sw.WriteLine($"{CsvEscape(r.Path)},{CsvEscape(r.Sprite)},{CsvEscape(r.Label)},{r.PixelsW},{r.PixelsH},{r.LogicalRefPxW},{r.LogicalRefPxH},{r.MaxDisplayPxW},{r.MaxDisplayPxH},{r.RequiredPxW},{r.RequiredPxH},{CsvEscape(r.Result)}");
            sw.Flush();
            EditorUtility.RevealInFinder(path);
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private sealed class Row
        {
            public string Path, Sprite, Label, Result;
            public int PixelsW, PixelsH, LogicalRefPxW, LogicalRefPxH, MaxDisplayPxW, MaxDisplayPxH, RequiredPxW, RequiredPxH;
        }
    }
}
#endif
