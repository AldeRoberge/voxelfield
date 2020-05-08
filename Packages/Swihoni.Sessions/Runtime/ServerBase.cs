using System.Collections.Generic;
using System.Net;
using Swihoni.Collections;
using Swihoni.Components;
using Swihoni.Networking;
using Swihoni.Sessions.Components;
using Swihoni.Sessions.Modes;
using Swihoni.Sessions.Player.Components;
using UnityEngine;
using UnityEngine.Profiling;

namespace Swihoni.Sessions
{
    public abstract class ServerBase : NetworkedSessionBase
    {
        private ComponentServerSocket m_Socket;
        private readonly DualDictionary<IPEndPoint, byte> m_PlayerIds = new DualDictionary<IPEndPoint, byte>();
        
        public override ComponentSocketBase Socket => m_Socket;

        protected ServerBase(SessionElements elements, IPEndPoint ipEndPoint)
            : base(elements, ipEndPoint)
        {
            ForEachPlayer(player => player.Add(typeof(ServerTag), typeof(ServerPingComponent)));
        }

        public override void Start()
        {
            base.Start();
            m_Socket = new ComponentServerSocket(IpEndPoint);
            RegisterMessages(m_Socket);

            Physics.autoSimulation = false;
        }

        protected virtual void PreTick(Container tickSession) { }

        protected virtual void PostTick(Container tickSession) { }

        protected virtual void SettingsTick(Container serverSession)
        {
            var tickRate = serverSession.Require<TickRateProperty>();
            tickRate.FastCopyFrom(DebugBehavior.Singleton.TickRate);
            serverSession.Require<ModeIdProperty>().FastCopyFrom(DebugBehavior.Singleton.ModeId);
            Time.fixedDeltaTime = tickRate.TickInterval;
        }

        protected sealed override void Tick(uint tick, float time, float duration)
        {
            base.Tick(tick, time, duration);

            Profiler.BeginSample("Server Setup");
            Container previousServerSession = m_SessionHistory.Peek(),
                      serverSession = m_SessionHistory.ClaimNext();
            serverSession.FastCopyFrom(previousServerSession);

            SettingsTick(serverSession);

            var serverStamp = serverSession.Require<ServerStampComponent>();
            serverStamp.tick.Value = tick;
            serverStamp.time.Value = time;
            serverStamp.duration.Value = duration;
            Profiler.EndSample();

            Profiler.BeginSample("Server Tick");
            PreTick(serverSession);
            Tick(serverSession, time, duration);
            PostTick(serverSession);
            IterateClients(tick, time, duration, serverSession);
            Profiler.EndSample();
        }

        private readonly List<byte> m_ToRemove = new List<byte>();

        private void IterateClients(uint tick, float time, float duration, Container serverSession)
        {
            var players = serverSession.Require<PlayerContainerArrayProperty>();
            m_ToRemove.Clear();
            foreach ((IPEndPoint _, byte playerId) in m_PlayerIds)
            {
                Container player = players[playerId];
                if (HandleTimeout(time, playerId, player)) m_ToRemove.Add(playerId);
                CheckClientPing(player, tick, time, playerId, duration);
            }
            foreach (byte playerId in m_ToRemove)
                m_PlayerIds.Remove(playerId);
        }

        private void CheckClientPing(Container player, uint tick, float time, byte playerId, float duration)
        {
            const float checkTime = 1.0f;

            var ping = player.Require<ServerPingComponent>();
            if (ping.initiateTime.WithoutValue) return;

            ping.tick.Value = tick;
            ping.initiateTime.Value = time;
            ping.checkElapsed.Value += duration;
            while (ping.checkElapsed > checkTime)
            {
                var check = new PingCheckComponent {tick = new UIntProperty(tick)};
                m_Socket.Send(check, m_PlayerIds.GetReverse(playerId));
                ping.checkElapsed.Value -= checkTime;
            }
        }

        private bool HandleTimeout(float time, byte playerId, Container player)
        {
            FloatProperty serverPlayerTime = player.Require<ServerStampComponent>().time;
            if (serverPlayerTime.WithoutValue || Mathf.Abs(serverPlayerTime.Value - time) < 2.0f) return false;
            Debug.LogWarning($"Dropping player with id: {playerId}");
            player.Reset();
            return true;
        }

        protected virtual void ServerTick(Container serverSession, float time, float duration) { }

        private void Tick(Container serverSession, float time, float duration)
        {
            ServerTick(serverSession, time, duration);
            m_Socket.PollReceived((ipEndPoint, message) =>
            {
                (byte clientId, Container serverPlayer) = GetPlayerForEndpoint(serverSession, ipEndPoint);
                switch (message)
                {
                    case ClientCommandsContainer receivedClientCommands:
                    {
                        HandleClientCommand(clientId, receivedClientCommands, serverSession, serverPlayer);
                        break;
                    }
                    case PingCheckComponent receivedPingCheck:
                    {
                        var ping = serverPlayer.Require<ServerPingComponent>();
                        if (receivedPingCheck.tick.HasValue && ping.tick == receivedPingCheck.tick)
                        {
                            float roundTripElapsed = time - ping.initiateTime;
                            ping.rtt.Value = roundTripElapsed;
                            if (serverPlayer.Has(out StatsComponent stats))
                                stats.ping.Value = (ushort) Mathf.Round(roundTripElapsed / 2.0f * 1000.0f);
                        }
                        break;
                    }
                    case DebugClientView receivedDebugClientView:
                    {
                        DebugBehavior.Singleton.Render(this, clientId, receivedDebugClientView, new Color(1.0f, 0.0f, 0.0f, 0.3f));
                        break;
                    }
                }
            });
            Physics.Simulate(duration);
            EntityManager.Modify(serverSession, time, duration);
            GetMode(serverSession).Modify(serverSession, duration);
            SendServerSession(serverSession);
        }

