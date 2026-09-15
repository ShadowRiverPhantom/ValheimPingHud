using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimPingHud
{
    internal enum HudAnchorMode
    {
        BelowMinimap,
        AboveMinimap,
        TopRight,
        TopLeft,
        BottomRight,
        BottomLeft
    }

    /// <summary>
    /// The HUD panel itself. Built exactly like the DayTimeCountdown panel
    /// (same parent, same sprite, same font/outline treatment) so both look
    /// like one family, but anchored differently so they never overlap:
    /// DayTimeCountdown defaults to the slot ABOVE the minimap, this panel
    /// defaults to the slot BELOW it, and it additionally measures any panel
    /// listed in "Avoid panel names" and slides itself out of the way.
    /// </summary>
    internal sealed class PingHudPanel
    {
        private const float RowHeight = 22f;
        private const float VerticalPadding = 4f;
        private const float CornerMargin = 8f;
        private const float MinimapGap = 6f;

        // Fallback minimap geometry, measured from the DayTimeCountdown layout:
        // its panel sits at x = -140 with y = -25 (above) / -255 (below).
        private const float FallbackMinimapCenterX = -140f;
        private const float FallbackMinimapTop = -40f;
        private const float FallbackMinimapBottom = -240f;

        private const string PanelObjectName = "PingHudPanel";

        private static readonly Color GoodColor = new Color(0.45f, 1f, 0.45f, 1f);
        private static readonly Color WarnColor = new Color(1f, 0.86f, 0.35f, 1f);
        private static readonly Color BadColor = new Color(1f, 0.36f, 0.32f, 1f);

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly Image _background;
        private readonly Text[] _cells = new Text[6];
        private readonly Outline[] _outlines = new Outline[6];

        private readonly Vector3[] _cornerBuffer = new Vector3[4];
        private readonly List<RectTransform> _avoidTargets = new List<RectTransform>();

        private static FieldInfo _smallRootField;
        private static FieldInfo _mapSmallField;
        private static bool _minimapFieldsResolved;

        public PingHudPanel(Transform parent)
        {
            _root = new GameObject(PanelObjectName);
            _root.layer = 5;
            _root.transform.SetParent(parent, false);

            _rect = _root.AddComponent<RectTransform>();
            _rect.anchorMin = new Vector2(1f, 1f);
            _rect.anchorMax = new Vector2(1f, 1f);
            _rect.pivot = new Vector2(0.5f, 0.5f);
            _rect.anchoredPosition = new Vector2(FallbackMinimapCenterX, -260f);

            _background = _root.AddComponent<Image>();
            _background.sprite = PanelSprite.Get();
            _background.type = Image.Type.Simple;
            _background.raycastTarget = false;

            for (int i = 0; i < _cells.Length; i++)
            {
                _cells[i] = CreateText(_root.transform, "Cell" + i, out _outlines[i]);
            }
        }

        public bool Alive
        {
            get { return _root != null; }
        }

        public void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible)
            {
                _root.SetActive(visible);
            }
        }

        public void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
        }

        // ------------------------------------------------------------------
        // refresh
        // ------------------------------------------------------------------

        public void Refresh(PingHudPlugin plugin, NetStatsSampler stats)
        {
            if (_root == null || !_root.activeInHierarchy)
            {
                return;
            }

            HudStrings strings = plugin.Strings;
            bool online = stats.HasConnection;
            bool details = online && plugin.CfgShowDetails.Value;

            // Each extra row only appears when something in it can actually be shown:
            // a LAN / direct-IP (TCP) connection has no Steam loss data but still has
            // latency (RPC probe) and byte rates.
            bool jitterRow = details && (stats.HasPing || stats.HasLoss);
            bool rateRow = details && stats.HasBandwidth;
            int rows = 1 + (jitterRow ? 1 : 0) + (rateRow ? 1 : 0);

            float width = Mathf.Max(80f, plugin.CfgPanelWidth.Value);
            float height = Mathf.Max(rows * RowHeight + VerticalPadding * 2f, plugin.CfgPanelHeight.Value);
            float padding = Mathf.Clamp(plugin.CfgPadding.Value, 0f, width * 0.25f);
            float firstRowY = (rows - 1) * RowHeight * 0.5f;

            _rect.sizeDelta = new Vector2(width, height);

            _background.enabled = plugin.CfgBackgroundEnabled.Value;
            _background.color = plugin.CfgBackgroundColor.Value;

            ApplyTypography(plugin, strings);
            FillRows(plugin, stats, strings, online, jitterRow, rateRow, firstRowY, width, padding);

            ApplyPosition(plugin, width, height);
        }

        private void ApplyTypography(PingHudPlugin plugin, HudStrings strings)
        {
            Font font = FontProvider.Get(plugin.CfgFontName.Value, plugin.CfgFontSize.Value,
                strings.Language == HudLanguage.Chinese);
            int size = Mathf.Clamp(plugin.CfgFontSize.Value, 6, 72);
            bool outlineOn = plugin.CfgOutlineEnabled.Value;
            Color outlineColor = plugin.CfgOutlineColor.Value;

            for (int i = 0; i < _cells.Length; i++)
            {
                Text cell = _cells[i];
                if (cell == null)
                {
                    continue;
                }

                if (cell.font != font)
                {
                    cell.font = font;
                }

                if (cell.fontSize != size)
                {
                    cell.fontSize = size;
                }

                Outline outline = _outlines[i];
                if (outline != null)
                {
                    outline.enabled = outlineOn;
                    outline.effectColor = outlineColor;
                }
            }
        }

        private void FillRows(PingHudPlugin plugin, NetStatsSampler stats, HudStrings strings,
            bool online, bool jitterRow, bool rateRow, float firstRowY, float width, float padding)
        {
            Color baseColor = plugin.CfgFontColor.Value;
            bool showLabels = plugin.CfgShowLabels.Value;
            float halfCell = width * 0.5f - padding;
            float halfOffset = width * 0.25f - padding * 0.5f;
            float fullCell = width - padding * 2f;

            // Markers: "--" = not measured yet, "N/A" = this transport cannot report it.
            Color dimColor = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * 0.6f);
            string pingMissing = strings.UnknownValue;
            string lossMissing = stats.LossSupported ? strings.UnknownValue : strings.NotAvailableValue;

            if (!online)
            {
                // Single centred "not connected" note.
                ShowCell(0, strings.OfflineLabel, TextAnchor.MiddleCenter, fullCell, 0f, 0f, dimColor);
                HideCell(1); HideCell(2); HideCell(3); HideCell(4); HideCell(5);
                return;
            }

            bool showPing = plugin.CfgShowPing.Value;
            bool showLoss = plugin.CfgShowLoss.Value;
            if (!showPing && !showLoss)
            {
                showPing = true;
            }

            bool pingLeft = !plugin.CfgReverseText.Value;
            bool both = showPing && showLoss;

            float pingValue = plugin.CfgSmooth.Value ? stats.PingSmooth : stats.PingRaw;
            float lossValue = plugin.CfgSmooth.Value ? stats.LossSmooth : stats.LossPercent;

            string pingText = strings.Labeled(strings.PingLabel,
                stats.HasPing ? strings.FormatPing(pingValue) : pingMissing, showLabels);
            string lossText = strings.Labeled(strings.LossLabel,
                stats.HasLoss ? strings.FormatLoss(lossValue) : lossMissing, showLabels);

            Color pingColor = stats.HasPing && plugin.CfgColorize.Value
                ? Grade(pingValue, plugin.CfgPingGood.Value, plugin.CfgPingBad.Value, baseColor)
                : (stats.HasPing ? baseColor : dimColor);
            Color lossColor = stats.HasLoss && plugin.CfgColorize.Value
                ? Grade(lossValue, plugin.CfgLossGood.Value, plugin.CfgLossBad.Value, baseColor)
                : (stats.HasLoss ? baseColor : dimColor);

            if (!both)
            {
                // Only one metric enabled: give it the whole row, centred.
                if (showPing)
                {
                    ShowCell(0, pingText, TextAnchor.MiddleCenter, fullCell, 0f, firstRowY, pingColor);
                }
                else
                {
                    ShowCell(0, lossText, TextAnchor.MiddleCenter, fullCell, 0f, firstRowY, lossColor);
                }

                HideCell(1);
            }
            else if (pingLeft)
            {
                ShowCell(0, pingText, TextAnchor.MiddleLeft, halfCell, -halfOffset, firstRowY, pingColor);
                ShowCell(1, lossText, TextAnchor.MiddleRight, halfCell, halfOffset, firstRowY, lossColor);
            }
            else
            {
                ShowCell(0, lossText, TextAnchor.MiddleLeft, halfCell, -halfOffset, firstRowY, lossColor);
                ShowCell(1, pingText, TextAnchor.MiddleRight, halfCell, halfOffset, firstRowY, pingColor);
            }

            float nextRowY = firstRowY - RowHeight;

            if (jitterRow)
            {
                Color jitterColor = stats.HasPing && plugin.CfgColorize.Value
                    ? Grade(stats.JitterMs, plugin.CfgPingGood.Value * 0.5f, plugin.CfgPingBad.Value * 0.5f, baseColor)
                    : (stats.HasPing ? baseColor : dimColor);

                float lossFromQuality = Mathf.Clamp01(1f - stats.QualityLocal) * 100f;
                Color qualityColor = stats.HasLoss && plugin.CfgColorize.Value
                    ? Grade(lossFromQuality, plugin.CfgLossGood.Value, plugin.CfgLossBad.Value, baseColor)
                    : (stats.HasLoss ? baseColor : dimColor);

                ShowCell(2, strings.Labeled(strings.JitterLabel,
                        stats.HasPing ? strings.FormatJitter(stats.JitterMs) : strings.UnknownValue, showLabels),
                    TextAnchor.MiddleLeft, halfCell, -halfOffset, nextRowY, jitterColor);
                ShowCell(3, strings.Labeled(strings.QualityLabel,
                        stats.HasLoss ? strings.FormatQuality(stats.QualityLocal) : lossMissing, showLabels),
                    TextAnchor.MiddleRight, halfCell, halfOffset, nextRowY, qualityColor);

                nextRowY -= RowHeight;
            }
            else
            {
                HideCell(2);
                HideCell(3);
            }

            if (rateRow)
            {
                ShowCell(4, "\u2193 " + strings.FormatRate(stats.OutBytesPerSec),
                    TextAnchor.MiddleLeft, halfCell, -halfOffset, nextRowY, baseColor);
                ShowCell(5, "\u2191 " + strings.FormatRate(stats.InBytesPerSec),
                    TextAnchor.MiddleRight, halfCell, halfOffset, nextRowY, baseColor);
            }
            else
            {
                HideCell(4);
                HideCell(5);
            }
        }

        private void ShowCell(int index, string text, TextAnchor anchor, float width, float x, float y, Color color)
        {
            Text cell = _cells[index];
            if (cell == null)
            {
                return;
            }

            if (!cell.gameObject.activeSelf)
            {
                cell.gameObject.SetActive(true);
            }

            if (cell.text != text)
            {
                cell.text = text;
            }

            cell.alignment = anchor;
            cell.color = color;

            RectTransform rect = cell.rectTransform;
            Vector2 size = new Vector2(width, RowHeight);
            if (rect.sizeDelta != size)
            {
                rect.sizeDelta = size;
            }

            Vector2 position = new Vector2(x, y);
            if (rect.anchoredPosition != position)
            {
                rect.anchoredPosition = position;
            }
        }

        private void HideCell(int index)
        {
            Text cell = _cells[index];
            if (cell != null && cell.gameObject.activeSelf)
            {
                cell.gameObject.SetActive(false);
            }
        }

        // ------------------------------------------------------------------
        // positioning
        // ------------------------------------------------------------------

        private void ApplyPosition(PingHudPlugin plugin, float width, float height)
        {
            HudAnchorMode mode = ParseMode(plugin.CfgPosition.Value);
            float offsetX = plugin.CfgOffsetX.Value;
            float offsetY = plugin.CfgOffsetY.Value;

            float mapCenterX = FallbackMinimapCenterX;
            float mapTop = FallbackMinimapTop;
            float mapBottom = FallbackMinimapBottom;
            TryGetMinimapBounds(_rect.parent, out mapCenterX, out mapTop, out mapBottom);

            Vector2 anchor;
            Vector2 position;
            int pushDirection;

            switch (mode)
            {
                case HudAnchorMode.AboveMinimap:
                    anchor = new Vector2(1f, 1f);
                    position = new Vector2(mapCenterX + offsetX, mapTop + MinimapGap + height * 0.5f + offsetY);
                    pushDirection = 1;
                    break;

                case HudAnchorMode.TopRight:
                    anchor = new Vector2(1f, 1f);
                    position = new Vector2(-(width * 0.5f + CornerMargin) + offsetX,
                        -(height * 0.5f + CornerMargin) + offsetY);
                    pushDirection = 1;
                    break;

                case HudAnchorMode.TopLeft:
                    anchor = new Vector2(0f, 1f);
                    position = new Vector2(width * 0.5f + CornerMargin + offsetX,
                        -(height * 0.5f + CornerMargin) + offsetY);
                    pushDirection = 1;
                    break;

                case HudAnchorMode.BottomRight:
                    anchor = new Vector2(1f, 0f);
                    position = new Vector2(-(width * 0.5f + CornerMargin) + offsetX,
                        height * 0.5f + CornerMargin + offsetY);
                    pushDirection = -1;
                    break;

                case HudAnchorMode.BottomLeft:
                    anchor = new Vector2(0f, 0f);
                    position = new Vector2(width * 0.5f + CornerMargin + offsetX,
                        height * 0.5f + CornerMargin + offsetY);
                    pushDirection = -1;
                    break;

                default: // BelowMinimap
                    anchor = new Vector2(1f, 1f);
                    position = new Vector2(mapCenterX + offsetX, mapBottom - MinimapGap - height * 0.5f + offsetY);
                    pushDirection = -1;
                    break;
            }

            if (_rect.anchorMin != anchor || _rect.anchorMax != anchor)
            {
                _rect.anchorMin = anchor;
                _rect.anchorMax = anchor;
            }

            // There are only ~40px between the top of the screen and the minimap, so a
            // panel with the extra detail rows does not fit above it. Keep it on screen
            // by dropping it below the minimap instead.
            if (mode == HudAnchorMode.AboveMinimap && mapTop < 0f && height + MinimapGap > -mapTop)
            {
                pushDirection = -1;
                position = new Vector2(mapCenterX + offsetX, mapBottom - MinimapGap - height * 0.5f + offsetY);
            }

            float y = ResolveOverlapY(plugin, position, pushDirection);

            // Never let the panel leave the screen along its anchor axis.
            y = anchor.y >= 0.5f
                ? Mathf.Min(y, -(height * 0.5f + 2f))
                : Mathf.Max(y, height * 0.5f + 2f);

            _rect.anchoredPosition = new Vector2(position.x, y);
        }

        /// <summary>
        /// Slides the panel along the push axis until it clears every panel listed in
        /// "Avoid panel names" (DayTimeCountdown's panel is called "DayTimePanel").
        /// Runs on a copy of the base position every refresh, so it can never drift.
        /// </summary>
        private float ResolveOverlapY(PingHudPlugin plugin, Vector2 basePosition, int pushDirection)
        {
            float y = basePosition.y;

            if (!plugin.CfgAutoAvoid.Value)
            {
                _rect.anchoredPosition = new Vector2(basePosition.x, y);
                return y;
            }

            Transform parent = _rect.parent;
            CollectAvoidTargets(plugin, parent);
            if (parent == null || _avoidTargets.Count == 0)
            {
                _rect.anchoredPosition = new Vector2(basePosition.x, y);
                return y;
            }

            float gap = Mathf.Max(0f, plugin.CfgAvoidGap.Value);

            for (int pass = 0; pass < 3; pass++)
            {
                _rect.anchoredPosition = new Vector2(basePosition.x, y);

                float myMinX, myMaxX, myMinY, myMaxY;
                if (!TryGetParentLocalRect(_rect, parent, _cornerBuffer, out myMinX, out myMaxX, out myMinY, out myMaxY))
                {
                    break;
                }

                float shift = 0f;
                for (int i = 0; i < _avoidTargets.Count; i++)
                {
                    float tMinX, tMaxX, tMinY, tMaxY;
                    if (!TryGetParentLocalRect(_avoidTargets[i], parent, _cornerBuffer,
                        out tMinX, out tMaxX, out tMinY, out tMaxY))
                    {
                        continue;
                    }

                    if (myMinX >= tMaxX || myMaxX <= tMinX)
                    {
                        continue; // different column, cannot collide
                    }

                    if (pushDirection < 0)
                    {
                        float requirement = tMinY - gap;      // our top edge must stay below this
                        if (myMaxY > requirement)
                        {
                            shift = Mathf.Max(shift, myMaxY - requirement);
                        }
                    }
                    else
                    {
                        float requirement = tMaxY + gap;      // our bottom edge must stay above this
                        if (myMinY < requirement)
                        {
                            shift = Mathf.Max(shift, requirement - myMinY);
                        }
                    }
                }

                if (shift <= 0.01f)
                {
                    break;
                }

                y += pushDirection * shift;
            }

            _rect.anchoredPosition = new Vector2(basePosition.x, y);
            return y;
        }

        private void CollectAvoidTargets(PingHudPlugin plugin, Transform parent)
        {
            _avoidTargets.Clear();
            if (parent == null)
            {
                return;
            }

            string raw = plugin.CfgAvoidPanelNames.Value;
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            string[] names = raw.Split(',');
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i].Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                Transform found = parent.Find(name);
                if (found == null || found == _root.transform)
                {
                    continue;
                }

                RectTransform rect = found as RectTransform;
                if (rect == null)
                {
                    rect = found.GetComponent<RectTransform>();
                }

                if (rect != null && rect.gameObject.activeInHierarchy)
                {
                    _avoidTargets.Add(rect);
                }
            }
        }

        private static bool TryGetParentLocalRect(RectTransform rect, Transform parent, Vector3[] buffer,
            out float minX, out float maxX, out float minY, out float maxY)
        {
            minX = 0f; maxX = 0f; minY = 0f; maxY = 0f;
            if (rect == null || parent == null || buffer == null || !rect.gameObject.activeInHierarchy)
            {
                return false;
            }

            rect.GetWorldCorners(buffer);

            Vector3 bottomLeft = parent.InverseTransformPoint(buffer[0]);
            Vector3 topRight = parent.InverseTransformPoint(buffer[2]);

            minX = Mathf.Min(bottomLeft.x, topRight.x);
            maxX = Mathf.Max(bottomLeft.x, topRight.x);
            minY = Mathf.Min(bottomLeft.y, topRight.y);
            maxY = Mathf.Max(bottomLeft.y, topRight.y);
            return true;
        }

        /// <summary>
        /// Measures the real minimap rect so the panel keeps hugging it at any HUD scale.
        /// Falls back to the constants derived from the DayTimeCountdown layout when the
        /// measurement does not look like a minimap.
        /// </summary>
        private void TryGetMinimapBounds(Transform parent, out float centerX, out float top, out float bottom)
        {
            centerX = FallbackMinimapCenterX;
            top = FallbackMinimapTop;
            bottom = FallbackMinimapBottom;

            if (parent == null)
            {
                return;
            }

            try
            {
                Minimap minimap = Minimap.instance;
                if (minimap == null)
                {
                    return;
                }

                ResolveMinimapFields();
                Transform target = null;

                if (_smallRootField != null)
                {
                    GameObject smallRoot = _smallRootField.GetValue(minimap) as GameObject;
                    if (smallRoot != null)
                    {
                        target = smallRoot.transform;
                    }
                }

                if (target == null && _mapSmallField != null)
                {
                    GameObject mapSmall = _mapSmallField.GetValue(minimap) as GameObject;
                    if (mapSmall != null)
                    {
                        target = mapSmall.transform;
                    }
                }

                if (target == null)
                {
                    return;
                }

                RectTransform rect = target as RectTransform;
                if (rect == null)
                {
                    rect = target.GetComponent<RectTransform>();
                }

                if (rect == null)
                {
                    return;
                }

                float minX, maxX, minY, maxY;
                if (!TryGetParentLocalRect(rect, parent, _cornerBuffer, out minX, out maxX, out minY, out maxY))
                {
                    return;
                }

                float width = maxX - minX;
                float height = maxY - minY;

                // Sanity check: only accept something that looks like the HUD minimap.
                bool plausible = width >= 80f && width <= 420f &&
                                 height >= 80f && height <= 420f &&
                                 maxX < 0f && maxY < 0f;
                if (!plausible)
                {
                    return;
                }

                centerX = (minX + maxX) * 0.5f;
                top = maxY;
                bottom = minY;
            }
            catch (Exception)
            {
                // Keep the fallback geometry.
            }
        }

        private static void ResolveMinimapFields()
        {
            if (_minimapFieldsResolved)
            {
                return;
            }

            _minimapFieldsResolved = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _smallRootField = typeof(Minimap).GetField("m_smallRoot", flags);
            _mapSmallField = typeof(Minimap).GetField("m_mapSmall", flags);
        }

        public static HudAnchorMode ParseMode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return HudAnchorMode.BelowMinimap;
            }

            string normalized = value.Trim().Replace(" ", string.Empty).Replace("_", string.Empty);
            switch (normalized.ToLowerInvariant())
            {
                case "aboveminimap":
                case "top":
                case "above":
                    return HudAnchorMode.AboveMinimap;
                case "topright":
                    return HudAnchorMode.TopRight;
                case "topleft":
                    return HudAnchorMode.TopLeft;
                case "bottomright":
                    return HudAnchorMode.BottomRight;
                case "bottomleft":
                    return HudAnchorMode.BottomLeft;
                case "belowminimap":
                case "below":
                    return HudAnchorMode.BelowMinimap;
                default:
                    return HudAnchorMode.BelowMinimap;
            }
        }

        private static Color Grade(float value, float good, float bad, Color baseColor)
        {
            if (bad < good)
            {
                float swap = good;
                good = bad;
                bad = swap;
            }

            Color color;
            if (value <= good)
            {
                color = GoodColor;
            }
            else if (value <= bad)
            {
                color = WarnColor;
            }
            else
            {
                color = BadColor;
            }

            color.a = baseColor.a;
            return color;
        }

        private static Text CreateText(Transform parent, string name, out Outline outline)
        {
            GameObject go = new GameObject(name);
            go.layer = 5;
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            Text text = go.AddComponent<Text>();
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.supportRichText = false;

            outline = go.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1f, -1f);
            outline.useGraphicAlpha = true;

            return text;
        }
    }

    /// <summary>Font lookup with a per (setting, size, language) cache.</summary>
    internal static class FontProvider
    {
        private static readonly string[] ChineseFontCandidates =
        {
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "微软雅黑",
            "SimHei",
            "黑体",
            "SimSun",
            "宋体",
            "Noto Sans CJK SC",
            "Source Han Sans SC",
            "Arial Unicode MS"
        };

        private static readonly string[] LatinFontCandidates =
        {
            "AveriaSansLibre-Bold",
            "AveriaSansLibre",
            "Arial"
        };

        private static Font _cached;
        private static string _cachedSetting;
        private static int _cachedSize;
        private static bool _cachedChinese;

        public static Font Get(string setting, int size, bool chinese)
        {
            if (_cached != null && _cachedSetting == setting && _cachedSize == size && _cachedChinese == chinese)
            {
                return _cached;
            }

            Font font = Resolve(setting, size, chinese);
            _cached = font;
            _cachedSetting = setting;
            _cachedSize = size;
            _cachedChinese = chinese;
            return font;
        }

        private static Font Resolve(string setting, int size, bool chinese)
        {
            int pointSize = Mathf.Clamp(size, 6, 72);
            bool auto = string.IsNullOrEmpty(setting) || setting.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

            if (!auto)
            {
                Font named = FindInResources(setting.Trim()) ?? CreateFromOs(setting.Trim(), pointSize);
                if (named != null && (!chinese || HasChineseGlyphs(named)))
                {
                    return named;
                }
            }

            if (chinese)
            {
                Font cjk = CreateFromOs(ChineseFontCandidates, pointSize);
                if (cjk != null)
                {
                    return cjk;
                }
            }

            Font fallback = FindInResources("AveriaSansLibre-Bold") ?? FindAnyInResources();
            if (fallback != null)
            {
                return fallback;
            }

            return CreateFromOs(LatinFontCandidates, pointSize);
        }

        private static Font FindInResources(string name)
        {
            Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
            for (int i = 0; i < fonts.Length; i++)
            {
                Font font = fonts[i];
                if (font != null && font.name == name)
                {
                    return font;
                }
            }

            return null;
        }

        private static Font FindAnyInResources()
        {
            Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
            for (int i = 0; i < fonts.Length; i++)
            {
                if (fonts[i] != null)
                {
                    return fonts[i];
                }
            }

            return null;
        }

        private static Font CreateFromOs(string name, int size)
        {
            try
            {
                Font font = Font.CreateDynamicFontFromOSFont(name, size);
                return font != null && font.name == name ? font : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Font CreateFromOs(string[] candidates, int size)
        {
            string[] installed;
            try
            {
                installed = Font.GetOSInstalledFontNames();
            }
            catch (Exception)
            {
                installed = null;
            }

            if (installed == null || installed.Length == 0)
            {
                return null;
            }

            for (int c = 0; c < candidates.Length; c++)
            {
                string candidate = candidates[c];
                if (Array.IndexOf(installed, candidate) < 0)
                {
                    continue;
                }

                try
                {
                    Font font = Font.CreateDynamicFontFromOSFont(candidate, size);
                    if (font != null && HasChineseGlyphs(font))
                    {
                        return font;
                    }
                }
                catch (Exception)
                {
                    // try the next candidate
                }
            }

            return null;
        }

        private static bool HasChineseGlyphs(Font font)
        {
            try
            {
                return font.HasCharacter('\u5ef6') && font.HasCharacter('\u8fdf');
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>The dark rounded sprite Valheim uses behind input fields.</summary>
    internal static class PanelSprite
    {
        private const string PreferredSprite = "InputFieldBackground";
        private static Sprite _cached;
        private static bool _searched;

        public static Sprite Get()
        {
            if (_searched)
            {
                return _cached;
            }

            _searched = true;
            Sprite[] sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int i = 0; i < sprites.Length; i++)
            {
                Sprite sprite = sprites[i];
                if (sprite != null && sprite.name == PreferredSprite)
                {
                    _cached = sprite;
                    return _cached;
                }
            }

            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites[i] != null)
                {
                    _cached = sprites[i];
                    break;
                }
            }

            return _cached;
        }
    }
}
