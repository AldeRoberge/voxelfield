using System;
using System.Linq;
using System.Net;
using LiteNetLib;
using Swihoni.Collections;
using Swihoni.Components;
using Swihoni.Components.Networking;
using Swihoni.Sessions.Components;
using Swihoni.Sessions.Player.Components;
using Swihoni.Sessions.Player.Modifiers;
using Swihoni.Sessions.Player.Visualization;
using UnityEngine;
using UnityEngine.Profiling;

namespace Swihoni.Sessions
{
    public sealed class Client : NetworkedSessionBase
    {
        private readonly string m_ConnectKey;
        private readonly CyclicArray<ClientCommandsContainer> m_CommandHistory;
        private readonly CyclicArray<Container> m_PlayerPredictionHistory;
        private ComponentClientSocket m_Socket;

        public int PredictionErrors { get; private set; }
        public override ComponentSocketBase Socket => m_Socket;

        public Client(SessionElements elements, IPEndPoint ipEndPoint, string connectKey, SessionInjectorBase injector)
            : base(elements, ipEndPoint, injector)
        {
            m_ConnectKey = connectKey;
            /* Prediction */
            m_CommandHistory = new CyclicArray<ClientCommandsContainer>(HistoryCount, () => m_EmptyClientCommands.Clone());
            // TODO:refactor zeroing
            ZeroCommand(m_CommandHistory.Peek());
            m_PlayerPredictionHistory = new CyclicArray<Container>(HistoryCount, () => new Container(elements.playerElements.Append(typeof(ClientStampComponent))));
            Container firstPrediction = m_PlayerPredictionHistory.Peek();
            firstPrediction.Zero();
            firstPrediction.Require<ClientStampComponent>().Reset();

            foreach (ServerSessionContainer session in m_SessionHistory)
            {
                session.RegisterAppend(typeof(LocalizedClientStampComponent));
                foreach (Container player in session.Require<PlayerContainerArrayElement>())
                    player.RegisterAppend(typeof(LocalizedClientStampComponent));
            }
        }

        public override void Start()
        {
            base.Start();
            m_Socket = new ComponentClientSocket(IpEndPoint, m_ConnectKey);
            m_Socket.Listener.PeerDisconnectedEvent += OnDisconnect;
            RegisterMessages(m_Socket);
        }

        private void OnDisconnect(NetPeer peer, DisconnectInfo disconnect)
        {
            if (disconnect.AdditionalData.TryGetString(out string reason))
                Debug.Log($"Disconnected for reason: {reason}");
            Dispose();
        }

        private void UpdateInputs(Container player, int localPlayerId)
        {
            ClientCommandsContainer commands = m_CommandHistory.Peek();
            GetPlayerModifier(player, localPlayerId).ModifyCommands(this, commands);
            ForEachSessionInterface(@interface => @interface.ModifyLocalTrusted(localPlayerId, this, commands));
        }

        protected override void Input(uint timeUs, uint deltaUs)
        {
            Container latestSession = GetLatestSession();
            if (!GetLocalPlayerId(latestSession, out int localPlayerId))
                return;
            Container player = GetPlayerFromId(localPlayerId, latestSession);
            UpdateInputs(player, localPlayerId);
            GetPlayerModifier(player, localPlayerId).ModifyTrusted(this, localPlayerId, m_CommandHistory.Peek(), player, m_CommandHistory.Peek(), deltaUs);
        }

