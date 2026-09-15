using System;
using UnityEngine;

namespace ValheimPingHud
{
    /// <summary>
    /// Collects latency / packet loss data out of Valheim's own network layer.
    ///
    /// Valheim has four socket implementations and they do NOT all report statistics:
    ///
    ///   ZSteamSocket     Steam P2P (invite / server browser)
    ///                    -> real ping, real delivery rate, real byte rates
    ///   ZSocket2         direct IP / LAN, plain TCP (inherits ZNetStats)
    ///                    -> ping = 0 and quality = 0 hardcoded, byte rates are real
    ///   ZPlayFabSocket   crossplay (inherits ZNetStats)
    ///                    -> ping = 0 and quality = 0 hardcoded, byte rates are real
    ///   ZSteamSocketOLD  legacy Steam socket
    ///                    -> everything zeroed
    ///
    /// So for anything but ZSteamSocket this class reports HasLoss = false (and the HUD
    /// prints "N/A" instead of inventing 100% loss), while latency is taken from
    /// <see cref="RpcPingProbe"/>, which works on every transport.
    /// </summary>
    internal sealed class NetStatsSampler
    {
        private const int HistorySize = 32;      // samples kept for min/max/jitter
        private const float EmaAlpha = 0.30f;    // smoothing for the displayed values

        private readonly float[] _pingHistory = new float[HistorySize];
        private int _historyCount;
        private int _historyNext;
        private int _lastProbeVersion = -1;

        private readonly RpcPingProbe _probe = new RpcPingProbe();
        private ZRpc _probeTarget;

        // ---- state exposed to the UI -------------------------------------
        public bool HasConnection { get; private set; }
        public bool IsServer { get; private set; }
        public bool IsSinglePlayer { get; private set; }
        public int PeerCount { get; private set; }

        /// <summary>True while the socket is a Steam P2P socket (loss/quality are trustworthy).</summary>
        public bool LossSupported { get; private set; }

        public bool HasPing { get; private set; }
        public bool HasLoss { get; private set; }
        public bool HasBandwidth { get; private set; }

        /// <summary>True when the displayed latency comes from the RPC ping/pong probe.</summary>
        public bool PingFromRpc { get; private set; }

        /// <summary>Socket type name, for the one-time log line and diagnostics.</summary>
        public string TransportName { get; private set; }

        public float PingRaw { get; private set; }
        public float PingSmooth { get; private set; }
        public float PingMin { get; private set; }
        public float PingMax { get; private set; }
        public float JitterMs { get; private set; }

        public float QualityLocal { get; private set; }
        public float QualityRemote { get; private set; }

        /// <summary>
        /// Packet loss in percent. Valve defines m_flConnectionQualityLocal as the
        /// "percentage of packets delivered end-to-end in order", so
        /// (1 - quality) * 100 IS the packet loss rate, measured by Steam itself.
        /// Only meaningful for Steam P2P sockets.
        /// </summary>
        public float LossPercent { get; private set; }
        public float LossSmooth { get; private set; }

        public float OutBytesPerSec { get; private set; }
        public float InBytesPerSec { get; private set; }

        /// <summary>
        /// Per-frame poll of the RPC ping/pong counters. The counters wrap between frames,
        /// so this cannot be driven from the slower sampling interval.
        /// </summary>
        public void PollTransport()
        {
            if (_probeTarget == null)
            {
                return;
            }

            _probe.Poll(_probeTarget);
        }

