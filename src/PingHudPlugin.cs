using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace ValheimPingHud
{
    /// <summary>
    /// Shows the latency and the packet loss of the connection to the current server,
    /// in a small HUD panel styled like Aidin's DayTimeCountdown panel but anchored
    /// to a different slot (below the minimap by default) so the two never overlap.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class PingHudPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "kagegawa.valheim.pinghud";
        public const string PluginName = "PingHud - Server Latency & Packet Loss";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;

        // ---- 1. General 常规 -------------------------------------------------
        internal ConfigEntry<bool> CfgEnabled;
        internal ConfigEntry<KeyboardShortcut> CfgToggleKey;
        internal ConfigEntry<float> CfgUpdateInterval;
        internal ConfigEntry<bool> CfgHideWhenOffline;
        internal ConfigEntry<bool> CfgHideWithHud;

        // ---- 2. Position 位置 ------------------------------------------------
        internal ConfigEntry<string> CfgPosition;
        internal ConfigEntry<float> CfgOffsetX;
        internal ConfigEntry<float> CfgOffsetY;
        internal ConfigEntry<bool> CfgAutoAvoid;
        internal ConfigEntry<float> CfgAvoidGap;
        internal ConfigEntry<string> CfgAvoidPanelNames;

        // ---- 3. Appearance 外观 ----------------------------------------------
        internal ConfigEntry<float> CfgPanelWidth;
        internal ConfigEntry<float> CfgPanelHeight;
        internal ConfigEntry<string> CfgFontName;
        internal ConfigEntry<int> CfgFontSize;
        internal ConfigEntry<Color> CfgFontColor;
        internal ConfigEntry<bool> CfgOutlineEnabled;
        internal ConfigEntry<Color> CfgOutlineColor;
        internal ConfigEntry<bool> CfgBackgroundEnabled;
        internal ConfigEntry<Color> CfgBackgroundColor;
        internal ConfigEntry<float> CfgPadding;

        // ---- 4. Display 显示 -------------------------------------------------
        internal ConfigEntry<bool> CfgShowPing;
        internal ConfigEntry<bool> CfgShowLoss;
        internal ConfigEntry<bool> CfgShowDetails;
        internal ConfigEntry<bool> CfgSmooth;
        internal ConfigEntry<bool> CfgShowLabels;
        internal ConfigEntry<bool> CfgReverseText;
        internal ConfigEntry<bool> CfgColorize;
        internal ConfigEntry<float> CfgPingGood;
        internal ConfigEntry<float> CfgPingBad;
        internal ConfigEntry<float> CfgLossGood;
        internal ConfigEntry<float> CfgLossBad;
        internal ConfigEntry<string> CfgLanguage;

        private readonly NetStatsSampler _sampler = new NetStatsSampler();
        private PingHudPanel _panel;
        private float _sampleTimer;
        private bool _userVisible = true;
        private bool _errorLogged;
        private string _languageSetting;
        private string _loggedTransport;
        private HudStrings _strings = HudStrings.For(HudLanguage.English);

        private static MethodInfo _hudIsVisible;

        internal HudStrings Strings
        {
            get { return _strings; }
        }

        private void Awake()
        {
            Log = Logger;
            BindConfiguration();
            RefreshStrings(true);
            Log.LogInfo(PluginName + " " + PluginVersion + " loaded (anchor: " +
                        PingHudPanel.ParseMode(CfgPosition.Value) + ", language: " + _strings.Language + ").");
        }

        private void OnDestroy()
        {
            if (_panel != null)
            {
                _panel.Destroy();
                _panel = null;
            }
        }

        // ------------------------------------------------------------------
        // configuration
        // ------------------------------------------------------------------

        private void BindConfiguration()
        {
            const string general = "1. General 常规";
            const string position = "2. Position 位置";
            const string appearance = "3. Appearance 外观";
            const string display = "4. Display 显示";

            CfgEnabled = Config.Bind(general, "Enabled 启用", true,
                "是否启用本插件。\nEnable or disable the HUD.");

            CfgToggleKey = Config.Bind(general, "Toggle key 切换热键", new KeyboardShortcut(KeyCode.F8),
                "游戏中按下该按键可临时显示/隐藏面板。\nPress in game to show/hide the panel temporarily.");

            CfgUpdateInterval = Config.Bind(general, "Update interval 刷新间隔", 0.25f,
                new ConfigDescription("采样网络数据的间隔（秒）。\nHow often the network data is sampled, in seconds.",
                    new AcceptableValueRange<float>(0.05f, 2f)));

            CfgHideWhenOffline = Config.Bind(general, "Hide when not connected 未联网时隐藏", true,
                "单人游戏或未连接服务器时隐藏面板。\nHide the panel in single player / when no server connection exists.");

            CfgHideWithHud = Config.Bind(general, "Hide with HUD 随HUD隐藏", true,
                "游戏隐藏 HUD 时同时隐藏本面板。\nHide the panel together with the game HUD.");

            CfgPosition = Config.Bind(position, "Anchor 位置", "BelowMinimap",
                "面板位置。可选值：\n" +
                "  BelowMinimap  - 小地图正下方（默认）\n" +
                "  AboveMinimap  - 小地图正上方\n" +
                "  TopRight / TopLeft / BottomRight / BottomLeft - 屏幕四角\n" +
                "Panel anchor: BelowMinimap (default), AboveMinimap, TopRight, TopLeft, BottomRight, BottomLeft.");

            CfgOffsetX = Config.Bind(position, "Extra X offset 额外X偏移", 0f,
                "在所选位置基础上再水平移动（像素）。\nExtra horizontal offset in HUD pixels.");

            CfgOffsetY = Config.Bind(position, "Extra Y offset 额外Y偏移", 0f,
                "在所选位置基础上再垂直移动（像素，正数向上）。\nExtra vertical offset in HUD pixels (positive = up).");

            CfgAutoAvoid = Config.Bind(position, "Auto avoid other panels 自动避让其他面板", true,
                "开启后会自动检测下列面板，若位置重叠则自动错开，保证 UI 不重叠。\n" +
                "Automatically slides this panel out of the way of the panels listed below.");

            CfgAvoidGap = Config.Bind(position, "Avoid gap 避让间距", 8f,
                "避让时两个面板之间保留的像素间距。\nGap kept between this panel and the avoided panel.");

            CfgAvoidPanelNames = Config.Bind(position, "Avoid panel names 避让面板名称", "DayTimePanel",
                "需要避让的 HUD 子物体名称（逗号分隔）。\n" +
                "填 HUD 里已存在的面板名即可，避免和本面板重叠。\n" +
                "Comma separated list of HUD child object names to avoid. If a panel with one of these names is already in the way, PingHud moves aside.");

            CfgPanelWidth = Config.Bind(appearance, "Panel width 面板宽度", 200f,
                "面板宽度。\nPanel width.");

            CfgPanelHeight = Config.Bind(appearance, "Panel height 面板高度", 30f,
                "面板高度（内容需要更高时会自动扩展）。\nPanel height; grows automatically when extra rows are shown.");

            CfgFontName = Config.Bind(appearance, "Font name 字体名称", "auto",
                "字体名称。auto = 中文使用系统中文字体、英文使用游戏字体(AveriaSansLibre-Bold)。\n" +
                "Font name, or 'auto'.");

            CfgFontSize = Config.Bind(appearance, "Font size 字号", 16,
                new ConfigDescription("字号。\nFont size.", new AcceptableValueRange<int>(6, 72)));

            CfgFontColor = Config.Bind(appearance, "Font color 字体颜色", new Color(1f, 1f, 1f, 0.791f),
                "字体颜色（RGBA）。\nText colour.");

            CfgOutlineEnabled = Config.Bind(appearance, "Text outline enabled 文字描边", true,
                "是否给文字加描边，保证任何背景下都能看清。\nDraw an outline around the text.");

            CfgOutlineColor = Config.Bind(appearance, "Text outline color 描边颜色", Color.black,
                "描边颜色。\nOutline colour.");

            CfgBackgroundEnabled = Config.Bind(appearance, "Display background 显示背景", true,
                "是否显示半透明背景。\nDraw the dark background panel.");

            CfgBackgroundColor = Config.Bind(appearance, "Background color 背景颜色", new Color(0f, 0f, 0f, 0.3921569f),
                "背景颜色（RGBA）。\nBackground colour.");

            CfgPadding = Config.Bind(appearance, "Padding left and right 左右内边距", 10f,
                "文字与面板左右边缘的间距。\nHorizontal padding between the text and the panel edge.");

            CfgShowPing = Config.Bind(display, "Show ping 显示延迟", true,
                "显示延迟 (ms)。\nShow latency in ms.");

            CfgShowLoss = Config.Bind(display, "Show packet loss 显示丢包", true,
                "显示丢包率 (%)。\n" +
                "只有 Steam P2P 连接（ZSteamSocket）才有丢包数据：Steam 的“连接质量”定义是\n" +
                "“端到端按序送达的数据包比例”，所以丢包率 = 100% − 连接质量（0% = 不丢包）。\n" +
                "跨平台联机（ZPlayFabSocket）和直连 IP / 局域网（ZSocket2，TCP）这两种传输层\n" +
                "在游戏内部就把延迟与质量硬编码为 0，没有丢包数据源，因此显示 N/A。\n" +
                "想要真实丢包率请用 Steam 邀请 / 服务器浏览器进服（关闭 Crossplay）。\n" +
                "-- = 暂时还没数据；N/A = 该连接方式不提供。\n" +
                "Packet loss is only available on Steam P2P connections; crossplay and direct-IP\n" +
                "connections report none and show N/A.");

            CfgShowDetails = Config.Bind(display, "Show extra details 显示详细信息", false,
                "额外显示抖动、连接质量与上下行带宽（面板会自动变高）。\n" +
                "Also show jitter, connection quality and up/down bandwidth.");

            CfgSmooth = Config.Bind(display, "Smooth values 数值平滑", true,
                "使用平滑后的数值，避免数字剧烈跳动。\nSmooth the displayed numbers.");

            CfgShowLabels = Config.Bind(display, "Show text labels 显示文字标签", true,
                "显示“延迟/丢包”等文字标签。\nShow the text labels next to the numbers.");

            CfgReverseText = Config.Bind(display, "Ping on the right 延迟显示在右侧", false,
                "勾选后延迟显示在右、丢包显示在左。\nPut ping on the right and loss on the left.");

            CfgColorize = Config.Bind(display, "Color by quality 按质量着色", true,
                "根据下面的阈值把数值染成绿/黄/红。\nColour the values green/yellow/red using the thresholds below.");

            CfgPingGood = Config.Bind(display, "Ping good (ms) 延迟良好阈值", 80f,
                "低于该值显示绿色。\nBelow this ping the value is green.");

            CfgPingBad = Config.Bind(display, "Ping bad (ms) 延迟较差阈值", 150f,
                "高于该值显示红色。\nAbove this ping the value is red.");

            CfgLossGood = Config.Bind(display, "Loss good (%) 丢包良好阈值", 1f,
                "低于该值显示绿色。\nBelow this loss the value is green.");

            CfgLossBad = Config.Bind(display, "Loss bad (%) 丢包较差阈值", 5f,
                "高于该值显示红色。\nAbove this loss the value is red.");

            CfgLanguage = Config.Bind(display, "Language 语言", "auto",
                "面板语言：auto（跟随游戏语言）/ Chinese / English。\n" +
                "Panel language: auto (follow the game), Chinese or English.");
        }

        // ------------------------------------------------------------------
        // main loop
        // ------------------------------------------------------------------

        private void Update()
        {
            try
            {
                Tick();
                _errorLogged = false;
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    if (Log != null)
                    {
                        Log.LogError("PingHud update failed: " + e);
                    }
                }
            }
        }

        private void Tick()
        {
            if (CfgToggleKey.Value.IsDown())
            {
                _userVisible = !_userVisible;
            }

            // The RPC ping/pong counters wrap between frames, so they must be polled
            // every frame - not on the slower sampling interval.
            _sampler.PollTransport();

            _sampleTimer += Time.unscaledDeltaTime;
            float interval = Mathf.Max(0.05f, CfgUpdateInterval.Value);
            if (_sampleTimer >= interval)
            {
                _sampleTimer = 0f;
                _sampler.Sample();
                RefreshStrings(false);
                LogTransportOnce();
            }

            if (_panel != null && !_panel.Alive)
            {
                _panel = null;
            }

            if (!ShouldShow())
            {
                if (_panel != null)
                {
                    _panel.SetVisible(false);
                }

                return;
            }

            if (_panel == null)
            {
                Hud hud = Hud.instance;
                if (hud == null || hud.m_rootObject == null)
                {
                    return;
                }

                _panel = new PingHudPanel(hud.m_rootObject.transform);
                if (Log != null)
                {
                    Log.LogInfo("HUD panel created (" + PingHudPanel.ParseMode(CfgPosition.Value) + ").");
                }
            }

            _panel.SetVisible(true);
            _panel.Refresh(this, _sampler);
        }

        /// <summary>
        /// Writes one line about the transport the first time a connection is seen.
        /// Steam P2P sockets report latency and delivery rate; direct-IP/LAN (ZSocket2,
        /// TCP) and crossplay (ZPlayFabSocket) sockets hardcode ping = 0 and quality = 0,
        /// so the panel shows "N/A" for loss and takes latency from the RPC probe.
        /// </summary>
        private void LogTransportOnce()
        {
            string transport = _sampler.TransportName;
            if (transport == null || transport == _loggedTransport)
            {
                return;
            }

            _loggedTransport = transport;
            if (Log == null)
            {
                return;
            }

            if (_sampler.LossSupported)
            {
                Log.LogInfo("Connection is " + transport + " (Steam P2P): latency and packet loss come from Steam.");
            }
            else
            {
                Log.LogInfo("Connection is " + transport +
                            " (not Steam P2P): Steam provides no latency/loss for this transport, " +
                            "so packet loss shows N/A and latency is measured from the game's own RPC ping/pong.");
            }
        }

        private bool ShouldShow()
        {
            if (!CfgEnabled.Value || !_userVisible)
            {
                return false;
            }

            if (Player.m_localPlayer == null)
            {
                return false;
            }

            Hud hud = Hud.instance;
            if (hud == null || hud.m_rootObject == null)
            {
                return false;
            }

            if (CfgHideWithHud.Value && !IsHudVisible(hud))
            {
                return false;
            }

            if (!_sampler.HasConnection && CfgHideWhenOffline.Value)
            {
                return false;
            }

            return true;
        }

        private void RefreshStrings(bool force)
        {
            string setting = CfgLanguage == null ? "auto" : CfgLanguage.Value;
            if (!force && setting == _languageSetting)
            {
                return;
            }

            _languageSetting = setting;
            _strings = HudStrings.For(HudStrings.ResolveLanguage(setting));
        }

        /// <summary>Hud.IsVisible() is private; it is simply "HUD root is not parked off screen".</summary>
        private static bool IsHudVisible(Hud hud)
        {
            if (_hudIsVisible == null)
            {
                _hudIsVisible = typeof(Hud).GetMethod("IsVisible",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (_hudIsVisible == null)
            {
                return true;
            }

            object result = _hudIsVisible.Invoke(hud, null);
            return !(result is bool) || (bool)result;
        }
    }
}