        protected override void Render(uint renderTimeUs)
        {
            if (m_RenderSession.Without(out PlayerContainerArrayElement renderPlayers)
             || m_RenderSession.Without(out LocalPlayerId localPlayer)
             || !GetLocalPlayerId(GetLatestSession(), out int localPlayerId))
                return;

            var tickRate = GetLatestSession().Require<TickRateProperty>();
            if (tickRate.WithoutValue) return;

            m_RenderSession.CopyFrom(GetLatestSession());
            localPlayer.Value = (byte) localPlayerId;

            for (var playerId = 0; playerId < renderPlayers.Length; playerId++)
            {
                bool isLocalPlayer = playerId == localPlayerId;
                Container renderPlayer = renderPlayers[playerId];
                if (isLocalPlayer)
                {
                    Container GetInHistory(int historyIndex) => m_PlayerPredictionHistory.Get(-historyIndex);
                    uint playerRenderTimeUs = renderTimeUs - tickRate.TickIntervalUs;
                    RenderInterpolatedPlayer<ClientStampComponent>(playerRenderTimeUs, renderPlayer, m_PlayerPredictionHistory.Size, GetInHistory);
                    renderPlayer.MergeFrom(m_CommandHistory.Peek());
                    // localPlayerRenderComponent.MergeSet(DebugBehavior.Singleton.RenderOverride);
                }
                else
                {
                    int copiedPlayerId = playerId;
                    Container GetInHistory(int historyIndex) => m_SessionHistory.Get(-historyIndex).Require<PlayerContainerArrayElement>()[copiedPlayerId];

                    uint playerRenderTimeUs = renderTimeUs - tickRate.PlayerRenderIntervalUs;
                    RenderInterpolatedPlayer<LocalizedClientStampComponent>(playerRenderTimeUs, renderPlayer, m_SessionHistory.Size, GetInHistory);
                }
                PlayerVisualsDispatcherBehavior visuals = GetPlayerVisuals(renderPlayer, playerId);
                if (visuals) visuals.Render(this, m_RenderSession, playerId, renderPlayer, isLocalPlayer);
            }
            RenderInterfaces(m_RenderSession);
            RenderEntities<LocalizedClientStampComponent>(renderTimeUs, tickRate.TickIntervalUs * 2u);
        }

        protected override void Tick(uint tick, uint timeUs, uint durationUs)
        {
            Profiler.BeginSample("Client Predict");
            Container latestSession = GetLatestSession();
            if (GetLocalPlayerId(latestSession, out int localPlayerId))
            {
                m_Injector.OnSettingsTick(latestSession);
                UpdateInputs(GetPlayerFromId(localPlayerId, latestSession), localPlayerId);
                Predict(tick, timeUs, localPlayerId); // Advances commands
            }
            Profiler.EndSample();

            Profiler.BeginSample("Client Send");
            SendCommand();
            Profiler.EndSample();

            Profiler.BeginSample("Client Receive");
            Receive(timeUs);
            Profiler.EndSample();
            
            ClearSingleTicks(m_CommandHistory.Peek());

            base.Tick(tick, timeUs, durationUs);
        }

        public static void ClearSingleTicks(ElementBase commands)
        {
            commands.Navigate(_element =>
            {
                if (_element is PropertyBase _property && _property.WithAttribute<SingleTick>())
                    _property.Clear();
                return Navigation.Continue;
            });
        }

        private void Predict(uint tick, uint timeUs, int localPlayerId)
        {
            Container previousPredictedPlayer = m_PlayerPredictionHistory.Peek(),
                      predictedPlayer = m_PlayerPredictionHistory.ClaimNext();
            ClientCommandsContainer previousCommand = m_CommandHistory.Peek(),
                                    commands = m_CommandHistory.ClaimNext();
            if (predictedPlayer.Without(out ClientStampComponent predictedStamp)) return;

            predictedPlayer.CopyFrom(previousPredictedPlayer);
            commands.CopyFrom(previousCommand);

            if (IsPaused)
            {
                commands.Require<ClientStampComponent>().Reset();
            }
            else
            {
                predictedStamp.tick.Value = tick;
                predictedStamp.timeUs.Value = timeUs;
                var previousClientStamp = previousPredictedPlayer.Require<ClientStampComponent>();
                if (previousClientStamp.timeUs.WithValue)
                {
                    uint lastTime = previousClientStamp.timeUs.Else(timeUs),
                         durationUs = timeUs - lastTime;
                    predictedStamp.durationUs.Value = durationUs;
                }

                // Inject trusted component
                commands.Require<ClientStampComponent>().CopyFrom(predictedStamp);
                predictedPlayer.MergeFrom(commands);
                if (!IsPaused && predictedStamp.durationUs.WithValue)
                {
                    PlayerModifierDispatcherBehavior modifier = GetPlayerModifier(predictedPlayer, localPlayerId);
                    if (modifier) modifier.ModifyChecked(this, localPlayerId, predictedPlayer, commands, predictedStamp.durationUs);
                }
            }
        }
        