        public void Sample()
        {
            HasConnection = false;
            HasPing = false;
            HasLoss = false;
            HasBandwidth = false;
            LossSupported = false;
            PingFromRpc = false;
            PeerCount = 0;
            IsServer = false;
            IsSinglePlayer = false;

            ZNet znet = ZNet.instance;
            if (znet == null)
            {
                SetProbeTarget(null);
                ResetHistory();
                return;
            }

            IsServer = znet.IsServer();
            IsSinglePlayer = ZNet.IsSinglePlayer;

            int ready = 0;
            bool steam = false;
            ZRpc rpc = null;
            string transport = null;

            try
            {
                System.Collections.Generic.List<ZNetPeer> peers = znet.GetConnectedPeers();
                if (peers != null)
                {
                    for (int i = 0; i < peers.Count; i++)
                    {
                        ZNetPeer peer = peers[i];
                        if (peer == null || !peer.IsReady())
                        {
                            continue;
                        }

                        ready++;
                        if (rpc == null)
                        {
                            rpc = peer.m_rpc;
                        }

                        if (peer.m_socket != null)
                        {
                            transport = peer.m_socket.GetType().Name;
                            if (peer.m_socket is ZSteamSocket)
                            {
                                steam = true;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A peer list changing mid-iteration must never break the HUD.
            }

            PeerCount = ready;
            TransportName = transport;
            SetProbeTarget(rpc);

            if (ready <= 0)
            {
                ResetHistory();
                return;
            }

            float localQuality = 0f;
            float remoteQuality = 0f;
            float outBytes = 0f;
            float inBytes = 0f;
            int ping = 0;

            znet.GetNetStats(out localQuality, out remoteQuality, out ping, out outBytes, out inBytes);

            HasConnection = true;
            LossSupported = steam;
            OutBytesPerSec = outBytes;
            InBytesPerSec = inBytes;
            HasBandwidth = outBytes > 0f || inBytes > 0f;

            if (steam)
            {
                // ZSteamSocket: everything is real, but Steam needs a moment after
                // connecting before it has numbers. An all-zero answer means "no data
                // yet", not "the link is dead".
                bool valid = ping > 0 || localQuality > 0f || outBytes > 0f || inBytes > 0f;
                if (!valid)
                {
                    return;
                }

                HasPing = true;
                HasLoss = true;
                PingFromRpc = false;
                QualityLocal = Mathf.Clamp01(localQuality);
                QualityRemote = Mathf.Clamp01(remoteQuality);
                PingRaw = ping;

                // Valve: m_flConnectionQualityLocal is the percentage of packets delivered
                // end-to-end in order, so the gap to 1.0 is the packet loss rate itself.
                LossPercent = Mathf.Clamp01(1f - QualityLocal) * 100f;
                LossSmooth = LossPercent <= 0f ? 0f : Mathf.Lerp(LossSmooth, LossPercent, EmaAlpha);

                PushPingHistory(ping);
                PingSmooth = PingSmooth <= 0f ? ping : Mathf.Lerp(PingSmooth, ping, EmaAlpha);
                return;
            }

            // Non-Steam transport (TCP / crossplay): the socket does not provide ping or
            // loss at all. Latency comes from the RPC probe, loss simply has no source.
            HasLoss = false;
            LossPercent = 0f;
            LossSmooth = 0f;

            if (!_probe.Fresh)
            {
                return;
            }

            HasPing = true;
            PingFromRpc = true;
            PingRaw = _probe.RttMs;

            if (_lastProbeVersion != _probe.Version)
            {
                _lastProbeVersion = _probe.Version;
                PushPingHistory(_probe.RttMs);
                PingSmooth = PingSmooth <= 0f
                    ? _probe.RttMs
                    : Mathf.Lerp(PingSmooth, _probe.RttMs, EmaAlpha);
            }
        }

        private void SetProbeTarget(ZRpc rpc)
        {
            if (ReferenceEquals(rpc, _probeTarget))
            {
                return;
            }

            _probeTarget = rpc;
            _lastProbeVersion = -1;
            _probe.Reset();
        }

        private void PushPingHistory(float ping)
        {
            _pingHistory[_historyNext] = ping;
            _historyNext = (_historyNext + 1) % HistorySize;
            if (_historyCount < HistorySize)
            {
                _historyCount++;
            }

            float sum = 0f;
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < _historyCount; i++)
            {
                float v = _pingHistory[i];
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            PingMin = _historyCount > 0 ? min : 0f;
            PingMax = _historyCount > 0 ? max : 0f;

            if (_historyCount <= 1)
            {
                JitterMs = 0f;
                return;
            }

            float mean = sum / _historyCount;
            float variance = 0f;
            for (int i = 0; i < _historyCount; i++)
            {
                float d = _pingHistory[i] - mean;
                variance += d * d;
            }

            JitterMs = Mathf.Sqrt(variance / _historyCount);
        }

        private void ResetHistory()
        {
            _historyCount = 0;
            _historyNext = 0;
            HasPing = false;
            HasLoss = false;
            HasBandwidth = false;
            LossSupported = false;
            PingSmooth = 0f;
            PingRaw = 0f;
            PingMin = 0f;
            PingMax = 0f;
            JitterMs = 0f;
            LossPercent = 0f;
            LossSmooth = 0f;
            QualityLocal = 0f;
            QualityRemote = 0f;
            OutBytesPerSec = 0f;
            InBytesPerSec = 0f;
        }
    }
}
