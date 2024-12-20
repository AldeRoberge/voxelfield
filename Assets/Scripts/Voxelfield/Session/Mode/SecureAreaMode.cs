using System;
using System.Collections.Generic;
using System.Linq;
using Swihoni.Components;
using Swihoni.Sessions;
using Swihoni.Sessions.Components;
using Swihoni.Sessions.Config;
using Swihoni.Sessions.Entities;
using Swihoni.Sessions.Items.Modifiers;
using Swihoni.Sessions.Modes;
using Swihoni.Sessions.Player;
using Swihoni.Sessions.Player.Components;
using Swihoni.Sessions.Player.Modifiers;
using Swihoni.Util.Math;
using UnityEngine;
using Voxels.Map;
using Random = UnityEngine.Random;

namespace Voxelfield.Session.Mode
{
    [Serializable, Config(name: "sa")]
    public class SecureAreaConfig : ComponentBase
    {
        [Config(ConfigType.Mode, "end_duration")] public UIntProperty roundEndDurationUs;
        [Config(ConfigType.Mode, "duration")] public UIntProperty roundDurationUs;
        [Config(ConfigType.Mode, "buy_duration")] public UIntProperty buyDurationUs;
        [Config(ConfigType.Mode, "secure_duration")] public UIntProperty secureDurationUs;
        [Config(ConfigType.Mode)] public ByteProperty playerCount;
        [Config(ConfigType.Mode, "win_bonus")] public UShortProperty roundWinMoney;
        [Config(ConfigType.Mode, "lose_bonus")] public UShortProperty roundLoseMoney;
        [Config(ConfigType.Mode, "kill_bonus")] public UShortProperty killMoney;
        [Config(ConfigType.Mode)] public ByteProperty maxRounds;
    }

    [CreateAssetMenu(fileName = "Secure Area", menuName = "Session/Mode/Secure Area", order = 0)]
    public class SecureAreaMode : DeathmatchMode, IModeWithBuying
    {
        private const byte BlueTeam = 0, RedTeam = 1;
        private const int MaxMoney = 7000;

        [SerializeField] private Color m_BlueColor = new(0.1764705882f, 0.5098039216f, 0.8509803922f),
                                       m_RedColor = new(0.8196078431f, 0.2156862745f, 0.1960784314f);
        [SerializeField] private LayerMask m_PlayerTriggerMask = default;
        [SerializeField] private ushort[] m_ItemPrices = default;

        private MapManager m_LastMapManager;
        private readonly Collider[] m_CachedColliders = new Collider[SessionBase.MaxPlayers];
        private SecureAreaConfig m_Config;

        public uint SecureDurationUs => m_Config.secureDurationUs;
        public uint RoundDurationUs => m_Config.roundDurationUs;
        public uint BuyDurationUs => m_Config.buyDurationUs;
        public uint RoundEndDurationUs => m_Config.roundEndDurationUs;

        public override void Initialize()
        {
            m_Config = Config.Active.secureAreaConfig;
            SessionBase.RegisterSessionCommand("give_money");
            _randomIndices = Enumerable.Range(0, 2).Select(_ => new List<int>()).ToArray();
        }

        private SiteBehavior[] GetSiteBehaviors(in SessionContext context)
        {
            MapManager mapManager = context.GetMapManager();
            return mapManager.Models.Values
                             .Where(model => model.Container.Require<ModelIdProperty>() == ModelsProperty.Site)
                             .Cast<SiteBehavior>().ToArray();
        }

        public override uint GetItemEntityLifespanUs(in SessionContext context)
            => context.sessionContainer.Require<SecureAreaComponent>().roundTime.WithValue ? uint.MaxValue : base.GetItemEntityLifespanUs(context);

        protected override void HandleAutoRespawn(in SessionContext context, HealthProperty health)
        {
            if (context.sessionContainer.Require<SecureAreaComponent>().roundTime.WithValue)
            {
                // TODO:refactor this snippet is used multiple times
                if (health.IsAlive || context.player.Without(out RespawnTimerProperty respawn)) return;
                respawn.Subtract(context.durationUs);
            }
            else base.HandleAutoRespawn(in context, health);
        }

        protected override void KillPlayer(in DamageContext damageContext)
        {
            base.KillPlayer(damageContext);

            Container killer = damageContext.InflictingPlayer;
            if (damageContext.sessionContext.player.Require<TeamProperty>() != killer.Require<TeamProperty>())
            {
                UShortProperty money = killer.Require<MoneyComponent>().count;
                money.IncrementCapped(m_Config.killMoney, MaxMoney);
            }
        }

