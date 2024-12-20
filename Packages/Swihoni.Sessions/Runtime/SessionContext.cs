using System.Collections.Generic;
using Swihoni.Components;
using Swihoni.Sessions.Components;
using Swihoni.Sessions.Config;
using Swihoni.Sessions.Modes;
using Swihoni.Sessions.Player.Components;
using UnityEngine;

namespace Swihoni.Sessions
{
    public readonly struct SessionContext
    {
        public readonly SessionBase session;
        public readonly Container entity, sessionContainer, commands;
        public readonly int playerId;
        public readonly Container player;
        public readonly uint timeUs, durationUs;
        public readonly int tickDelta;
        
        public SessionContext(SessionBase session = null, Container sessionContainer = null, Container commands = null,
                              int? playerId = null, Container player = null,
                              Container entity = null,
                              uint? timeUs = null, uint? durationUs = null, int? tickDelta = null, in SessionContext? existing = null)
        {
            if (existing is { } context)
            {
                this.session = session ?? context.session;
                this.entity = entity ?? context.entity;
                this.sessionContainer = sessionContainer ?? context.sessionContainer;
                this.commands = commands ?? context.commands;
                this.playerId = playerId ?? context.playerId;
                this.player = player ?? context.player;
                this.timeUs = timeUs ?? context.timeUs;
                this.durationUs = durationUs ?? context.durationUs;
                this.tickDelta = tickDelta ?? context.tickDelta;
            }
            else
            {
                this.session = session;
                this.entity = entity;
                this.sessionContainer = sessionContainer;
                this.commands = commands;
                this.playerId = playerId.GetValueOrDefault();
                this.player = player;
                this.timeUs = timeUs.GetValueOrDefault();
                this.durationUs = durationUs.GetValueOrDefault();
                this.tickDelta = tickDelta.GetValueOrDefault();
            }
        }

        public PhysicsScene PhysicsScene => session.PhysicsScene;
        public Color TeamColor => Mode.GetTeamColor(player.Require<TeamProperty>());
        public ModeBase Mode => ModeManager.GetMode(sessionContainer.Require<ModeIdProperty>());
        public ModeBase ModifyingMode => session.GetModifyingMode(sessionContainer);
        public Container ModifyingPlayer => session.GetModifyingPlayerFromId(playerId, sessionContainer);

        public Container GetModifyingPlayer(int otherPlayerId) => session.GetModifyingPlayerFromId(otherPlayerId, sessionContainer);
        public Container GetPlayer(int otherPlayerId) => sessionContainer.GetPlayer(otherPlayerId);

        public bool IsValidLocalPlayer(out Container localPlayer, out byte localPlayerId, bool needsToBeAlive = true)
        {
            var localPlayerIdProperty = sessionContainer.Require<LocalPlayerId>();
            if (localPlayerIdProperty.WithoutValue)
            {
                localPlayer = default;
                localPlayerId = default;
                return false;
            }
            localPlayerId = localPlayerIdProperty;
            localPlayer = GetModifyingPlayer(localPlayerId);
            HealthProperty health = localPlayer.Health();
            return health.WithValue && (!needsToBeAlive || health.IsAlive);
        }

        public bool WithServerStringCommands(out IEnumerable<string[]> stringCommands)
        {
            if (player.Without<ServerTag>() || commands.Without(out StringCommandProperty command) || !command.AsNewString(out string stringCommand))
            {
                stringCommands = default;
                return false;
            }
            stringCommands = stringCommand.GetArguments();
            return true;
        }
    }
}