#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UGUIImage = UnityEngine.UI.Image;
using UGUIRawImage = UnityEngine.UI.RawImage;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UIAudit.Runtime;

namespace UIAudit.Editor
{
    public sealed class UGUIUsageAuditWindow : EditorWindow
    {
        [Serializable]
        private sealed class Row
        {
            public string PrefabPath;
            public string ObjectPath;
            public string AssetName;
            public string TexturePath;
            public string SpriteName;
            public int AuthoredPxW;
            public int AuthoredPxH;
            public float DisplayRefW;
            public float DisplayRefH;
            public float DisplayTopW;
            public float DisplayTopH;
            public int RequiredPxW;
            public int RequiredPxH;
            public string Result;
            public ResultType Type;
            public Severity Severity;
            public float Impact;
            public int UsageCount;
            public int WasteKB;
            public string Note;
            public float LocalScale;
        }

        private enum ResultType { OK, Blurry, Heavy, Context }
        private enum Severity { NONE = 0, LOW = 1, MED = 2, HIGH = 3, CRITICAL = 5 }

        private Vector2 _scroll;
        private List<Row> _rows = new();
        private string _status = "";
        private string _filter = "";

        private UIElementSizingPolicy _policy;
        private bool _usePrefabScaler = false;

        private bool _showDetails = false;
        private VisualElement _detailsPanel;
        private TextField _dfPrefabObj, _dfAsset, _dfResult;

        private DefaultAsset _targetFolder = null;
        private GameObject _targetPrefab = null;

        private bool _estimateStretch = false;
        private int _assumedContainerW = 800;
        private int _assumedContainerH = 600;

        private bool _filterBlurry = true;
        private bool _filterHeavy = true;
        private Severity _minSeverity = Severity.LOW;
        private bool _sortByImpact = true;

        private bool _tintSeverityRows = true;
        private GUIStyle _sevLabelStyleLow, _sevLabelStyleMed, _sevLabelStyleHigh, _sevLabelStyleCrit, _sevLabelStyleNone;
        private GUIStyle _resultLabelRich;

        private MultiColumnListView _table;
        private List<Row> _view = new();

        private static string ColorToHtml(Color c)
        {
            Color32 c32 = c;
            return c32.r.ToString("X2") + c32.g.ToString("X2") + c32.b.ToString("X2");
        }

        private string ColorizeSeverity(string text, Severity sev)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string token = sev.ToString();
            string hex = ColorToHtml(SevColor(sev));
            return text.Replace(token, $"<b><color=#{hex}>{token}</color></b>");
        }

        private string FormatResultText(Row r)
        {
            string baseText = string.IsNullOrEmpty(r.Result) ? "OK" : r.Result;
            if (!string.IsNullOrEmpty(r.Note)) baseText += " · " + r.Note;
            baseText = ColorizeSeverity(baseText, r.Severity);

            if (r.Type == ResultType.OK || r.Type == ResultType.Context)
                return baseText;

            // Show delta for the worst dimension
            int deltaW = r.AuthoredPxW - r.RequiredPxW;
            int deltaH = r.AuthoredPxH - r.RequiredPxH;
            int delta = r.Type == ResultType.Blurry ? Mathf.Min(deltaW, deltaH) : Mathf.Max(deltaW, deltaH);
            string sign = delta > 0 ? "+" : "";
            string hex = ColorToHtml(SevColor(r.Severity));
            string deltaText = $" <b><color=#{hex}>Δ{sign}{delta} px</color></b>";
            return baseText + deltaText;
        }

        private static Color SevColor(Severity s)
        {
            switch (s)
            {
                case Severity.CRITICAL: return new Color(0.85f, 0.20f, 0.20f);
                case Severity.HIGH: return new Color(1.00f, 0.55f, 0.00f);
                case Severity.MED: return new Color(0.95f, 0.80f, 0.20f);
                case Severity.LOW: return new Color(0.40f, 0.70f, 0.30f);
                default: return new Color(0.60f, 0.60f, 0.60f);
            }
        }

        private GUIStyle SevStyle(Severity s)
        {
            if (_sevLabelStyleLow == null)
            {
                _sevLabelStyleLow = new GUIStyle(EditorStyles.label) { normal = { textColor = SevColor(Severity.LOW) } };
                _sevLabelStyleMed = new GUIStyle(EditorStyles.label) { normal = { textColor = SevColor(Severity.MED) } };
                _sevLabelStyleHigh = new GUIStyle(EditorStyles.label) { normal = { textColor = SevColor(Severity.HIGH) } };
                _sevLabelStyleCrit = new GUIStyle(EditorStyles.label) { normal = { textColor = SevColor(Severity.CRITICAL) } };
                _sevLabelStyleNone = new GUIStyle(EditorStyles.label) { normal = { textColor = SevColor(Severity.NONE) } };
            }
            switch (s)
            {
                case Severity.CRITICAL: return _sevLabelStyleCrit;
                case Severity.HIGH: return _sevLabelStyleHigh;
                case Severity.MED: return _sevLabelStyleMed;
                case Severity.LOW: return _sevLabelStyleLow;
                default: return _sevLabelStyleNone;
            }
        }

        [MenuItem("Tools/UI Audit/UGUI Usage Audit")]
        public static void Open() => GetWindow<UGUIUsageAuditWindow>("UGUI Usage Audit");

