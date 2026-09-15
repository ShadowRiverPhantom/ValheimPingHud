using System;
using System.Globalization;
using UnityEngine;

namespace ValheimPingHud
{
    internal enum HudLanguage
    {
        English,
        Chinese,
        TraditionalChinese
    }

    /// <summary>
    /// All user visible text plus number formatting.
    /// "Auto" follows the language the player selected inside Valheim.
    /// </summary>
    internal sealed class HudStrings
    {
        public const string AutoSetting = "auto";

        public readonly HudLanguage Language;

        public readonly string PingLabel;
        public readonly string LossLabel;
        public readonly string JitterLabel;
        public readonly string QualityLabel;
        public readonly string DownLabel;
        public readonly string UpLabel;
        public readonly string OfflineLabel;
        public readonly string UnknownValue;
        public readonly string NotAvailableValue;

        private HudStrings(HudLanguage language)
        {
            Language = language;
            UnknownValue = "--";
            NotAvailableValue = "N/A";
            if (language == HudLanguage.Chinese)
            {
                PingLabel = "延迟";
                LossLabel = "丢包";
                JitterLabel = "抖动";
                QualityLabel = "质量";
                DownLabel = "下行";
                UpLabel = "上行";
                OfflineLabel = "未连接";
            }
            else if (language == HudLanguage.TraditionalChinese)
            {
                PingLabel = "延遲";
                LossLabel = "丟包";
                JitterLabel = "抖動";
                QualityLabel = "品質";
                DownLabel = "下行";
                UpLabel = "上行";
                OfflineLabel = "未連線";
            }
            else
            {
                PingLabel = "Ping";
                LossLabel = "Loss";
                JitterLabel = "Jitter";
                QualityLabel = "Quality";
                DownLabel = "Down";
                UpLabel = "Up";
                OfflineLabel = "Not connected";
            }
        }

        public static HudLanguage ResolveLanguage(string setting)
        {
            // Traditional must be tested first: "繁體中文" and "Chinese_Trad" both also contain the
            // simplified markers, so checking "chinese" first would swallow them.
            if (Matches(setting, "traditional") || setting == "繁體中文" || setting == "繁体中文" ||
                setting == "繁體" || setting == "繁体" || setting == "正體中文" ||
                Matches(setting, "chinese_trad"))
            {
                return HudLanguage.TraditionalChinese;
            }

            if (Matches(setting, "chinese") || setting == "中文" || setting == "简体中文" ||
                setting == "簡體中文")
            {
                return HudLanguage.Chinese;
            }

            if (Matches(setting, "english") || setting == "英文")
            {
                return HudLanguage.English;
            }

            // "auto" (or anything unknown): follow the game's own language.
            try
            {
                Localization localization = Localization.instance;
                if (localization != null)
                {
                    string gameLanguage = localization.GetSelectedLanguage();
                    if (!string.IsNullOrEmpty(gameLanguage))
                    {
                        // Valheim carries both Chinese variants: zh-CN/zh-SG map to "Chinese" and
                        // zh-HK maps to "Chinese_Trad". Anything else that is not Chinese falls
                        // through to English rather than to a half-translated panel.
                        if (gameLanguage.IndexOf("trad", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return HudLanguage.TraditionalChinese;
                        }

                        if (gameLanguage.IndexOf("chin", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return HudLanguage.Chinese;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to English; never let a lookup break the HUD.
            }

            return HudLanguage.English;
        }

        public static HudStrings For(HudLanguage language)
        {
            if (language == HudLanguage.Chinese) { return Chinese; }
            if (language == HudLanguage.TraditionalChinese) { return Traditional; }
            return English;
        }

        private static readonly HudStrings Chinese = new HudStrings(HudLanguage.Chinese);
        private static readonly HudStrings Traditional = new HudStrings(HudLanguage.TraditionalChinese);
        private static readonly HudStrings English = new HudStrings(HudLanguage.English);

        private static bool Matches(string value, string expected)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        // ---- value formatting -------------------------------------------------

        public string FormatPing(float milliseconds)
        {
            return Mathf.RoundToInt(milliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
        }

        public string FormatLoss(float percent)
        {
            return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        public string FormatJitter(float milliseconds)
        {
            return milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        }

        public string FormatQuality(float quality)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(quality) * 100f)
                .ToString(CultureInfo.InvariantCulture) + "%";
        }

        public string FormatRate(float bytesPerSecond)
        {
            float value = Mathf.Max(0f, bytesPerSecond);
            if (value >= 1024f * 1024f)
            {
                return (value / (1024f * 1024f)).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s";
            }

            if (value >= 1024f)
            {
                return (value / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " kB/s";
            }

            return Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture) + " B/s";
        }

        public string Labeled(string label, string value, bool showLabels)
        {
            return showLabels ? label + " " + value : value;
        }
    }
}