        private void SendCommand() => m_Socket.SendToServer(m_CommandHistory.Peek(), DeliveryMethod.ReliableUnordered);

        private void CheckPrediction(Container serverSession)
        {
            if (!GetLocalPlayerId(serverSession, out int localPlayerId))
                return;

            Container serverPlayer = serverSession.GetPlayer(localPlayerId);
            UIntProperty targetTick = serverPlayer.Require<ClientStampComponent>().tick;

            if (targetTick.WithoutValue)
                return;
            for (var playerHistoryIndex = 0; playerHistoryIndex < m_PlayerPredictionHistory.Size; playerHistoryIndex++)
            {
                Container predictedPlayer = m_PlayerPredictionHistory.Get(-playerHistoryIndex);
                if (predictedPlayer.Require<ClientStampComponent>().tick != targetTick) continue;
                /* We are checking predicted */
                var areEqual = true;
                Container latestPredictedPlayer = m_PlayerPredictionHistory.Peek();
                ElementExtensions.NavigateZipped((_predicted, _latestPredicted, _server) =>
                {
                    if (_predicted.WithAttribute<OnlyServerTrustedAttribute>())
                    {
                        _latestPredicted.MergeFrom(_server);
                        return Navigation.SkipDescendents;
                    }
                    if (_predicted.WithAttribute<ClientTrustedAttribute>())
                        return Navigation.SkipDescendents;
                    switch (_predicted)
                    {
                        case FloatProperty f1 when _server is FloatProperty f2 && f1.TryAttribute(out PredictionToleranceAttribute fPredictionToleranceAttribute)
                                                                               && !f1.CheckWithinTolerance(f2, fPredictionToleranceAttribute.tolerance):
                        case VectorProperty v1 when _server is VectorProperty v2 && v1.TryAttribute(out PredictionToleranceAttribute vPredictionToleranceAttribute)
                                                                                 && !v1.CheckWithinTolerance(v2, vPredictionToleranceAttribute.tolerance):
                        case PropertyBase p1 when _server is PropertyBase p2 && !p1.Equals(p2):
                            areEqual = false;
                            Debug.LogWarning($"Prediction error with {_predicted.GetType().Name} with predicted: {_predicted} and verified: {_server}");
                            return Navigation.Exit;
                    }
                    return Navigation.Continue;
                }, predictedPlayer, latestPredictedPlayer, serverPlayer);
                if (areEqual) break;
                /* We did not predict properly */
                PredictionErrors++;
                // Place base from verified server
                predictedPlayer.CopyFrom(serverPlayer);
                // Replay old commands up until most recent to get back on track
                for (int commandHistoryIndex = playerHistoryIndex - 1; commandHistoryIndex >= 0; commandHistoryIndex--)
                {
                    ClientCommandsContainer commands = m_CommandHistory.Get(-commandHistoryIndex);
                    Container pastPredictedPlayer = m_PlayerPredictionHistory.Get(-commandHistoryIndex);
                    ClientStampComponent stamp = pastPredictedPlayer.Require<ClientStampComponent>().Clone(); // TODO:performance remove clone
                    pastPredictedPlayer.CopyFrom(m_PlayerPredictionHistory.Get(-commandHistoryIndex - 1));
                    pastPredictedPlayer.Require<ClientStampComponent>().CopyFrom(stamp);
                    PlayerModifierDispatcherBehavior localPlayerModifier = GetPlayerModifier(pastPredictedPlayer, localPlayerId);
                    if (commands.Require<ClientStampComponent>().durationUs.WithValue)
                    {
                        localPlayerModifier.ModifyChecked(this, localPlayerId, pastPredictedPlayer, commands, commands.Require<ClientStampComponent>().durationUs);
                    }
                    else
                    {
                        Debug.LogError("Should not happen");
                    }
                }
                break;
            }
        }

