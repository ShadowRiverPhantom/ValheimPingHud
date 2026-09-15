using System;
using System.Reflection;
using UnityEngine;

namespace ValheimPingHud
{
    /// <summary>
    /// Measures the real round trip time on transports that do not report latency.
    ///
    /// Why this exists: Steam's real-time statistics only exist for Steam P2P sockets
    /// (ZSteamSocket). A direct-IP / LAN connection uses ZSocket2 (TCP), and its
    /// GetConnectionQuality() - inherited from ZNetStats - always fills in
    /// ping = 0 and quality = 0 (only the byte rates are real). Without this probe a
    /// LAN player would see "0 ms / 100% loss" although the connection is perfectly fine.
    ///
    /// How it works: Valheim's RPC layer already performs a ping/pong exchange once per
    /// second (see ZRpc.m_pingInterval = 1):
    ///   * ZRpc.UpdatePing() sends a ping request and resets m_pingTimer to 0.
    ///   * ZRpc.ReceivePing() resets m_timeSinceLastPing to 0 when the reply arrives.
    /// Both fields are private, so they are read by reflection. The value of m_pingTimer
    /// at the moment m_timeSinceLastPing wraps is the elapsed time since the request was
    /// sent, i.e. the round trip time. The game only services the socket once per frame,
    /// so the measurement is quantised to one frame; half a frame is subtracted to
    /// remove the systematic over-estimate.
    /// </summary>
    internal sealed class RpcPingProbe
    {
        private const float NewerThanSeconds = 5f;

        private static FieldInfo _pingTimerField;
        private static FieldInfo _ageField;
        private static bool _fieldsResolved;

        private float _lastAge = -1f;
        private float _lastSampleTime = -1000f;

        /// <summary>Last measured round trip time in milliseconds.</summary>
        public float RttMs { get; private set; }

        /// <summary>Incremented once per new measurement.</summary>
        public int Version { get; private set; }

        public bool HasSample { get; private set; }

        /// <summary>True when the last measurement is recent enough to be displayed.</summary>
        public bool Fresh
        {
            get { return HasSample && Time.time - _lastSampleTime < NewerThanSeconds; }
        }

        public void Reset()
        {
            _lastAge = -1f;
            HasSample = false;
            RttMs = 0f;
        }

        /// <summary>Must be called every frame: the ping/pong counters wrap between frames.</summary>
        public void Poll(ZRpc rpc)
        {
            if (rpc == null)
            {
                return;
            }

            ResolveFields();
            if (_pingTimerField == null || _ageField == null)
            {
                return;
            }

            float timer;
            float age;
            try
            {
                timer = (float)_pingTimerField.GetValue(rpc);
                age = (float)_ageField.GetValue(rpc);
            }
            catch (Exception)
            {
                return;
            }

            if (_lastAge >= 0f && age < _lastAge - 0.0001f)
            {
                // A pong just landed: m_pingTimer holds the time since our request went out.
                float rttMs = Mathf.Max(0f, timer - Time.deltaTime * 0.5f) * 1000f;
                if (rttMs >= 0f && rttMs < 30000f)
                {
                    RttMs = rttMs;
                    _lastSampleTime = Time.time;
                    HasSample = true;
                    Version++;
                }
            }

            _lastAge = age;
        }

        private static void ResolveFields()
        {
            if (_fieldsResolved)
            {
                return;
            }

            _fieldsResolved = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _pingTimerField = typeof(ZRpc).GetField("m_pingTimer", flags);
            _ageField = typeof(ZRpc).GetField("m_timeSinceLastPing", flags);
        }
    }
}
