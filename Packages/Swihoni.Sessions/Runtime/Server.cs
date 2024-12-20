using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using Swihoni.Components;
using Swihoni.Components.Networking;
using Swihoni.Sessions.Components;
using Swihoni.Sessions.Config;
using Swihoni.Sessions.Entities;
using Swihoni.Sessions.Modes;
using Swihoni.Sessions.Player.Components;
using Swihoni.Sessions.Player.Modifiers;
using Swihoni.Util;
using UnityEngine;
using UnityEngine.Profiling;
using Random = UnityEngine.Random;

namespace Swihoni.Sessions
{
    public class Server : NetworkedSessionBase, IReceiver
    {
        private ComponentServerSocket m_Socket;
        private readonly Container m_SendSession;

        public override ComponentSocketBase Socket => m_Socket;

        public Server(SessionElements elements, IPEndPoint ipEndPoint, SessionInjectorBase injector)
            : base(elements, ipEndPoint, injector)
        {
            foreach (ServerSessionContainer serverSession in m_SessionHistory)
            {
                serverSession.RegisterAppend(typeof(ServerTag));
                foreach (Container player in serverSession.Require<PlayerArray>())
                {
                    player.RegisterAppend(typeof(ServerTag), typeof(ServerPingComponent), typeof(HasSentInitialData));
                    m_Injector.OnPlayerRegisterAppend(player);
                }
            }
            m_SendSession = m_EmptyServerSession.Clone();
        }

        public override void Start()
        {
            base.Start();
            Random.InitState(Environment.TickCount);
            m_Socket = new ComponentServerSocket(IpEndPoint, m_Injector.OnServerNewConnection);
            m_Socket.Listener.PeerDisconnectedEvent += OnPeerDisconnected;
            m_Socket.Listener.NetworkLatencyUpdateEvent += OnLatencyUpdated;
            m_Socket.Receiver = this;
            RegisterMessages(m_Socket);
        }

        private void OnLatencyUpdated(NetPeer peer, int latency)
        {
            Container player = GetModifyingPlayerFromId(GetPeerPlayerId(peer));
            var ping = player.Require<ServerPingComponent>();
            ping.latencyUs.Value = checked((uint) latency * 1_000);
            if (player.With(out StatsComponent stats))
                stats.ping.Value = checked((ushort) (latency / 2));
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnect)
        {
            int playerId = GetPeerPlayerId(peer);
            Debug.LogWarning($"Dropping player with id: {playerId}, reason: {disconnect.Reason}, error code: {disconnect.SocketErrorCode}");
            Container player = GetModifyingPlayerFromId(playerId);
            try
            {
                m_Injector.OnServerLoseConnection(peer, player);
            }
            finally
            {
                player.Clear();
                GetPlayerModifier(player, playerId); // Force synchronization of behavior
            }
        }

        protected virtual void PreTick(Container tickSession) => m_Injector.OnPreTick(tickSession);

        protected virtual void PostTick(Container tickSession) => m_Injector.OnPostTick(tickSession);

        protected override void Render(uint renderTimeUs) { }

        protected sealed override void Tick(uint tick, uint timeUs, uint durationUs)
        {
            Profiler.BeginSample("Server Setup");
            Container previousServerSession = m_SessionHistory.Peek(),
                      serverSession = m_SessionHistory.ClaimNext();
            CopyFromPreviousSession(previousServerSession, serverSession);

            DefaultConfig.UpdateSessionConfig(serverSession);
            base.Tick(tick, timeUs, durationUs);

            var serverStamp = serverSession.Require<ServerStampComponent>();
            serverStamp.tick.Value = tick;
            serverStamp.timeUs.Value = timeUs;
            serverStamp.durationUs.Value = durationUs;
            Profiler.EndSample();

            Profiler.BeginSample("Server Tick");
            PreTick(serverSession);
            Tick(serverSession, tick, timeUs, durationUs); // Send
            PostTick(serverSession);
            // IterateClients(tick, time, duration, serverSession);
            Profiler.EndSample();

            Profiler.BeginSample("Server Clear Single Ticks");
            ElementExtensions.NavigateZipped(previousServerSession, serverSession, (_previous, _current) =>
            {
                if (_current.WithAttribute<SingleTickAttribute>())
                    _current.Clear();
                if (_current is PropertyBase _currentProperty)
                    _currentProperty.IsOverride = false;
                return Navigation.Continue;
            });
            Profiler.EndSample();
        }