        private void Receive(uint timeUs)
        {
            m_Socket.PollReceived((peer, message) =>
            {
                switch (message)
                {
                    case ServerSessionContainer receivedServerSession:
                    {
                        Profiler.BeginSample("Client Receive Setup");
                        ServerSessionContainer previousServerSession = m_SessionHistory.Peek();

                        uint serverTick = receivedServerSession.Require<ServerStampComponent>().tick;
                        UIntProperty previousServerTick = previousServerSession.Require<ServerStampComponent>().tick;
                        ServerSessionContainer serverSession;
                        var isMostRecent = true;
                        if (previousServerTick.WithValue)
                        {
                            var delta = checked((int) (serverTick - (long) previousServerTick));
                            if (delta > 0)
                            {
                                m_CommandHistory.Peek().Require<AcknowledgedServerTickProperty>().Value = serverTick;
                                for (var i = 0; i < delta - 1; i++) // We skipped tick(s). Reserve spaces to fill later
                                {
                                    ServerSessionContainer reserved = m_SessionHistory.ClaimNext();
                                    reserved.CopyFrom(previousServerSession);
                                }
                                serverSession = m_SessionHistory.ClaimNext();
                            }
                            else
                            {
                                // We received an old tick. Fill in history
                                serverSession = m_SessionHistory.Get(delta);
                                Debug.LogWarning($"[{GetType().Name}] Received out of order server update");
                                isMostRecent = false;
                            }
                        }
                        else serverSession = m_SessionHistory.ClaimNext();

                        serverSession.CopyFrom(previousServerSession);
                        UpdateCurrentSessionFromReceived(serverSession, receivedServerSession);
                        Profiler.EndSample();

                        m_Injector.OnReceive(serverSession);

                        if (!isMostRecent)
                        {
                            Debug.LogWarning("Is not most recent!");
                            return;
                        }

                        {
                            // TODO:refactor make class
                            UIntProperty serverTimeUs = serverSession.Require<ServerStampComponent>().timeUs,
                                         localizedServerTimeUs = serverSession.Require<LocalizedClientStampComponent>().timeUs;

                            if (localizedServerTimeUs.WithValue)
                                localizedServerTimeUs.Value += checked(serverTimeUs - previousServerSession.Require<ServerStampComponent>().timeUs);
                            else localizedServerTimeUs.Value = timeUs;

                            long delta = localizedServerTimeUs.Value - (long) timeUs;
                            if (Math.Abs(delta) > serverSession.Require<TickRateProperty>().TickIntervalUs * 3u)
                            {
                                ResetErrors++;
                                localizedServerTimeUs.Value = timeUs;
                            }
                        }

                        Profiler.BeginSample("Client Update Players");
                        var serverPlayers = serverSession.Require<PlayerContainerArrayElement>();
                        for (var playerId = 0; playerId < serverPlayers.Length; playerId++)
                        {
                            Container serverPlayer = serverPlayers[playerId];
                            var healthProperty = serverPlayer.Require<HealthProperty>();
                            UIntProperty localizedServerTimeUs = serverPlayer.Require<LocalizedClientStampComponent>().timeUs;
                            if (healthProperty.WithoutValue)
                                localizedServerTimeUs.Clear(); // Is something a client only has so we have to clear it
                            if (healthProperty.WithoutValue || healthProperty.IsDead)
                                continue;
                            /* Valid player */

                            UIntProperty serverTimeUs = serverPlayer.Require<ServerStampComponent>().timeUs;

                            if (localizedServerTimeUs.WithValue)
                            {
                                uint previousTimeUs = previousServerSession.GetPlayer(playerId).Require<ServerStampComponent>().timeUs;
                                localizedServerTimeUs.Value += checked(serverTimeUs - previousTimeUs);
                            }
                            else localizedServerTimeUs.Value = timeUs;

                            GetLocalPlayerId(serverSession, out int localPlayerId);
                            if (playerId != localPlayerId) GetPlayerModifier(serverPlayer, playerId).Synchronize(serverPlayer);

                            long delta = localizedServerTimeUs.Value - (long) timeUs;
                            if (Math.Abs(delta) > serverSession.Require<TickRateProperty>().TickIntervalUs * 3u)
                            {
                                ResetErrors++;
                                localizedServerTimeUs.Value = timeUs;
                            }
                        }
                        Profiler.EndSample();

                        // Debug.Log($"{receivedServerSession.Require<ServerStampComponent>().time} {trackedTime.Value}");

                        Profiler.BeginSample("Client Check Prediction");
                        CheckPrediction(serverSession);
                        Profiler.EndSample();

                        break;
                    }
                }
            });
        }