        private void SendServerSession(Container serverSession)
        {
            var localPlayerProperty = serverSession.Require<LocalPlayerProperty>();
            foreach ((IPEndPoint ipEndPoint, byte id) in m_PlayerIds)
            {
                localPlayerProperty.Value = id;
                m_Socket.Send(serverSession, ipEndPoint);
            }
        }

        private void HandleClientCommand(byte clientId, Container receivedClientCommands, Container serverSession, Container serverPlayer)
        {
            FloatProperty serverPlayerTime = serverPlayer.Require<ServerStampComponent>().time;
            var clientStamp = receivedClientCommands.Require<ClientStampComponent>();
            float serverTime = serverSession.Require<ServerStampComponent>().time;
            var serverPlayerClientStamp = serverPlayer.Require<ClientStampComponent>();
            // Clients start to tag with ticks once they receive their first server player state
            if (clientStamp.tick.HasValue)
            {
                if (serverPlayerClientStamp.tick.WithoutValue)
                    // Take one tick to set initial server player client stamp
                    serverPlayerClientStamp.FastMergeSet(clientStamp);
                else
                {
                    // Make sure this is the newest tick
                    if (clientStamp.tick > serverPlayerClientStamp.tick)
                    {
                        serverPlayerTime.Value += clientStamp.time - serverPlayerClientStamp.time;

                        if (Mathf.Abs(serverPlayerTime.Value - serverTime) > serverSession.Require<TickRateProperty>().TickInterval * 3)
                        {
                            ResetErrors++;
                            serverPlayerTime.Value = serverTime;
                        }

                        ModeBase mode = GetMode(serverSession);
                        serverPlayer.FastMergeSet(receivedClientCommands); // Merge in trusted
                        m_Modifier[clientId].ModifyChecked(this, clientId, serverPlayer, receivedClientCommands, clientStamp.duration);
                        mode.Modify(serverSession, serverPlayer, receivedClientCommands, clientStamp.duration);
                    }
                    else
                        Debug.LogWarning($"[{GetType().Name}] Received out of order command from client: {clientId}");
                }
            }
            else
                serverPlayerTime.Value = serverTime;
        }

        /// <summary>
        /// Handles setting up player if new connection.
        /// </summary>
        /// <returns>Client id allocated to connection and player container</returns>
        private (byte clientId, Container serverPlayer) GetPlayerForEndpoint(Container serverSession, IPEndPoint ipEndPoint)
        {
            bool isNewPlayer = !m_PlayerIds.ContainsForward(ipEndPoint);
            if (isNewPlayer)
            {
                checked
                {
                    byte newPlayerId = 1;
                    while (m_PlayerIds.ContainsReverse(newPlayerId))
                        newPlayerId++;
                    m_PlayerIds.Add(new IPEndPoint(ipEndPoint.Address, ipEndPoint.Port), newPlayerId);
                    Debug.Log($"[{GetType().Name}] Received new connection: {ipEndPoint}, setting up id: {newPlayerId}");
                }
            }
            byte clientId = m_PlayerIds.GetForward(ipEndPoint);
            Container serverPlayer = serverSession.GetPlayer(clientId);
            if (isNewPlayer) SetupNewPlayer(serverSession, serverPlayer);
            return (clientId, serverPlayer);
        }

        protected void SetupNewPlayer(Container session, Container player)
        {
            GetMode(session).SpawnPlayer(player);
            // TODO:refactor zeroing

            player.ZeroIfHas<StatsComponent>();
            player.Require<ServerPingComponent>().Zero();
            player.Require<ClientStampComponent>().Reset();
            player.Require<ServerStampComponent>().Reset();
        }

        public override Ray GetRayForPlayerId(int playerId) => GetRayForPlayer(GetLatestSession().GetPlayer(playerId));

        protected override void RollbackHitboxes(int playerId)
        {
            float rtt = GetPlayerFromId(playerId).Require<ServerPingComponent>().rtt;
            for (var i = 0; i < m_Modifier.Length; i++)
            {
                int j = i;
                Container GetPlayerInHistory(int historyIndex) => m_SessionHistory.Get(-historyIndex).GetPlayer(j);

                Container rollbackPlayer = m_RollbackSession.GetPlayer(i);

                FloatProperty time = GetPlayerInHistory(0).Require<ServerStampComponent>().time;
                if (time.WithoutValue) continue;

                /* See: https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking */
                float rollback = DebugBehavior.Singleton.RollbackOverride.OrElse(GetLatestSession().Require<TickRateProperty>().TickInterval) * 3.6f + rtt;
                RenderInterpolatedPlayer<ServerStampComponent>(time - rollback, rollbackPlayer,
                                                               m_SessionHistory.Size, GetPlayerInHistory);

                m_Modifier[i].EvaluateHitboxes(i, rollbackPlayer);
                if (i == 0)
                    DebugBehavior.Singleton.Render(this, i, rollbackPlayer, new Color(0.0f, 0.0f, 1.0f, 0.3f));
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            m_Socket?.Dispose();
        }
    }
}