        public override void Modify(in SessionContext context)
        {
            base.Modify(context);

            Container sessionContainer = context.sessionContainer;
            uint durationUs = context.durationUs;
            var secureArea = sessionContainer.Require<SecureAreaComponent>();

            var players = sessionContainer.Require<PlayerArray>();
            int activePlayerCount = 0, redAlive = 0, blueAlive = 0;
            foreach (Container player in players)
            {
                HealthProperty health = player.Health();
                if (health.WithoutValue) continue;

                activePlayerCount++;
                if (health.IsDead) continue;

                if (player.Require<TeamProperty>().TryWithValue(out byte team))
                    if (team == RedTeam) redAlive++;
                    else if (team == BlueTeam) blueAlive++;
            }

            SiteBehavior[] siteBehaviors = GetSiteBehaviors(context);
            if (secureArea.roundTime.WithValue)
            {
                bool runTimer = true, redJustSecured = false, endedWithKills = false;
                bool inFightTime = secureArea.roundTime >= m_Config.roundEndDurationUs && secureArea.roundTime < m_Config.roundEndDurationUs + m_Config.roundDurationUs;
                if (inFightTime)
                {
                    if (activePlayerCount > 1)
                    {
                        if (redAlive == 0)
                        {
                            sessionContainer.Require<DualScoresArray>()[BlueTeam].Value++;
                            secureArea.lastWinningTeam.Value = BlueTeam;
                        }
                        if (blueAlive == 0)
                        {
                            sessionContainer.Require<DualScoresArray>()[RedTeam].Value++;
                            secureArea.lastWinningTeam.Value = RedTeam;
                        }
                        if (redAlive == 0 || blueAlive == 0)
                        {
                            secureArea.roundTime.Value = m_Config.roundEndDurationUs;
                            endedWithKills = true;
                        }
                    }
                    else
                    {
                        if (redAlive == 0 && blueAlive == 0)
                        {
                            sessionContainer.Require<DualScoresArray>()[BlueTeam].Value++;
                            secureArea.lastWinningTeam.Value = BlueTeam;
                            secureArea.roundTime.Value = m_Config.roundEndDurationUs;

                            endedWithKills = true;
                        }
                    }

                    if (!endedWithKills)
                    {
                        for (var siteIndex = 0; siteIndex < siteBehaviors.Length; siteIndex++)
                        {
                            SiteBehavior siteBehavior = siteBehaviors[siteIndex];
                            Transform siteTransform = siteBehavior.transform;
                            SiteComponent site = secureArea.sites[siteIndex];
                            Vector3 bounds = siteBehavior.Container.Require<ExtentsProperty>();
                            int playersInsideCount =
                                context.PhysicsScene.OverlapBox(siteTransform.position, bounds / 2, m_CachedColliders, siteTransform.rotation, m_PlayerTriggerMask);
                            bool isRedInside = false, isBlueInside = false;
                            for (var i = 0; i < playersInsideCount; i++)
                            {
                                Collider collider = m_CachedColliders[i];
                                if (collider.TryGetComponent(out PlayerTrigger playerTrigger))
                                {
                                    Container player = context.GetModifyingPlayer(playerTrigger.PlayerId);
                                    if (player.Health().IsInactiveOrDead) continue;

                                    byte team = player.Require<TeamProperty>();
                                    if (team == RedTeam) isRedInside = true;
                                    else if (team == BlueTeam) isBlueInside = true;
                                }
                            }
                            site.isRedInside.Value = isRedInside;
                            site.isBlueInside.Value = isBlueInside;

                            if (isRedInside && !isBlueInside)
                            {
                                // Red securing with no opposition
                                if (site.timeUs > durationUs) site.timeUs.Value -= durationUs;
                                else if (secureArea.roundTime >= m_Config.roundEndDurationUs)
                                {
                                    // Round ended, site was secured by red
                                    site.timeUs.Value = 0u;
                                    secureArea.roundTime.Value = m_Config.roundEndDurationUs;
                                    sessionContainer.Require<DualScoresArray>()[RedTeam].Value++;
                                    secureArea.lastWinningTeam.Value = RedTeam;
                                }
                                runTimer = redJustSecured = site.timeUs == 0u;
                            }
                            if (isRedInside && isBlueInside) runTimer = false; // Both in site
                        }
                    }
                }

                if (runTimer)
                {
                    if (secureArea.roundTime > durationUs)
                    {
                        if (!redJustSecured && !endedWithKills && secureArea.roundTime >= m_Config.roundEndDurationUs &&
                            secureArea.roundTime - durationUs < m_Config.roundEndDurationUs)
                        {
                            // Round just ended without contesting
                            sessionContainer.Require<DualScoresArray>()[BlueTeam].Value++;
                        }
                        secureArea.roundTime.Value -= durationUs;
                    }
                    else NextRound(context, secureArea);
                }
                else
                {
                    if (secureArea.roundTime > m_Config.roundEndDurationUs && secureArea.roundTime - m_Config.roundEndDurationUs > durationUs)
                        secureArea.roundTime.Value -= durationUs;
                    else secureArea.roundTime.Value = m_Config.roundEndDurationUs;
                }
            }
            else
            {
                bool start = activePlayerCount == m_Config.playerCount;
                if (start) FirstRound(context, secureArea);
            }
        }