        private static void UpdateCurrentSessionFromReceived(ElementBase serverSession, ElementBase receivedServerSession)
        {
            ElementExtensions.NavigateZipped((_current, _received) =>
            {
                if (_current is PropertyBase _currentProperty && _received is PropertyBase _receivedProperty)
                {
                    if (_current.WithAttribute<SingleTick>() || !_receivedProperty.WasSame)
                        _currentProperty.SetTo(_receivedProperty);
                }
                return Navigation.Continue;
            }, serverSession, receivedServerSession);
        }

        private static bool GetLocalPlayerId(Container session, out int localPlayerId)
        {
            if (session.With(out LocalPlayerId localPlayerProperty) && localPlayerProperty.WithValue)
            {
                localPlayerId = localPlayerProperty;
                return true;
            }
            localPlayerId = default;
            return false;
        }

        public override Ray GetRayForPlayerId(int playerId) => GetRayForPlayer(m_PlayerPredictionHistory.Peek());

        protected override void RollbackHitboxes(int playerId)
        {
            if (!DebugBehavior.Singleton.IsDebugMode) return;
            for (var i = 0; i < MaxPlayers; i++)
            {
                // int copiedPlayerId = i;
                // Container GetInHistory(int historyIndex) => m_SessionHistory.Get(-historyIndex).GetPlayer(copiedPlayerId);
                //
                // Container render = m_RenderSession.GetPlayer(i).Clone();
                //
                // float rollback = DebugBehavior.Singleton.RollbackOverride.OrElse(GetSettings().TickInterval) * 3;
                // RenderInterpolatedPlayer<LocalizedClientStampComponent>(Time.realtimeSinceStartup - rollback, render, m_SessionHistory.Size, GetInHistory);
                //
                // PlayerModifierDispatcherBehavior modifier = m_Modifier[i];
                // modifier.EvaluateHitboxes(i, render);

                Container recentPlayer = ((PlayerVisualsDispatcherBehavior) PlayerManager.UnsafeVisuals[i]).GetRecentPlayer();

                if (i == 0 && recentPlayer != null) SendDebug(recentPlayer);
            }
        }

        private void SendDebug(Container player)
        {
            DebugClientView debug = m_EmptyDebugClientView.Clone();
            debug.CopyFrom(player);
            m_Socket.SendToServer(debug, DeliveryMethod.ReliableOrdered);
        }

        public override void Dispose()
        {
            base.Dispose();
            m_Socket?.Dispose();
        }
    }
}