        private void OnEnable()
        {
            if (_policy == null)
            {
                var guids = AssetDatabase.FindAssets("t:UIElementSizingPolicy");
                if (guids.Length > 0)
                    _policy = AssetDatabase.LoadAssetAtPath<UIElementSizingPolicy>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
        }

        private void CreateGUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.flexGrow = 1f;

            var toolbarIMGUI = new IMGUIContainer(() => { DrawToolbar(); });
            toolbarIMGUI.style.flexShrink = 0;
            rootVisualElement.Add(toolbarIMGUI);

            _table = new MultiColumnListView();
            _table.style.flexGrow = 1f;
            _table.selectionType = SelectionType.Single;
            _table.fixedItemHeight = 18;
            _table.itemsSource = _view;

            _table.columns.Add(new Column { title = "Prefab / Object", width = 340, minWidth = 120, stretchable = true, resizable = true });
            _table.columns.Add(new Column { title = "Asset", width = 140, minWidth = 100, stretchable = true, resizable = true });
            _table.columns.Add(new Column { title = "Type", width = 60, minWidth = 50, resizable = true });
            _table.columns.Add(new Column { title = "Sev", width = 60, minWidth = 50, resizable = true });
            _table.columns.Add(new Column { title = "Impact", width = 60, minWidth = 50, resizable = true });
            _table.columns.Add(new Column { title = "Uses", width = 50, minWidth = 40, resizable = true });
            _table.columns.Add(new Column { title = "WasteKB", width = 70, minWidth = 60, resizable = true });
            _table.columns.Add(new Column { title = "Auth WxH", width = 80, minWidth = 70, resizable = true });
            _table.columns.Add(new Column { title = "Scale", width = 50, minWidth = 40, resizable = true });
            _table.columns.Add(new Column { title = "Disp@Ref", width = 80, minWidth = 70, resizable = true });
            _table.columns.Add(new Column { title = "Disp@Top", width = 80, minWidth = 70, resizable = true });
            _table.columns.Add(new Column { title = "Required", width = 80, minWidth = 70, resizable = true });
            _table.columns.Add(new Column { title = "Result", width = 220, minWidth = 120, stretchable = true, resizable = true });

            Func<Label> makeLabel = () => new Label { enableRichText = true, style = { unityTextAlign = TextAnchor.MiddleLeft, whiteSpace = WhiteSpace.NoWrap } };

            _table.columns[0].makeCell = () => makeLabel();
            _table.columns[0].bindCell = (ve, row) =>
            {
                var l = (Label)ve;
                if (row >= 0 && row < _view.Count)
                {
                    l.text = $"{_view[row].PrefabPath} → {_view[row].ObjectPath}";
                    l.tooltip = l.text;
                }
                else { l.text = string.Empty; l.tooltip = string.Empty; }
            };

            _table.columns[1].makeCell = () => makeLabel();
            _table.columns[1].bindCell = (ve, row) =>
            {
                var l = (Label)ve;
                if (row >= 0 && row < _view.Count) { l.text = _view[row].AssetName; l.tooltip = _view[row].TexturePath; }
                else { l.text = string.Empty; l.tooltip = string.Empty; }
            };

            _table.columns[2].makeCell = () => makeLabel();
            _table.columns[2].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? _view[row].Type.ToString() : string.Empty;

            _table.columns[3].makeCell = () => makeLabel();
            _table.columns[3].bindCell = (ve, row) =>
            {
                if (row < 0 || row >= _view.Count) { ((Label)ve).text = string.Empty; return; }
                var r = _view[row];
                var l = (Label)ve;
                l.text = r.Severity.ToString();
                l.style.color = new StyleColor(SevColor(r.Severity));
            };

            _table.columns[4].makeCell = () => makeLabel();
            _table.columns[4].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? _view[row].Impact.ToString("0.0") : string.Empty;

            _table.columns[5].makeCell = () => makeLabel();
            _table.columns[5].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? Mathf.Max(1, _view[row].UsageCount).ToString() : string.Empty;

            _table.columns[6].makeCell = () => makeLabel();
            _table.columns[6].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? _view[row].WasteKB.ToString() : string.Empty;

            _table.columns[7].makeCell = () => makeLabel();
            _table.columns[7].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? $"{_view[row].AuthoredPxW}x{_view[row].AuthoredPxH}" : string.Empty;

            _table.columns[8].makeCell = () => makeLabel();
            _table.columns[8].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? _view[row].LocalScale.ToString("0.00") : string.Empty;

            _table.columns[9].makeCell = () => makeLabel();
            _table.columns[9].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? $"{_view[row].DisplayRefW:0}x{_view[row].DisplayRefH:0}" : string.Empty;

            _table.columns[10].makeCell = () => makeLabel();
            _table.columns[10].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? $"{_view[row].DisplayTopW:0}x{_view[row].DisplayTopH:0}" : string.Empty;

            _table.columns[11].makeCell = () => makeLabel();
            _table.columns[11].bindCell = (ve, row) => ((Label)ve).text = row >= 0 && row < _view.Count ? $"{_view[row].RequiredPxW}x{_view[row].RequiredPxH}" : string.Empty;

            _table.columns[12].makeCell = () => makeLabel();
            _table.columns[12].bindCell = (ve, row) =>
            {
                var l = (Label)ve;
                if (row < 0 || row >= _view.Count) { l.text = string.Empty; l.tooltip = string.Empty; return; }
                var t = FormatResultText(_view[row]);
                l.text = t;
                l.tooltip = t;
            };

            _table.itemsChosen += OnItemsChosen;
            _table.selectionChanged += OnSelectionChanged;

            rootVisualElement.Add(_table);

            _detailsPanel = new VisualElement();
            _detailsPanel.style.flexShrink = 0;
            _detailsPanel.style.paddingLeft = 6;
            _detailsPanel.style.paddingRight = 6;
            _detailsPanel.style.paddingTop = 4;
            _detailsPanel.style.paddingBottom = 4;
            _detailsPanel.style.borderTopWidth = 1;
            _detailsPanel.style.borderTopColor = new Color(0, 0, 0, 0.2f);
            _detailsPanel.style.display = _showDetails ? DisplayStyle.Flex : DisplayStyle.None;

            var grid = new VisualElement { style = { flexDirection = FlexDirection.Column } };
            _dfPrefabObj = new TextField("Prefab / Object") { multiline = true, isReadOnly = true };
            _dfAsset = new TextField("Asset") { isReadOnly = true };
            _dfResult = new TextField("Result") { multiline = true, isReadOnly = true };
            grid.Add(_dfPrefabObj);
            grid.Add(_dfAsset);
            grid.Add(_dfResult);
            _detailsPanel.Add(grid);
            rootVisualElement.Add(_detailsPanel);

            RefreshTable();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent("UI Policy", "Sizing rules and top/reference resolution"), GUILayout.Width(70));
                    var newPolicy = (UIElementSizingPolicy)EditorGUILayout.ObjectField(_policy, typeof(UIElementSizingPolicy), false);
                    if (newPolicy != _policy) { _policy = newPolicy; }