        public override void ModifyPlayer(in SessionContext context)
        {
            var secureArea = context.sessionContainer.Require<SecureAreaComponent>();
            TimeUsProperty roundTime = secureArea.roundTime;
            bool isBuyTime = roundTime.WithValue && roundTime > m_Config.roundEndDurationUs + m_Config.roundDurationUs;
            Container player = context.player;
            if (isBuyTime) BuyingMode.HandleBuying(this, player, context.commands);
            var score = context.sessionContainer.Require<DualScoresArray>();
            player.Require<FrozenProperty>().Value = isBuyTime || AtPauseTime(secureArea, score);

            if (context.WithServerStringCommands(out IEnumerable<string[]> stringCommands))
            {
                foreach (string[] arguments in stringCommands)
                    switch (arguments[0])
                    {
                        case "give_money" when arguments.Length > 1 && DefaultConfig.Active.allowCheats && ushort.TryParse(arguments[1].Expand(), out ushort bonus):
                        {
                            player.Require<MoneyComponent>().count.IncrementCapped(bonus, MaxMoney);
                            break;
                        }
                        case "force_start":
                        {
                            FirstRound(context, secureArea);
                            break;
                        }
                    }
            }

            base.ModifyPlayer(context);
        }

        protected override void SpawnPlayer(in SessionContext context, bool begin = false)
        {
            var secureArea = context.sessionContainer.Require<SecureAreaComponent>();
            var scores = context.sessionContainer.Require<DualScoresArray>();
            Container player = context.player;
            var teamProperty = player.Require<TeamProperty>();
            if (begin)
            {
                teamProperty.Value = (byte)((context.playerId + 1) % 2);
                player.ZeroIfWith<StatsComponent>();
            }
            else if (AtRoundSum(secureArea, scores, m_Config.maxRounds) && teamProperty.TryWithValue(out byte team)) teamProperty.Value = (byte)((team + 1) % 2);
            if (secureArea.roundTime.WithValue)
            {
                HealthProperty health = player.Health();
                var money = player.Require<MoneyComponent>();
                var inventory = player.Require<InventoryComponent>();
                if (health.IsInactiveOrDead || money.count.WithoutValue)
                {
                    PlayerItemManagerModiferBehavior.ResetEquipStatus(inventory);
                    PlayerItemManagerModiferBehavior.SetAllItems(inventory, ItemId.Pickaxe, ItemId.Pistol);
                    if (money.count.WithoutValue) money.count.Value = 800;
                }
                if (health.IsActiveAndAlive) PlayerItemManagerModiferBehavior.RefillAllAmmo(inventory);

                var move = player.Require<MoveComponent>();
                move.Zero();
                move.position.Value = GetSpawnPosition(context);
                player.ZeroIfWith<CameraComponent>();
                health.Value = 100;
                player.ZeroIfWith<RespawnTimerProperty>();
                player.Require<ByteIdProperty>().Value = 1;
            }
            else base.SpawnPlayer(in context, begin);
        }

        private static List<int>[] _randomIndices;

        protected override Vector3 GetSpawnPosition(in SessionContext context)
        {
            try
            {
                Dictionary<Position3Int, Container> models = context.GetMapManager().Map.models.Map;
                KeyValuePair<Position3Int, Container>[][] spawns = models.Where(pair => pair.Value.With(out ModelIdProperty modelId)
                                                                                     && modelId == ModelsProperty.Spawn
                                                                                     && pair.Value.With<TeamProperty>())
                                                                         .GroupBy(spawnPair => spawnPair.Value.Require<TeamProperty>().Value)
                                                                         .OrderBy(spawnGroup => spawnGroup.Key)
                                                                         .Select(spawnGroup => spawnGroup.ToArray())
                                                                         .ToArray();
                byte team = context.player.Require<TeamProperty>();
                KeyValuePair<Position3Int, Container>[] teamSpawns = spawns[team];

                List<int> indices = _randomIndices[team];
                if (indices.Count == 0)
                {
                    indices.Capacity = teamSpawns.Length;
                    for (var i = 0; i < teamSpawns.Length; i++)
                        indices.Add(i);
                }
                int index = Random.Range(0, indices.Count);
                int spawnIndex = indices[index];
                indices.RemoveAt(index);

                return AdjustSpawn(context, teamSpawns[spawnIndex].Key);
            }
            catch (Exception)
            {
                return GetRandomPosition(context);
            }
        }