        private static void CopyFromPreviousSession(ElementBase previous, ElementBase current)
        {
            ElementExtensions.NavigateZipped(previous, current, (_previous, _current) =>
            {
                if (_previous is PropertyBase _previousProperty && _current is PropertyBase _currentProperty)
                {
                    _currentProperty.SetTo(_previousProperty);
                    _currentProperty.IsOverride = _previousProperty.IsOverride;
                }
                return Navigation.Continue;
            });
        }

        protected virtual void ServerTick(Container serverSession, uint timeUs, uint durationUs) { }

        void IReceiver.OnReceive(NetPeer fromPeer, NetDataReader reader, byte code)
        {
            try
            {
                Container serverSession = GetLatestSession();
                int clientId = GetPeerPlayerId(fromPeer);
                Container serverPlayer = GetModifyingPlayerFromId(clientId);
                switch (code)
                {
                    case ClientCommandsCode:
                    {
                        if (IsLoading) break;

                        m_EmptyClientCommands.Deserialize(reader);
                        if (CanSetupNewPlayer(serverPlayer))
                        {
                            Debug.Log($"[{GetType().Name}] Setting up new player for connection: {fromPeer.Address}, allocated id is: {clientId}");
                            var modifyContext = new SessionContext(this, serverSession, playerId: clientId, player: serverPlayer);
                            SetupNewPlayer(modifyContext);
                        }
                        HandleClientCommand(clientId, m_EmptyClientCommands, serverSession, serverPlayer);
                        break;
                    }
                    case DebugClientViewCode:
                    {
#if !VOXELFIELD_RELEASE_SERVER
                        var clientView = new ClientCommandsContainer(m_EmptyDebugClientView.ElementTypes);
                        clientView.Deserialize(reader);
                        var context = new SessionContext(this, playerId: clientId, player: clientView);
                        DebugBehavior.Singleton.Render(context, new Color(1.0f, 0.0f, 0.0f, 0.3f));
#endif
                        break;
                    }
                    default:
                    {
                        m_Injector.OnReceiveCode(fromPeer, reader, code);
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"Exception while handling packet from: {fromPeer.Address}: {exception}");
                DisconnectPeerSafely("Internal server error", fromPeer);
            }
        }

        private void DisconnectPeerSafely(string reason, NetPeer peer)
        {
            try
            {
                var writer = new NetDataWriter();
                writer.Put(reason);
                m_Socket.NetworkManager.DisconnectPeer(peer, writer);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Exception disconnecting graciously: {peer.Address}: {exception}");
                m_Socket.NetworkManager.DisconnectPeerForce(peer);
            }
        }

        private bool CanSetupNewPlayer(Container serverPlayer) => m_Injector.ShouldSetupPlayer(serverPlayer);

        private static uint _timeUs, _durationUs;

        private void Tick(Container serverSession, uint tick, uint timeUs, uint durationUs)
        {
            ServerTick(serverSession, timeUs, durationUs);
            m_Socket.PollEvents();
            PhysicsScene.Simulate(durationUs * TimeConversions.MicrosecondToSecond);

            if (!IsLoading)
            {
                _session = this; // Prevent closure allocation
                _timeUs = timeUs;
                _durationUs = durationUs;
                _container = serverSession;
                try
                {
                    EntityManager.ModifyAll(serverSession, (modifer, _, entity) =>
                    {
                        var entityModifier = (EntityModifierBehavior) modifer;
                        var modifyContext = new SessionContext(_session, _container, entity: entity, timeUs: _timeUs, durationUs: _durationUs);
                        entityModifier.Modify(modifyContext);
                    });
                    GetModifyingMode(serverSession).Modify(new SessionContext(_session, _container, timeUs: _timeUs, durationUs: _durationUs));
                }
                catch (Exception exception)
                {
                    L.Exception(exception, "Exception modifying session");
                }
            }

            SendServerSession(serverSession);
        }

        private readonly List<NetPeer> m_ConnectedPeers = new();

        private void SendServerSession(Container serverSession)
        {
            m_Socket.NetworkManager.GetPeersNonAlloc(m_ConnectedPeers, ConnectionState.Connected);
            foreach (NetPeer peer in m_ConnectedPeers)
                SendPeerLatestSession(peer, serverSession);
        }

        protected void SendPeerLatestSession(NetPeer peer, Container serverSession)
        {
            int playerId = GetPeerPlayerId(peer);
            Container player = GetModifyingPlayerFromId(playerId, serverSession);

            if (player.Health().WithValue)
            {
                var localPlayerProperty = serverSession.Require<LocalPlayerId>();
                localPlayerProperty.Value = (byte) playerId;

                // uint lastServerTickAcknowledged = player.Require<AcknowledgedServerTickProperty>().Else(0u);
                // var rollback = checked((int) (tick - lastServerTickAcknowledged));
                // if (lastServerTickAcknowledged == 0u)
                //     // m_SendSession.CopyFrom(serverSession);
                //     CopyToSend(serverSession);
                // else
                //     // TODO:performance serialize and compress at the same time
                //     CompressSession(serverSession, rollback);

                CopyToSend(serverSession);

                if (player.Require<ClientStampComponent>().tick.WithValue)
                {
                    BoolProperty hasSentInitialData = player.Require<HasSentInitialData>();
                    if (!hasSentInitialData)
                    {
                        m_Injector.OnSendInitialData(peer, serverSession, m_SendSession);
                        hasSentInitialData.Set();
                    }
                }

                m_Socket.Send(m_SendSession, peer, DeliveryMethod.ReliableUnordered);
            }
        }

        private void CopyToSend(ElementBase serverSession)
            => ElementExtensions.NavigateZipped(m_SendSession, serverSession, (_send, _server) =>
            {
                if (_send is PropertyBase _sendProperty && _server is PropertyBase _serverProperty)
                {
                    _sendProperty.SetTo(_serverProperty);
                    _sendProperty.IsOverride = _serverProperty.IsOverride;
                }
                return Navigation.Continue;
            });

        // Not working, prediction errors on ground tick and position
        private void CompressSession(ElementBase serverSession, int rollback)
            => ElementExtensions.NavigateZipped(serverSession, m_SessionHistory.Get(-1), m_SendSession, (_mostRecent, _lastAcknowledged, _send) =>
            {
                if (_mostRecent is PropertyBase _mostRecentProperty && _lastAcknowledged is PropertyBase _lastAcknowledgedProperty && _send is PropertyBase _sendProperty)
                {
                    if (_mostRecent.WithoutAttribute<SingleTickAttribute>()
                     && _mostRecent.WithoutAttribute<NeverCompress>()
                     && !(_mostRecentProperty is VectorProperty)
                     && !(_mostRecentProperty is StringProperty)
                     && _mostRecentProperty.Equals(_lastAcknowledgedProperty))
                    {
                        _sendProperty.Clear();
                        _sendProperty.WasSame = true;
                    }
                    else
                    {
                        _sendProperty.SetTo(_mostRecentProperty);
                        _sendProperty.IsOverride = _mostRecentProperty.IsOverride;
                        _sendProperty.WasSame = false;
                    }
                }
                return Navigation.Continue;
            });

        private void HandleClientCommand(int clientId, Container receivedClientCommands, Container serverSession, Container serverPlayer)
        {
            UIntProperty serverPlayerTimeUs = serverPlayer.Require<ServerStampComponent>().timeUs;
            var clientStamp = receivedClientCommands.Require<ClientStampComponent>();
            var serverStamp = serverSession.Require<ServerStampComponent>();
            var serverPlayerClientStamp = serverPlayer.Require<ClientStampComponent>();
            // Clients start to tag with ticks once they receive their first server player state
            if (clientStamp.tick.WithValue)
            {
                if (serverPlayerClientStamp.tick.WithoutValue)
                    // Take one tick to set initial server player client stamp
                    serverPlayerClientStamp.MergeFrom(clientStamp);
                else
                {
                    // Make sure this is the newest tick
                    var tickDelta = checked((int) (clientStamp.tick - (long) serverPlayerClientStamp.tick));
                    bool isLatestTick = tickDelta >= 1;
                    ModeBase mode = GetModifyingMode(serverSession);
                    if (isLatestTick)
                    {
                        checked
                        {
                            serverPlayerTimeUs.Value += clientStamp.timeUs - serverPlayerClientStamp.timeUs;

                            long deltaUs = serverPlayerTimeUs.Value - (long) serverStamp.timeUs;
                            if (Math.Abs(deltaUs) > serverSession.Require<TickRateProperty>().TickIntervalUs * 3u)
                            {
                                ResetErrors++;
                                serverPlayerTimeUs.Value = serverStamp.timeUs;
                            }
                        }
                        MergeTrustedFromCommands(serverPlayer, receivedClientCommands);
                    }
#if !VOXELFIELD_RELEASE_SERVER
                    else Debug.LogWarning($"[{GetType().Name}] Received out of order command from client: {clientId}");
#endif

                    var context = new SessionContext(this, serverSession, receivedClientCommands, clientId, serverPlayer,
                                                     durationUs: clientStamp.durationUs, tickDelta: tickDelta);
                    try
                    {
                        GetPlayerModifier(serverPlayer, clientId).ModifyChecked(context);
                        mode.ModifyPlayer(context);
                        m_Injector.ModifyPlayer(context);
                    }
                    catch (Exception exception)
                    {
                        L.Exception(exception, $"Exception modifying checked player: {context.playerId}");
                    }
                }
            }
            else serverPlayerTimeUs.Value = serverStamp.timeUs;
        }

        private static void MergeTrustedFromCommands(ElementBase serverPlayer, ElementBase receivedClientCommands)
            => ElementExtensions.NavigateZipped(serverPlayer, receivedClientCommands, (_server, _client) =>
            {
                if (_client.WithAttribute<ClientTrustedAttribute>())
                {
                    _server.SetTo(_client);
                    return Navigation.SkipDescendents;
                }
                return Navigation.Continue;
            });

        protected void SetupNewPlayer(in SessionContext context)
        {
            context.ModifyingMode.SetupNewPlayer(context);

            Container player = context.player;
            player.Require<HasSentInitialData>().Zero();
            player.Require<ServerPingComponent>().Zero();
            player.Require<UsernameProperty>().SetTo(m_Injector.GetUsername(context));
        }

        public override Ray GetRayForPlayerId(int playerId) => GetLatestSession().GetPlayer(playerId).GetRayForPlayer();

        protected override void RollbackHitboxes(in SessionContext context)
        {
            uint latencyUs = GetModifyingPlayerFromId(context.playerId).Require<ServerPingComponent>().latencyUs;
            for (var modifierId = 0; modifierId < MaxPlayers; modifierId++)
            {
                _indexer = modifierId;
                _serverHistory = m_SessionHistory;
                Container GetPlayerInHistory(int historyIndex) => _serverHistory.Get(-historyIndex).GetPlayer(_indexer);

                Container rollbackPlayer = m_RollbackSession.GetPlayer(modifierId);
                // UIntProperty timeUs = GetPlayerInHistory(0).Require<ServerStampComponent>().timeUs;
                UIntProperty timeUs = GetLatestSession().Require<ServerStampComponent>().timeUs;
                if (timeUs.WithoutValue) continue;

                checked
                {
                    /* See: https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking */
                    uint tickIntervalUs = DebugBehavior.Singleton.RollbackOverrideUs.Else(GetLatestSession().Require<TickRateProperty>().PlayerRenderIntervalUs * 2),
                         rollbackUs = tickIntervalUs + latencyUs;
                    RenderInterpolatedPlayer<ServerStampComponent>(timeUs - rollbackUs, rollbackPlayer,
                                                                   m_SessionHistory.Size, GetPlayerInHistory);
                }
                PlayerModifierDispatcherBehavior modifier = GetPlayerModifier(rollbackPlayer, modifierId);
                var playerContext = new SessionContext(existing: context, playerId: modifierId, player: rollbackPlayer);
                if (modifier) modifier.EvaluateHitboxes(playerContext);

                if (modifierId == 0) DebugBehavior.Singleton.Render(playerContext, new Color(0.0f, 0.0f, 1.0f, 0.3f));
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            m_Socket?.Dispose();
        }
    }
}