                    _usePrefabScaler = EditorGUILayout.ToggleLeft(new GUIContent("Use Prefab CanvasScaler", "If enabled, reads each prefab's CanvasScaler reference & match."), _usePrefabScaler, GUILayout.Width(190));

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _targetFolder = (DefaultAsset)EditorGUILayout.ObjectField(new GUIContent("Folder", "Limit scan to this folder"), _targetFolder, typeof(DefaultAsset), false, GUILayout.Width(320));
                        _targetPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Prefab", "Scan only this prefab"), _targetPrefab, typeof(GameObject), false, GUILayout.Width(340));
                    }

                    GUILayout.FlexibleSpace();
                    _filter = EditorGUILayout.TextField(GUIContent.none, _filter, "SearchTextField");
                    if (GUILayout.Button("Scan Prefabs", GUILayout.Width(120))) ScanPrefabs();
                    if (GUILayout.Button("Export CSV", GUILayout.Width(100))) ExportCsv();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    _filterBlurry = EditorGUILayout.ToggleLeft(new GUIContent("Blurry", "Show Blurry results"), _filterBlurry, GUILayout.Width(70));
                    _filterHeavy = EditorGUILayout.ToggleLeft(new GUIContent("Heavy", "Show Heavy results"), _filterHeavy, GUILayout.Width(70));

                    EditorGUILayout.LabelField(new GUIContent("Min Sev"), GUILayout.Width(60));
                    var newMin = (Severity)EditorGUILayout.EnumPopup(_minSeverity, GUILayout.Width(120));
                    if (newMin != _minSeverity) { _minSeverity = newMin; RefreshTable(); Repaint(); }

                    if (GUILayout.Button(_sortByImpact ? "Sort: Impact" : "Sort: None", GUILayout.Width(110)))
                    {
                        _sortByImpact = !_sortByImpact;
                        AggregateAndSort();
                        RefreshTable();
                        Repaint();
                    }

                    _tintSeverityRows = EditorGUILayout.ToggleLeft(new GUIContent("Row strip", "Colored strip per row"), _tintSeverityRows, GUILayout.Width(90));
                    _showDetails = EditorGUILayout.ToggleLeft(new GUIContent("Details", "Show details panel"), _showDetails, GUILayout.Width(70));

                    GUILayout.FlexibleSpace();

                    _estimateStretch = EditorGUILayout.ToggleLeft(new GUIContent("Estimate Stretch", "Estimate stretched elements using assumed container"), _estimateStretch, GUILayout.Width(140));
                    EditorGUI.BeginDisabledGroup(!_estimateStretch);
                    EditorGUILayout.LabelField("W", GUILayout.Width(15));
                    _assumedContainerW = EditorGUILayout.IntField(_assumedContainerW, GUILayout.Width(70));
                    EditorGUILayout.LabelField("H", GUILayout.Width(15));
                    _assumedContainerH = EditorGUILayout.IntField(_assumedContainerH, GUILayout.Width(70));
                    EditorGUI.EndDisabledGroup();

                    if (GUI.changed) { RefreshTable(); }
                }

                if (_policy == null)
                {
                    EditorGUILayout.HelpBox("Assign a UIElementSizingPolicy asset to run the audit.", MessageType.Info);
                }
                EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);

                if (_detailsPanel != null)
                    _detailsPanel.style.display = _showDetails ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void RefreshTable()
        {
            _view = _rows.Where(PassesUiFilters).ToList();
            if (_table != null)
            {
                _table.itemsSource = _view;
                _table.Rebuild();
            }
        }

        private void ScanPrefabs()
        {
            _rows.Clear();
            _status = "Scanning...";
            if (_targetPrefab)
                _status = $"Scanning prefab: {AssetDatabase.GetAssetPath(_targetPrefab)}";
            else if (_targetFolder)
                _status = $"Scanning folder: {AssetDatabase.GetAssetPath(_targetFolder)}";
            Repaint();

            if (_policy == null)
            {
                _status = "No policy assigned.";
                Repaint();
                return;
            }

            int foundPrefabs = 0, foundUsages = 0;
            int processed = 0;

            var prefabPaths = GetTargetPrefabPaths();
            foreach (var prefabPath in prefabPaths)
            {
                var lower = prefabPath.ToLowerInvariant();
                if (!lower.StartsWith("assets/")) continue;
                if (lower.Contains("/packages/") || lower.Contains("/packagecache/") || lower.Contains("/editor/")) continue;

                GameObject root = null;
                Scene previewScene = default;
                bool usedPreview = false;
                try
                {
                    try
                    {
                        root = PrefabUtility.LoadPrefabContents(prefabPath);
                    }
                    catch (ArgumentException)
                    {
                        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        if (prefabAsset == null) continue;
                        previewScene = EditorSceneManager.NewPreviewScene();
                        root = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, previewScene);
                        usedPreview = true;
                    }
                    catch (Exception)
                    {
                        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        if (prefabAsset == null) continue;
                        previewScene = EditorSceneManager.NewPreviewScene();
                        root = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, previewScene);
                        usedPreview = true;
                    }

                    if (root == null) continue;

                    EnsureCanvasContext(root, out var canvas, out var scaler);
                    ForceLayout(canvas);

                    var images = root.GetComponentsInChildren<UGUIImage>(true);
                    var rawImages = root.GetComponentsInChildren<UGUIRawImage>(true);

                    foreach (var img in images)
                    {
                        if (img.sprite == null) continue;
                        var sp = img.sprite;
                        var tex = sp.texture;
                        string texPath = tex ? AssetDatabase.GetAssetPath(tex) : string.Empty;
                        int authoredW = Mathf.RoundToInt(sp.rect.width);
                        int authoredH = Mathf.RoundToInt(sp.rect.height);
                        if (authoredW <= 0 || authoredH <= 0) continue;

                        bool isNineSliced = img.type == UGUIImage.Type.Sliced || img.type == UGUIImage.Type.Tiled;
                        bool fullStretch = IsFullStretch(img.rectTransform);
                        bool hasFixedContainer = TryGetFixedContainer(img.rectTransform, canvas, out var fixedContPx);
                        bool contextStretch = fullStretch && !hasFixedContainer;

                        // Get local scale factor
                        float localScale = GetCumulativeScale(img.transform);

                        var (widthRef, heightRef) = GetPixelSize(canvas, img);
                        if (fullStretch && hasFixedContainer)
                        {
                            if (img.preserveAspect && img.sprite)
                            {
                                float aspect = img.sprite.rect.width / Mathf.Max(1f, img.sprite.rect.height);
                                widthRef = Mathf.Min(fixedContPx.x, fixedContPx.y * aspect);
                                heightRef = widthRef / aspect;
                            }
                            else
                            {
                                widthRef = fixedContPx.x;
                                heightRef = fixedContPx.y;
                            }
                        }
                        else if (contextStretch && _estimateStretch)
                        {
                            (widthRef, heightRef) = GetPixelSizeAssumed(img, _assumedContainerW, _assumedContainerH);
                        }

                        // Apply local scale
                        widthRef *= localScale;
                        heightRef *= localScale;

                        var ownerCanvas = GetOwningCanvas(img, canvas);
                        var ownerScaler = ownerCanvas ? ownerCanvas.GetComponent<CanvasScaler>() : scaler;
                        float scaleToTop = ComputeScaleToTop(ownerScaler);
                        float widthTop = widthRef * scaleToTop;
                        float heightTop = heightRef * scaleToTop;
                        int requiredW = Mathf.CeilToInt(widthTop * Mathf.Max(1f, _policy.oversample));
                        int requiredH = Mathf.CeilToInt(heightTop * Mathf.Max(1f, _policy.oversample));

                        var (type, sev, result, wasteKB) = Evaluate(authoredW, authoredH, requiredW, requiredH);

                        if (isNineSliced)
                        {
                            type = ResultType.Context;
                            sev = Severity.NONE;
                            result = "Nine-sliced/tiled — skip resolution check";
                            wasteKB = 0;
                        }
                        else if (contextStretch && !_estimateStretch)
                        {
                            type = ResultType.Context;
                            sev = Severity.NONE;
                            result = "Context-dependent (stretches to parent)";
                            wasteKB = 0;
                        }

                        _rows.Add(new Row
                        {
                            PrefabPath = prefabPath,
                            ObjectPath = GetHierarchyPath(img.transform, root.transform),
                            AssetName = sp.name,
                            TexturePath = texPath,
                            SpriteName = sp.name,
                            AuthoredPxW = authoredW,
                            AuthoredPxH = authoredH,
                            DisplayRefW = widthRef,
                            DisplayRefH = heightRef,
                            DisplayTopW = widthTop,
                            DisplayTopH = heightTop,
                            RequiredPxW = requiredW,
                            RequiredPxH = requiredH,
                            Result = result,
                            Type = type,
                            Severity = sev,
                            WasteKB = wasteKB,
                            LocalScale = localScale,
                            Note = isNineSliced ? "Sliced" :
                                   (fullStretch && hasFixedContainer ? $"Container {fixedContPx.x:0}x{fixedContPx.y:0}" :
                                    (contextStretch ? (_estimateStretch ? $"Stretch (assumed {_assumedContainerW}x{_assumedContainerH})" : "Stretch") :
                                     (localScale > 1.01f || localScale < 0.99f ? $"Scale {localScale:0.00}x" : string.Empty))),
                        });
                        foundUsages++;
                    }

                    foreach (var ri in rawImages)
                    {
                        var tex = ri.texture as Texture2D;
                        if (tex == null) continue;
                        string texPath = AssetDatabase.GetAssetPath(tex);
                        int authoredW = tex.width;
                        int authoredH = tex.height;
                        if (authoredW <= 0 || authoredH <= 0) continue;

                        bool fullStretch = IsFullStretch(ri.rectTransform);
                        bool hasFixedContainer = TryGetFixedContainer(ri.rectTransform, canvas, out var fixedContPx);
                        bool contextStretch = fullStretch && !hasFixedContainer;

                        float localScale = GetCumulativeScale(ri.transform);

                        var (widthRef, heightRef) = GetPixelSize(canvas, ri);
                        if (fullStretch && hasFixedContainer)
                        {
                            var arf = ri.GetComponent<AspectRatioFitter>();
                            if (arf != null)
                            {
                                float aspect = (ri.texture != null) ? (ri.texture.width / Mathf.Max(1f, (float)ri.texture.height)) : 1f;
                                switch (arf.aspectMode)
                                {
                                    case AspectRatioFitter.AspectMode.FitInParent:
                                        widthRef = Mathf.Min(fixedContPx.x, fixedContPx.y * aspect);
                                        heightRef = widthRef / aspect;
                                        break;
                                    case AspectRatioFitter.AspectMode.EnvelopeParent:
                                        widthRef = Mathf.Max(fixedContPx.x, fixedContPx.y * aspect);
                                        heightRef = widthRef / aspect;
                                        break;
                                    default:
                                        widthRef = fixedContPx.x;
                                        heightRef = fixedContPx.y;
                                        break;
                                }
                            }
                            else
                            {
                                widthRef = fixedContPx.x;
                                heightRef = fixedContPx.y;
                            }
                        }
                        else if (contextStretch && _estimateStretch)
                        {
                            (widthRef, heightRef) = GetPixelSizeAssumed(ri, _assumedContainerW, _assumedContainerH);
                        }

                        widthRef *= localScale;
                        heightRef *= localScale;

                        var ownerCanvas = GetOwningCanvas(ri, canvas);
                        var ownerScaler = ownerCanvas ? ownerCanvas.GetComponent<CanvasScaler>() : scaler;
                        float scaleToTop = ComputeScaleToTop(ownerScaler);
                        float widthTop = widthRef * scaleToTop;
                        float heightTop = heightRef * scaleToTop;
                        int requiredW = Mathf.CeilToInt(widthTop * Mathf.Max(1f, _policy.oversample));
                        int requiredH = Mathf.CeilToInt(heightTop * Mathf.Max(1f, _policy.oversample));

                        var (type, sev, result, wasteKB) = Evaluate(authoredW, authoredH, requiredW, requiredH);

                        if (contextStretch && !_estimateStretch)
                        {
                            type = ResultType.Context;
                            sev = Severity.NONE;
                            result = "Context-dependent (stretches to parent)";
                            wasteKB = 0;
                        }

                        _rows.Add(new Row
                        {
                            PrefabPath = prefabPath,
                            ObjectPath = GetHierarchyPath(ri.transform, root.transform),
                            AssetName = tex.name,
                            TexturePath = texPath,
                            SpriteName = string.Empty,
                            AuthoredPxW = authoredW,
                            AuthoredPxH = authoredH,
                            DisplayRefW = widthRef,
                            DisplayRefH = heightRef,
                            DisplayTopW = widthTop,
                            DisplayTopH = heightTop,
                            RequiredPxW = requiredW,
                            RequiredPxH = requiredH,
                            Result = result,
                            Type = type,
                            Severity = sev,
                            WasteKB = wasteKB,
                            LocalScale = localScale,
                            Note = (fullStretch && hasFixedContainer) ? $"Container {fixedContPx.x:0}x{fixedContPx.y:0}" :
                                   (contextStretch ? (_estimateStretch ? $"Stretch (assumed {_assumedContainerW}x{_assumedContainerH})" : "Stretch") :
                                    (localScale > 1.01f || localScale < 0.99f ? $"Scale {localScale:0.00}x" : string.Empty)),
                        });
                        foundUsages++;
                    }

                    processed++;
                    if ((processed % 50) == 0)
                    {
                        _status = $"Scanning… {processed} prefabs processed";
                        Repaint();
                    }

                    foundPrefabs++;
                }
                finally
                {
                    if (usedPreview)
                    {
                        if (root != null) DestroyImmediate(root);
                        if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
                    }
                    else
                    {
                        if (root != null) PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }

            AggregateAndSort();
            RefreshTable();
            _status = $"Scanned {foundPrefabs} prefabs. Usages: {foundUsages}.";
            Repaint();
        }

        /// <summary>
        /// Gets cumulative scale from transform hierarchy (relative to Canvas).
        /// </summary>
        private static float GetCumulativeScale(Transform t)
        {
            float scale = 1f;
            var cur = t;
            while (cur != null)
            {
                // Stop at Canvas level
                if (cur.GetComponent<Canvas>() != null) break;
                scale *= Mathf.Max(cur.localScale.x, cur.localScale.y);
                cur = cur.parent;
            }
            return Mathf.Max(0.001f, scale);
        }

        private void EnsureCanvasContext(GameObject prefabRootGO, out Canvas canvas, out CanvasScaler scaler)
        {
            canvas = null; scaler = null;
            if (prefabRootGO == null) return;

            canvas = prefabRootGO.GetComponentInChildren<Canvas>(true);
            if (canvas != null)
            {
                scaler = canvas.GetComponent<CanvasScaler>();
            }
            else
            {
                var tempCanvasGO = new GameObject("__UGUIAuditCanvas__");
                if (prefabRootGO.scene.IsValid())
                    SceneManager.MoveGameObjectToScene(tempCanvasGO, prefabRootGO.scene);

                canvas = tempCanvasGO.AddComponent<Canvas>();
                scaler = tempCanvasGO.AddComponent<CanvasScaler>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                prefabRootGO.transform.SetParent(tempCanvasGO.transform, false);
            }

            if (canvas != null && canvas.gameObject.name == "__UGUIAuditCanvas__")
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(_policy.referenceWidth, _policy.referenceHeight);
                scaler.matchWidthOrHeight = _policy.match;
            }

            var cRt = canvas.GetComponent<RectTransform>();
            if (cRt != null && canvas.gameObject.name == "__UGUIAuditCanvas__")
            {
                cRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _policy.referenceWidth);
                cRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _policy.referenceHeight);
            }
        }

        private static void ForceLayout(Canvas canvas)
        {
            var cRt = canvas.GetComponent<RectTransform>();
            LayoutRebuilder.ForceRebuildLayoutImmediate(cRt);
        }

        private (float w, float h) GetPixelSize(Canvas canvas, RectTransform rt)
        {
            var owner = GetOwningCanvas(rt, canvas);
            var r = RectTransformUtility.PixelAdjustRect(rt, owner);
            return (r.width, r.height);
        }

        private (float w, float h) GetPixelSize(Canvas canvas, UGUIImage img)
        {
            var rt = img.rectTransform;
            var owner = GetOwningCanvas(rt, canvas);
            var rectPx = RectTransformUtility.PixelAdjustRect(rt, owner);
            float w = rectPx.width;
            float h = rectPx.height;
            if (img.preserveAspect && img.sprite)
            {
                float aspect = img.sprite.rect.width / Mathf.Max(1f, img.sprite.rect.height);
                float fitW = Mathf.Min(w, h * aspect);
                float fitH = fitW / aspect;
                return (fitW, fitH);
            }
            return (w, h);
        }

        private (float w, float h) GetPixelSize(Canvas canvas, UGUIRawImage ri)
        {
            var rt = ri.rectTransform;
            var owner = GetOwningCanvas(rt, canvas);
            var rectPx = RectTransformUtility.PixelAdjustRect(rt, owner);
            float w = rectPx.width;
            float h = rectPx.height;
            var arf = ri.GetComponent<AspectRatioFitter>();
            if (arf != null)
            {
                float aspect = (ri.texture != null) ? (ri.texture.width / Mathf.Max(1f, (float)ri.texture.height)) : 1f;
                switch (arf.aspectMode)
                {
                    case AspectRatioFitter.AspectMode.FitInParent:
                        w = Mathf.Min(w, h * aspect);
                        h = w / aspect;
                        break;
                    case AspectRatioFitter.AspectMode.EnvelopeParent:
                        w = Mathf.Max(w, h * aspect);
                        h = w / aspect;
                        break;
                }
            }
            return (w, h);
        }

        private static bool IsFullStretch(RectTransform rt)
        {
            if (rt == null) return false;
            var a = rt.anchorMin; var b = rt.anchorMax;
            if (a.x != 0f || a.y != 0f || b.x != 1f || b.y != 1f) return false;
            bool offsetsZero = Vector2.SqrMagnitude(rt.offsetMin) < 0.001f && Vector2.SqrMagnitude(rt.offsetMax) < 0.001f;
            bool sizeZero = Vector2.SqrMagnitude(rt.sizeDelta) < 0.001f;
            return offsetsZero && sizeZero;
        }

        private static bool HasFixedSize(RectTransform rt)
        {
            if (rt == null) return false;
            bool nonStretchX = !(Mathf.Approximately(rt.anchorMin.x, 0f) && Mathf.Approximately(rt.anchorMax.x, 1f));
            bool nonStretchY = !(Mathf.Approximately(rt.anchorMin.y, 0f) && Mathf.Approximately(rt.anchorMax.y, 1f));
            if (nonStretchX || nonStretchY) return true;
            if (rt.sizeDelta.sqrMagnitude > 0.001f) return true;
            if (rt.offsetMin.sqrMagnitude > 0.001f || rt.offsetMax.sqrMagnitude > 0.001f) return true;
            return false;
        }

        private static bool TryGetFixedContainer(RectTransform rt, Canvas canvas, out Vector2 containerPx)
        {
            containerPx = Vector2.zero;
            Transform curT = rt ? rt.parent : null;
            while (curT != null)
            {
                var curRT = curT as RectTransform;
                if (curRT != null)
                {
                    var owner = GetOwningCanvas(curRT, canvas);
                    var ownerCanvasPx = CanvasPixelSize(owner);

                    var le = curRT.GetComponent<LayoutElement>();
                    if (le != null && (le.preferredWidth > 0f || le.preferredHeight > 0f))
                    {
                        var rLE = RectTransformUtility.PixelAdjustRect(curRT, owner);
                        containerPx = new Vector2(Mathf.Max(1, rLE.width), Mathf.Max(1, rLE.height));
                        return true;
                    }

                    var r = RectTransformUtility.PixelAdjustRect(curRT, owner);
                    var rPx = new Vector2(Mathf.Max(1, r.width), Mathf.Max(1, r.height));
                    bool smallerThanCanvas = (rPx.x < ownerCanvasPx.x - 1f) || (rPx.y < ownerCanvasPx.y - 1f);

                    if (smallerThanCanvas || HasFixedSize(curRT))
                    {
                        containerPx = rPx;
                        return true;
                    }
                }
                curT = curT.parent;
            }
            return false;
        }

        private static Vector2 CanvasPixelSize(Canvas canvas)
        {
            var rt = canvas ? canvas.GetComponent<RectTransform>() : null;
            if (!rt) return Vector2.zero;
            var r = RectTransformUtility.PixelAdjustRect(rt, canvas);
            return new Vector2(Mathf.Max(1, r.width), Mathf.Max(1, r.height));
        }

        private float ComputeScaleToTop(CanvasScaler scaler)
        {
            float refW = (scaler && scaler.referenceResolution.x > 0f) ? scaler.referenceResolution.x : _policy.referenceWidth;
            float refH = (scaler && scaler.referenceResolution.y > 0f) ? scaler.referenceResolution.y : _policy.referenceHeight;
            float match = scaler ? scaler.matchWidthOrHeight : _policy.match;
            float sW = _policy.topWidth / Mathf.Max(1f, refW);
            float sH = _policy.topHeight / Mathf.Max(1f, refH);
            return Mathf.Lerp(sW, sH, Mathf.Clamp01(match));
        }

        private static string GetHierarchyPath(Transform t, Transform root)
        {
            var stack = new Stack<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                stack.Push(cur.name);
                cur = cur.parent;
            }
            if (root != null) stack.Push(root.name);
            return string.Join("/", stack);
        }

        private static void PingPath(string path)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj) EditorGUIUtility.PingObject(obj);
        }

        private (ResultType type, Severity sev, string result, int wasteKB) Evaluate(int authW, int authH, int reqW, int reqH)
        {
            // Use worst-case ratio for blurry detection
            float ratioW = reqW > 0 ? authW / (float)reqW : 1f;
            float ratioH = reqH > 0 ? authH / (float)reqH : 1f;
            float worstRatio = Mathf.Min(ratioW, ratioH);

            if (worstRatio < 1f - 0.001f)
            {
                var sev = worstRatio < 0.5f ? Severity.CRITICAL : worstRatio < 0.75f ? Severity.HIGH : worstRatio < 0.9f ? Severity.MED : Severity.LOW;
                string dim = ratioW < ratioH ? "W" : "H";
                int have = ratioW < ratioH ? authW : authH;
                int need = ratioW < ratioH ? reqW : reqH;
                string msg = $"Blurry {sev} ({dim}: have {have}px, need {need}px)";
                return (ResultType.Blurry, sev, msg, 0);
            }

            // Heavy calculation using area
            long authoredArea = (long)authW * authH;
            long requiredArea = (long)reqW * reqH;

            float heavyRatioW = reqW > 0 ? authW / (float)reqW : 1f;
            float heavyRatioH = reqH > 0 ? authH / (float)reqH : 1f;
            float heavyX = Mathf.Max(heavyRatioW, heavyRatioH);

            var heavyThresholdW = Mathf.CeilToInt(reqW * _policy.oversizePixelFactor);
            var heavyThresholdH = Mathf.CeilToInt(reqH * _policy.oversizePixelFactor);

            if (authW > heavyThresholdW || authH > heavyThresholdH)
            {
                var sev = heavyX >= 10 ? Severity.CRITICAL : heavyX >= 5 ? Severity.HIGH : heavyX >= 3 ? Severity.MED : Severity.LOW;
                long wasteArea = Mathf.Max(0, (int)(authoredArea - requiredArea));
                long authoredKB = (authoredArea * 4L) / 1024L;
                int wasteKB = (int)Mathf.Min(authoredKB, (wasteArea * 4L) / 1024f);
                string msg = $"Heavy {sev} x{heavyX:0.0} (need {reqW}x{reqH}px)";
                return (ResultType.Heavy, sev, msg, Mathf.Max(0, wasteKB));
            }

            return (ResultType.OK, Severity.NONE, string.Empty, 0);
        }

        private void AggregateAndSort()
        {
            var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _rows)
            {
                string key = (r.TexturePath ?? string.Empty) + "::" + (r.SpriteName ?? string.Empty);
                if (!usage.ContainsKey(key)) usage[key] = 0;
                usage[key]++;
            }
            foreach (var r in _rows)
            {
                string key = (r.TexturePath ?? string.Empty) + "::" + (r.SpriteName ?? string.Empty);
                r.UsageCount = usage.TryGetValue(key, out var c) ? Mathf.Max(1, c) : 1;
                float sevW = Mathf.Max(0f, (float)r.Severity);
                float areaFactor = Mathf.Log(Mathf.Max(1f, r.DisplayTopW) / 48f + 1f, 2f);
                r.Impact = (r.Type == ResultType.OK || r.Type == ResultType.Context) ? 0f : sevW * r.UsageCount * areaFactor;
            }
            if (_sortByImpact)
            {
                _rows = _rows
                    .OrderByDescending(r => r.Impact)
                    .ThenByDescending(r => r.Severity)
                    .ThenByDescending(r => r.DisplayTopW)
                    .ToList();
            }
            else
            {
                _rows = _rows
                    .OrderBy(r => r.PrefabPath)
                    .ThenBy(r => r.ObjectPath)
                    .ToList();
            }
        }

        private bool PassesUiFilters(Row r)
        {
            if (!string.IsNullOrEmpty(_filter))
            {
                if (r.PrefabPath.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.AssetName.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.ObjectPath.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            if (!_filterBlurry && r.Type == ResultType.Blurry) return false;
            if (!_filterHeavy && r.Type == ResultType.Heavy) return false;
            if (r.Severity < _minSeverity) return false;
            return true;
        }

        private (float w, float h) GetPixelSizeAssumed(UGUIImage img, int containerW, int containerH)
        {
            float w = Mathf.Max(1, containerW);
            float h = Mathf.Max(1, containerH);
            if (img != null && img.preserveAspect && img.sprite)
            {
                float aspect = img.sprite.rect.width / Mathf.Max(1f, img.sprite.rect.height);
                w = Mathf.Min(w, h * aspect);
                h = w / aspect;
            }
            return (w, h);
        }

        private (float w, float h) GetPixelSizeAssumed(UGUIRawImage ri, int containerW, int containerH)
        {
            float w = Mathf.Max(1, containerW);
            float h = Mathf.Max(1, containerH);
            var arf = ri ? ri.GetComponent<AspectRatioFitter>() : null;
            if (arf != null)
            {
                float aspect = (ri.texture != null) ? (ri.texture.width / Mathf.Max(1f, (float)ri.texture.height)) : 1f;
                switch (arf.aspectMode)
                {
                    case AspectRatioFitter.AspectMode.FitInParent:
                        w = Mathf.Min(w, h * aspect);
                        h = w / aspect;
                        break;
                    case AspectRatioFitter.AspectMode.EnvelopeParent:
                        w = Mathf.Max(w, h * aspect);
                        h = w / aspect;
                        break;
                }
            }
            return (w, h);
        }

        private List<string> GetTargetPrefabPaths()
        {
            var results = new List<string>();
            if (_targetPrefab != null)
            {
                var p = AssetDatabase.GetAssetPath(_targetPrefab);
                if (!string.IsNullOrEmpty(p)) results.Add(p);
                return results;
            }
            if (_targetFolder != null)
            {
                var folderPath = AssetDatabase.GetAssetPath(_targetFolder);
                if (!string.IsNullOrEmpty(folderPath) && AssetDatabase.IsValidFolder(folderPath))
                {
                    var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });
                    foreach (var g in guids)
                    {
                        var p = AssetDatabase.GUIDToAssetPath(g);
                        if (!string.IsNullOrEmpty(p)) results.Add(p);
                    }
                    return results;
                }
            }
            var all = AssetDatabase.FindAssets("t:Prefab");
            foreach (var g in all)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p)) results.Add(p);
            }
            return results;
        }

        private void ExportCsv()
        {
            var path = EditorUtility.SaveFilePanel("Export UGUI Audit CSV", Directory.GetCurrentDirectory(), "ugui_audit.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            using var sw = new StreamWriter(path);
            sw.WriteLine("Prefab,Object,Asset,TexturePath,SpriteName,Type,Severity,Impact,Usages,WasteKB,AuthoredW,AuthoredH,LocalScale,DispRefW,DispRefH,DispTopW,DispTopH,RequiredW,RequiredH,Result");
            foreach (var r in _rows)
                sw.WriteLine($"{CsvEscape(r.PrefabPath)},{CsvEscape(r.ObjectPath)},{CsvEscape(r.AssetName)},{CsvEscape(r.TexturePath)},{CsvEscape(r.SpriteName)},{r.Type},{r.Severity},{r.Impact:0.0},{r.UsageCount},{r.WasteKB},{r.AuthoredPxW},{r.AuthoredPxH},{r.LocalScale:0.00},{r.DisplayRefW:0},{r.DisplayRefH:0},{r.DisplayTopW:0},{r.DisplayTopH:0},{r.RequiredPxW},{r.RequiredPxH},{CsvEscape(r.Result)}");
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

        private static Canvas GetOwningCanvas(Component c, Canvas fallback)
        {
            if (c == null) return fallback;
            var t = c.transform;
            while (t != null)
            {
                var cv = t.GetComponent<Canvas>();
                if (cv != null) return cv;
                t = t.parent;
            }
            return fallback;
        }

        private void OnItemsChosen(IEnumerable<object> objs)
        {
            var idx = _table.selectedIndex;
            if (idx >= 0 && idx < _view.Count)
            {
                PingPath(_view[idx].PrefabPath);
                OnSelectionChanged(null);
            }
        }

        private void OnSelectionChanged(IEnumerable<object> objs)
        {
            if (_table == null || _view == null) return;
            var idx = _table.selectedIndex;
            if (idx < 0 || idx >= _view.Count) return;
            var r = _view[idx];
            _dfPrefabObj?.SetValueWithoutNotify($"{r.PrefabPath} → {r.ObjectPath}");
            _dfAsset?.SetValueWithoutNotify(r.AssetName);
            _dfResult?.SetValueWithoutNotify(FormatResultText(r));
        }
    }
}
#endif