        private void FirstRound(in SessionContext context, SecureAreaComponent secureArea) => NextRound(context, secureArea, true);

        private void NextRound(in SessionContext context, SecureAreaComponent secureArea, bool isFirstRound = false)
        {
            var isFirstRoundOfSecondHalf = false;
            var scores = context.sessionContainer.Require<DualScoresArray>();
            if (isFirstRound) scores.Zero();
            else if (AtRoundSum(secureArea, scores, m_Config.maxRounds)) isFirstRoundOfSecondHalf = true;
            else if (AtRoundCount(secureArea, scores, m_Config.maxRounds)) return;

            secureArea.roundTime.Value = m_Config.roundEndDurationUs + m_Config.roundDurationUs + m_Config.buyDurationUs;
            foreach (SiteComponent site in secureArea.sites)
            {
                site.Zero();
                site.timeUs.Value = m_Config.secureDurationUs;
            }
            context.ForEachActivePlayer((in SessionContext playerModifyContext) =>
            {
                if (isFirstRoundOfSecondHalf)
                {
                    playerModifyContext.player.ClearIfWith<MoneyComponent>();
                    secureArea.lastWinningTeam.Clear();
                }
                SpawnPlayer(playerModifyContext);
                if (isFirstRound) playerModifyContext.player.ZeroIfWith<StatsComponent>();
                if (secureArea.lastWinningTeam.WithValue)
                {
                    Container player = playerModifyContext.player;
                    UShortProperty money = player.Require<MoneyComponent>().count;
                    money.Value += player.Require<TeamProperty>() == secureArea.lastWinningTeam ? m_Config.roundWinMoney : m_Config.roundLoseMoney;
                    if (money.Value > MaxMoney) money.Value = MaxMoney;
                }
            });
            context.sessionContainer.Require<EntityArray>().Clear();

            if (isFirstRoundOfSecondHalf)
            {
                scores.Swap();
                context.sessionContainer.Require<MapGenerationProperty>().Reload();
            }
        }

        public override bool ShowScoreboard(in SessionContext context)
            => AtPauseTime(context.sessionContainer.Require<SecureAreaComponent>(), context.sessionContainer.Require<DualScoresArray>());

        private static int _int;

        private bool AtPauseTime(SecureAreaComponent secureArea, DualScoresArray scores)
            => AtRoundCount(secureArea, scores, m_Config.maxRounds)
            || AtRoundSum(secureArea, scores, m_Config.maxRounds) && secureArea.roundTime < m_Config.roundEndDurationUs;

        private static bool AtRoundCount(SecureAreaComponent secureArea, DualScoresArray scores, int count)
        {
            _int = count;
            return secureArea.roundTime.WithValue && scores.Any(score => score == _int);
        }

        private static bool AtRoundSum(SecureAreaComponent secureArea, DualScoresArray scores, int sum)
            => secureArea.roundTime.WithValue && scores.Sum(score => score.Value) == sum;

        public override void Render(in SessionContext context)
        {
            if (context.session.IsLoading) return;

            base.Render(context);

            SiteBehavior[] siteBehaviors = GetSiteBehaviors(context);
            var secureArea = context.sessionContainer.Require<SecureAreaComponent>();
            for (var siteIndex = 0; siteIndex < siteBehaviors.Length; siteIndex++)
                siteBehaviors[siteIndex].Render(secureArea.sites[siteIndex]);
        }

        public override bool IsSpectating(Container session, Container actualLocalPlayer)
        {
            if (session.Require<SecureAreaComponent>().roundTime.WithoutValue) return base.IsSpectating(session, actualLocalPlayer);
            return actualLocalPlayer.Health().IsDead && actualLocalPlayer.Require<RespawnTimerProperty>().Value < DefaultConfig.Active.respawnDuration / 2;
        }

        protected override float CalculateWeaponDamage(in PlayerHitContext context)
            => context.sessionContext.sessionContainer.Require<SecureAreaComponent>().roundTime.WithValue
            && context.hitPlayer.Require<TeamProperty>() == context.sessionContext.player.Require<TeamProperty>()
                ? 0.0f
                : base.CalculateWeaponDamage(context);

        public bool CanBuy(in SessionContext context, Container sessionLocalPlayer)
        {
            if (sessionLocalPlayer.Health().IsInactiveOrDead) return false;
            var secureArea = context.sessionContainer.Require<SecureAreaComponent>();
            return secureArea.roundTime.WithValue && secureArea.roundTime > m_Config.roundEndDurationUs + m_Config.roundDurationUs;
        }

        public ushort GetCost(int itemId) => m_ItemPrices[itemId - 1];

        public override Color GetTeamColor(byte? teamId) => teamId is { } team
            ? team == BlueTeam ? m_BlueColor : m_RedColor
            : Color.white;
    }
}