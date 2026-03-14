using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using UnityEngine;
using Random = UnityEngine.Random;
using Physics = UnityEngine.Physics;

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;

using ConVar;

using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "3.4.3")]
    [Description("Spawns and kills zombies at set times")]
    class NightZombies : RustPlugin
    {
        //private const string DeathSound = "assets/prefabs/npc/murderer/sound/death.prefab";
        private const string AdminPermission = "nightzombies.admin";
        private const string RemoveMeMethodName = nameof(DroppedItemContainer.RemoveMe);
        private const int GrenadeItemId = 1840822026;
        
        private static NightZombies _instance;
        private static Configuration _config;
        private DynamicConfigFile _dataFile;

        [PluginReference("Kits")]
        private Plugin _kits;

        [PluginReference("Vanish")]
        private Plugin _vanish;

        private SpawnController _spawnController;
        
        #region -Init-
        
        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(AdminPermission, this);
            RegisterCommands();

            _spawnController = new SpawnController();
            
            //Read saved number of days since last spawn
            _dataFile = Interface.Oxide.DataFileSystem.GetFile("NightZombies-daysSinceSpawn");

            try
            {
                _spawnController.DaysSinceLastSpawn = _dataFile.ReadObject<int>();
            }
            catch //Default to 0 if error reading or data broken
            {
                PrintWarning("Failed to load saved days since last spawn, defaulting to 0");
                _spawnController.DaysSinceLastSpawn = 0;
            }

            if (_config.Behaviour.SentriesAttackZombies)
            {
                Unsubscribe(nameof(OnTurretTarget));
            }

            if (_config.Destroy.SpawnLoot)
            {
                Unsubscribe(nameof(OnCorpsePopulate));
            }

            if (_config.Behaviour.Ignored.Count == 0 && !_config.Behaviour.IgnoreHumanNpc && _config.Behaviour.AttackSleepers)
            {
                Unsubscribe(nameof(OnNpcTarget));
            }
        }
        
        private void OnServerInitialized()
        {
            //Warn if kits is not loaded
            if (!_kits?.IsLoaded ?? false)
            {
                PrintWarning("Kits is not loaded, custom kits will not work");
            }
            
            // Start time check even when always spawned so the initial spawn cycle still runs.
            if (_config.Spawn.SpawnTime >= 0 && _config.Spawn.DestroyTime >= 0)
            {
                TOD_Sky.Instance.Components.Time.OnMinute += _spawnController.TimeTick;
                TOD_Sky.Instance.Components.Time.OnDay += OnDay;

                _spawnController.StartWaveTimer();

                NextTick(_spawnController.TimeTick);
            }
        }
        
        private void Unload()
        {
            TOD_Sky.Instance.Components.Time.OnMinute -= _spawnController.TimeTick;
            TOD_Sky.Instance.Components.Time.OnDay -= OnDay;

            _dataFile.WriteObject(_spawnController.DaysSinceLastSpawn);

            _spawnController?.Shutdown();

            _config = null;
            _instance = null;
        }

        private void OnDay() => _spawnController.DaysSinceLastSpawn++;

        #endregion

        #region -Oxide Hooks-

        private void NightZombiesChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player))
            {
                SendReply(player, "You do not have permission to use this command.");
                return;
            }

            SendReply(player, HandleAdminCommand(command));
        }

        private void NightZombiesConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && !HasAdminAccess(player))
            {
                SendReply(player, "You do not have permission to use this command.");
                return;
            }

            string response = HandleAdminCommand(arg.cmd?.Name ?? string.Empty);
            if (player != null)
            {
                SendReply(player, response);
                return;
            }

            Puts(response);
        }

        private object OnNpcTarget(ScarecrowNPC npc, BaseEntity target)
        {
            return CanAttack(target);
        }

        private object OnNpcTarget(ScientistNPC npc, BaseEntity target)
        {
            return CanAttack(target);
        }

        private object OnTurretTarget(NPCAutoTurret turret, ScarecrowNPC entity)
        {
            if (entity == null)
            {
                return null;
            }
            
            return true;
        }

        private object OnTurretTarget(NPCAutoTurret turret, ScientistNPC entity)
        {
            if (entity == null)
            {
                return null;
            }

            return true;
        }

        private object OnPlayerDeath(ScarecrowNPC scarecrow, HitInfo info)
        {
            return HandleNpcDeath(scarecrow);
        }

        private object OnPlayerDeath(ScientistNPC scientist, HitInfo info)
        {
            return HandleNpcDeath(scientist);
        }

        private object HandleNpcDeath(BasePlayer npc)
        {
            //Effect.server.Run(DeathSound, npc.transform.position);
            _spawnController.NpcDied(npc);

            if (_config.Destroy.LeaveCorpseKilled)
            {
                return null;
            }
            
            NextTick(() =>
            {
                if (npc == null || npc.IsDestroyed)
                {
                    return;
                }

                npc.AdminKill();
            });
                
            return true;
        }

        private BaseCorpse OnCorpsePopulate(ScarecrowNPC npcPlayer, NPCPlayerCorpse corpse)
        {
            return corpse;
        }

        private BaseCorpse OnCorpsePopulate(ScientistNPC npcPlayer, NPCPlayerCorpse corpse)
        {
            return corpse;
        }

        private void OnEntitySpawned(NPCPlayerCorpse corpse)
        {
            if (string.Equals(corpse.playerName, "Scarecrow", StringComparison.OrdinalIgnoreCase))
            {
                corpse.playerName = _config.Spawn.Zombies.DisplayName;
            }
            else if (string.Equals(corpse.playerName, "Scientist", StringComparison.OrdinalIgnoreCase))
            {
                corpse.playerName = _config.Spawn.Scientists.DisplayName;
            }
        }
        
        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (!_config.Destroy.HalfBodybagDespawn)
            {
                return;
            }
            
            NextTick(() =>
            {
                if (container == null)
                {
                    return;
                }

                if (container.playerName == _config.Spawn.Zombies.DisplayName || container.playerName == _config.Spawn.Scientists.DisplayName)
                {
                    container.CancelInvoke(RemoveMeMethodName);
                    container.Invoke(RemoveMeMethodName, container.CalculateRemovalTime() / 2);
                }
            });
        }

        #endregion

        #region -Helpers-

        private void RegisterCommands()
        {
            cmd.AddChatCommand(_config.Commands.ChatSpawnCommand, this, nameof(NightZombiesChatCommand));
            cmd.AddChatCommand(_config.Commands.ChatRespawnCommand, this, nameof(NightZombiesChatCommand));
            cmd.AddChatCommand(_config.Commands.ChatClearCommand, this, nameof(NightZombiesChatCommand));
            cmd.AddChatCommand(_config.Commands.ChatStatusCommand, this, nameof(NightZombiesChatCommand));

            cmd.AddConsoleCommand(_config.Commands.ConsoleSpawnCommand, this, nameof(NightZombiesConsoleCommand));
            cmd.AddConsoleCommand(_config.Commands.ConsoleRespawnCommand, this, nameof(NightZombiesConsoleCommand));
            cmd.AddConsoleCommand(_config.Commands.ConsoleClearCommand, this, nameof(NightZombiesConsoleCommand));
            cmd.AddConsoleCommand(_config.Commands.ConsoleStatusCommand, this, nameof(NightZombiesConsoleCommand));
        }

        private bool HasAdminAccess(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPermission));
        }

        private string HandleAdminCommand(string command)
        {
            string normalized = (command ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
            {
                return GetAdminUsage();
            }

            if (normalized == _config.Commands.ChatSpawnCommand || normalized == _config.Commands.ConsoleSpawnCommand)
            {
                return _spawnController.ForceSpawnWave();
            }

            if (normalized == _config.Commands.ChatRespawnCommand || normalized == _config.Commands.ConsoleRespawnCommand)
            {
                return _spawnController.ForceRespawnWave();
            }

            if (normalized == _config.Commands.ChatClearCommand || normalized == _config.Commands.ConsoleClearCommand)
            {
                return _spawnController.ForceClearWave();
            }

            if (normalized == _config.Commands.ChatStatusCommand || normalized == _config.Commands.ConsoleStatusCommand)
            {
                return _spawnController.GetStatus();
            }

            return GetAdminUsage();
        }

        private string GetAdminUsage()
        {
            return $"Chat: /{_config.Commands.ChatSpawnCommand}, /{_config.Commands.ChatRespawnCommand}, /{_config.Commands.ChatClearCommand}, /{_config.Commands.ChatStatusCommand} | Console: {_config.Commands.ConsoleSpawnCommand}, {_config.Commands.ConsoleRespawnCommand}, {_config.Commands.ConsoleClearCommand}, {_config.Commands.ConsoleStatusCommand}";
        }

        private object CanAttack(BaseEntity target)
        {
            if (target is ScientistNPC || target is ScarecrowNPC)
            {
                return true;
            }

            if (_config.Behaviour.Ignored.Contains(target.ShortPrefabName) || 
                (_config.Behaviour.IgnoreHumanNpc && HumanNPCCheck(target)) || 
                (!_config.Behaviour.AttackSleepers && target is BasePlayer player && player.IsSleeping()))
            {
                return true;
            }
            
            return null;
        }

        private bool HumanNPCCheck(BaseEntity target)
        {
            return target is BasePlayer player && !player.userID.IsSteamId() && target is not ScientistNPC &&
                   target is not ScarecrowNPC;
        }

        #endregion

        #region -Classes-

        private class SpawnController
        {
            private const string ScarecrowPrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";
            private const string ScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
            private const BindingFlags ReflectionFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            private readonly Configuration.SpawnSettings _spawnConfig;
            private readonly List<SpawnProfile> _profiles = new();

            private readonly int _spawnLayerMask = LayerMask.GetMask("Default", "Tree", "Construction", "World", "Vehicle_Detailed", "Deployed");
            private readonly WaitForSeconds _waitTenthSecond = new(0.1f);

            private bool IsSpawnTime => _spawnConfig.AlwaysSpawned || (_spawnTime > _destroyTime
                                            ? Env.time >= _spawnTime || Env.time < _destroyTime
                                            : Env.time <= _spawnTime || Env.time > _destroyTime);

            private bool IsDestroyTime => !_spawnConfig.AlwaysSpawned && (_spawnTime > _destroyTime
                                              ? Env.time >= _destroyTime && Env.time < _spawnTime
                                              : Env.time <= _destroyTime && Env.time > _spawnTime);

            public int DaysSinceLastSpawn;

            private readonly float _spawnTime;
            private readonly float _destroyTime;
            private readonly int _waveRespawnMinutes;
            private readonly bool _leaveCorpse;

            private bool _spawned;
            private bool _roadPointsInitialized;
            private bool _roadWarningShown;
            private DateTime _nextWaveActionTimeUtc = DateTime.MinValue;

            private Coroutine _currentCoroutine;

            private readonly Dictionary<BasePlayer, SpawnProfile> _npcs = new();
            private readonly List<Vector3> _roadPoints = new();

            private class SpawnProfile
            {
                public readonly string Prefab;
                public readonly string DefaultName;
                public readonly string DisplayName;
                public readonly int Population;
                public readonly float Health;
                public readonly List<string> Kits;

                public SpawnProfile(string prefab, string defaultName, string displayName, int population, float health, List<string> kits)
                {
                    Prefab = prefab;
                    DefaultName = defaultName;
                    DisplayName = displayName;
                    Population = population;
                    Health = health;
                    Kits = kits ?? new List<string>();
                }
            }

            public SpawnController()
            {
                _spawnConfig = _config.Spawn;

                _spawnTime = _spawnConfig.SpawnTime;
                _destroyTime = _spawnConfig.DestroyTime;
                _waveRespawnMinutes = _spawnConfig.WaveRespawnMinutes;

                // These might not be available after the plugin is unloaded, will cause NRE if trying to access in RemoveNpcs
                _leaveCorpse = _config.Destroy.LeaveCorpse;

                AddProfile(ScarecrowPrefab, "Scarecrow", _spawnConfig.Zombies.DisplayName, _spawnConfig.Zombies.Population, _spawnConfig.Zombies.Health, _spawnConfig.Zombies.Kits);
                AddProfile(ScientistPrefab, "Scientist", _spawnConfig.Scientists.DisplayName, _spawnConfig.Scientists.Population, _spawnConfig.Scientists.Health, _spawnConfig.Scientists.Kits);
            }

            private void AddProfile(string prefab, string defaultName, string displayName, int population, float health, List<string> kits)
            {
                if (population <= 0)
                {
                    return;
                }

                _profiles.Add(new SpawnProfile(prefab, defaultName, displayName, population, health, kits));
            }

            private IEnumerator SpawnNpcs(bool stopCurrent = true)
            {
                if (_profiles.Count == 0)
                {
                    yield break;
                }

                if (stopCurrent && _currentCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_currentCoroutine);
                }

                if (_waveRespawnMinutes > 0)
                {
                    PushNextWaveActionWindow();
                }

                _spawned = false;

                int scientistCount = 0;
                int scarecrowCount = 0;

                foreach (SpawnProfile profile in _profiles)
                {
                    int amountToSpawn = profile.Population - GetPopulation(profile);

                    for (int i = 0; i < amountToSpawn; i++)
                    {
                        if (SpawnNpc(profile))
                        {
                            _spawned = true;

                            if (profile.DefaultName == "Scientist")
                            {
                                scientistCount++;
                            }
                            else
                            {
                                scarecrowCount++;
                            }
                        }

                        yield return _waitTenthSecond;
                    }
                }

                if (_config.Broadcast.DoBroadcast && _spawned)
                {
                    if (scientistCount > 0 && scarecrowCount > 0)
                    {
                        Broadcast("ChatBroadcastSeparate", scientistCount, scarecrowCount);
                    }
                    else
                    {
                        Broadcast("ChatBroadcast", scientistCount + scarecrowCount);
                    }
                }

                if (_spawned)
                {
                    DaysSinceLastSpawn = 0;

                    if (_waveRespawnMinutes > 0)
                    {
                        PushNextWaveActionWindow();
                    }
                }

                _currentCoroutine = null;
            }

            private IEnumerator RemoveNpcs(bool shuttingDown = false, bool stopCurrent = true)
            {
                CleanupNpcs();
                if (_npcs.Count == 0)
                {
                    yield break;
                }

                if (stopCurrent && _currentCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_currentCoroutine);
                }

                List<BasePlayer> npcs = new List<BasePlayer>(_npcs.Keys);
                foreach (BasePlayer npc in npcs)
                {
                    if (npc == null || npc.IsDestroyed)
                    {
                        continue;
                    }

                    if (_leaveCorpse && !shuttingDown)
                    {
                        npc.Die();
                    }
                    else
                    {
                        npc.AdminKill();
                    }

                    yield return !shuttingDown ? _waitTenthSecond : null;
                }

                _npcs.Clear();
                _spawned = false;
                _currentCoroutine = null;
            }

            private IEnumerator RespawnWave()
            {
                yield return RemoveNpcs(false, false);
                yield return _waitTenthSecond;
                yield return SpawnNpcs(false);

                _currentCoroutine = null;
            }

            public void TimeTick()
            {
                CleanupNpcs();

                if (_currentCoroutine != null)
                {
                    return;
                }

                if (_waveRespawnMinutes > 0)
                {
                    if (_npcs.Count == 0 && !_spawned)
                    {
                        if (CanSpawn() && CanRunWaveAction())
                        {
                            _currentCoroutine = ServerMgr.Instance.StartCoroutine(SpawnNpcs());
                        }

                        return;
                    }

                    if (IsDestroyTime && _spawned)
                    {
                        _currentCoroutine = ServerMgr.Instance.StartCoroutine(RemoveNpcs());
                        return;
                    }

                    return;
                }

                if (_npcs.Count == 0)
                {
                    _spawned = false;
                }

                if (CanSpawn())
                {
                    _currentCoroutine = ServerMgr.Instance.StartCoroutine(SpawnNpcs());
                }
                else if (_npcs.Count > 0 && IsDestroyTime && _spawned)
                {
                    _currentCoroutine = ServerMgr.Instance.StartCoroutine(RemoveNpcs());
                }
            }

            public void NpcDied(BasePlayer npc)
            {
                if (npc == null)
                {
                    return;
                }

                SpawnProfile profile;
                if (!_npcs.TryGetValue(npc, out profile))
                {
                    return;
                }

                _npcs.Remove(npc);

                if (!IsSpawnTime)
                {
                    return;
                }

                if (_waveRespawnMinutes > 0)
                {
                    return;
                }

                SpawnNpc(profile);
            }

            public string ForceSpawnWave()
            {
                CleanupNpcs();

                if (_currentCoroutine != null)
                {
                    return "Night Zombies is busy processing another spawn action.";
                }

                if (_waveRespawnMinutes > 0)
                {
                    PushNextWaveActionWindow();
                }

                _currentCoroutine = ServerMgr.Instance.StartCoroutine(SpawnNpcs());
                return "Night Zombies spawn wave started.";
            }

            public string ForceRespawnWave()
            {
                CleanupNpcs();

                if (_currentCoroutine != null)
                {
                    return "Night Zombies is busy processing another spawn action.";
                }

                if (_waveRespawnMinutes > 0)
                {
                    PushNextWaveActionWindow();
                }

                _currentCoroutine = ServerMgr.Instance.StartCoroutine(RespawnWave());
                return "Night Zombies respawn wave started.";
            }

            public string ForceClearWave()
            {
                CleanupNpcs();

                if (_npcs.Count == 0)
                {
                    _spawned = false;
                    return "Night Zombies has no active NPCs to clear.";
                }

                if (_currentCoroutine != null)
                {
                    return "Night Zombies is busy processing another spawn action.";
                }

                if (_waveRespawnMinutes > 0)
                {
                    PushNextWaveActionWindow();
                }

                _currentCoroutine = ServerMgr.Instance.StartCoroutine(RemoveNpcs());
                return "Night Zombies clear wave started.";
            }

            public string GetStatus()
            {
                CleanupNpcs();

                int scientistCount = 0;
                int scarecrowCount = 0;

                foreach (SpawnProfile profile in _npcs.Values)
                {
                    if (profile.DefaultName == "Scientist")
                    {
                        scientistCount++;
                    }
                    else if (profile.DefaultName == "Scarecrow")
                    {
                        scarecrowCount++;
                    }
                }

                string nextWaveStatus = "off";
                if (_waveRespawnMinutes > 0)
                {
                    TimeSpan remaining = _nextWaveActionTimeUtc - DateTime.UtcNow;
                    if (remaining < TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }

                    nextWaveStatus = $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    if (remaining.TotalHours >= 1)
                    {
                        nextWaveStatus = $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    }
                }

                return $"Night Zombies status: active={_npcs.Count}, scarecrows={scarecrowCount}, scientists={scientistCount}, busy={(_currentCoroutine != null ? "yes" : "no")}, nextWave={nextWaveStatus}.";
            }

            public void StartWaveTimer()
            {
                if (_waveRespawnMinutes <= 0)
                {
                    return;
                }

                _instance.timer.Every(_waveRespawnMinutes * 60f, RealTimeWaveTick);
            }

            private void RealTimeWaveTick()
            {
                CleanupNpcs();

                if (_currentCoroutine != null || !IsSpawnTime)
                {
                    return;
                }

                if (!CanRunWaveAction())
                {
                    return;
                }

                if (_npcs.Count == 0)
                {
                    _spawned = false;

                    if (CanSpawn())
                    {
                        _currentCoroutine = ServerMgr.Instance.StartCoroutine(SpawnNpcs());
                    }

                    return;
                }

                if (_spawned)
                {
                    _currentCoroutine = ServerMgr.Instance.StartCoroutine(RespawnWave());
                }
            }

            private bool CanRunWaveAction()
            {
                return DateTime.UtcNow >= _nextWaveActionTimeUtc;
            }

            private void PushNextWaveActionWindow()
            {
                _nextWaveActionTimeUtc = DateTime.UtcNow.AddMinutes(_waveRespawnMinutes);
            }

            #region -Util-

            private bool SpawnNpc(SpawnProfile profile)
            {
                if (GetPopulation(profile) >= profile.Population)
                {
                    return false;
                }

                Vector3 position;
                if (!TryGetSpawnPosition(out position))
                {
                    return false;
                }

                BasePlayer npc = GameManager.server.CreateEntity(profile.Prefab, position) as BasePlayer;
                if (!npc)
                {
                    return false;
                }

                npc.Spawn();
                npc.displayName = profile.DisplayName;

                if (npc.TryGetComponent(out BaseNavigator navigator))
                {
                    navigator.ForceToGround();
                    navigator.PlaceOnNavMesh(0);
                }

                npc.SetMaxHealth(profile.Health);
                npc.SetHealth(profile.Health);

                if (_instance._kits != null && profile.Kits.Count > 0)
                {
                    npc.inventory.containerWear.Clear();
                    ItemManager.DoRemoves();

                    _instance._kits.Call("GiveKit", npc, profile.Kits.GetRandom());
                }

                if (!_config.Behaviour.ThrowGrenades)
                {
					List<Item> items = new List<Item>();
					npc.inventory.FindItemsByItemID(items, GrenadeItemId);
                    foreach (Item item in items)
                    {
                        item.Remove();
                    }

                    ItemManager.DoRemoves();
                }

                _npcs[npc] = profile;
                _instance.Puts($"Spawned {profile.DefaultName} '{profile.DisplayName}' at {position} ({GetPopulation(profile)}/{profile.Population})");
                return true;
            }

            private bool TryGetSpawnPosition(out Vector3 position)
            {
                if (_spawnConfig.SpawnNearPlayers && BasePlayer.activePlayerList.Count >= _spawnConfig.MinNearPlayers)
                {
                    if (TryGetPositionNearAnyPlayer(out position))
                    {
                        return true;
                    }

                    return false;
                }

                Vector3 roadPosition;
                if (TryGetRoadPosition(null, out roadPosition))
                {
                    position = roadPosition;
                    return true;
                }

                return TryGetRandomPosition(out position);
            }

            private bool TryGetPositionNearAnyPlayer(out Vector3 position)
            {
                position = Vector3.zero;

                for (int i = 0; i < 12; i++)
                {
                    BasePlayer player;
                    if (!GetRandomPlayer(out player))
                    {
                        return false;
                    }

                    Vector3 nearRoadPosition;
                    if (TryGetRoadPosition(player, out nearRoadPosition))
                    {
                        position = nearRoadPosition;
                        return true;
                    }

                    if (TryGetRandomPositionAroundPlayer(player, out position))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool GetRandomPlayer(out BasePlayer player)
            {
                List<BasePlayer> players = new List<BasePlayer>();

                foreach (BasePlayer bplayer in BasePlayer.activePlayerList)
                {
                    if (bplayer.IsFlying || _instance._vanish?.Call<bool>("IsInvisible", bplayer) == true)
                    {
                        continue;
                    }

                    players.Add(bplayer);
                }

                if (players.Count == 0)
                {
                    player = null;
                    return false;
                }

                player = players.GetRandom();
                return player != null;
            }

            private bool TryGetRandomPosition(out Vector3 position)
            {
                position = Vector3.zero;

                for (int i = 0; i < 24; i++)
                {
                    float x = Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2),
                          z = Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2),
                          y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));

                    Vector3 candidate = new Vector3(x, y + 0.5f, z);
                    if (TryGetNavigablePosition(candidate, out candidate) && IsValidPosition(candidate))
                    {
                        position = candidate;
                        return true;
                    }
                }

                return false;
            }

            private bool TryGetRandomPositionAroundPlayer(BasePlayer player, out Vector3 position)
            {
                Vector3 playerPos = player.transform.position;
                position = Vector3.zero;
                float maxDist = _spawnConfig.MaxDistance;

                for (int i = 0; i < 24; i++)
                {
                    Vector3 candidate = new Vector3(Random.Range(playerPos.x - maxDist, playerPos.x + maxDist), 0, Random.Range(playerPos.z - maxDist, playerPos.z + maxDist));
                    candidate.y = TerrainMeta.HeightMap.GetHeight(candidate) + 0.5f;

                    if (TryGetNavigablePosition(candidate, out candidate) && IsValidPosition(candidate) && Vector3.Distance(playerPos, candidate) > _spawnConfig.MinDistance)
                    {
                        position = candidate;
                        return true;
                    }
                }

                return false;
            }

            private bool TryGetRoadPosition(BasePlayer player, out Vector3 position)
            {
                position = Vector3.zero;

                if (!_spawnConfig.Roads.PreferRoads || Random.Range(0f, 100f) > _spawnConfig.Roads.RoadChance)
                {
                    return false;
                }

                CacheRoadPoints();
                if (_roadPoints.Count == 0)
                {
                    return false;
                }

                for (int i = 0; i < 24; i++)
                {
                    Vector3 roadPoint = _roadPoints[Random.Range(0, _roadPoints.Count)];
                    if (TryGetValidRoadOffsetPosition(roadPoint, player, out position))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool TryGetValidRoadOffsetPosition(Vector3 roadPoint, BasePlayer player, out Vector3 position)
            {
                position = Vector3.zero;
                float maxRoadOffset = _spawnConfig.Roads.MaxDistanceFromRoad;

                for (int i = 0; i < 8; i++)
                {
                    Vector2 offset = Random.insideUnitCircle * maxRoadOffset;
                    Vector3 candidate = new Vector3(roadPoint.x + offset.x, 0f, roadPoint.z + offset.y);
                    candidate.y = TerrainMeta.HeightMap.GetHeight(candidate) + 0.5f;

                    if (!TryGetNavigablePosition(candidate, out candidate) || !IsValidPosition(candidate))
                    {
                        continue;
                    }

                    if (player != null)
                    {
                        float distance = Vector3.Distance(player.transform.position, candidate);
                        if (distance < _spawnConfig.MinDistance || distance > _spawnConfig.MaxDistance)
                        {
                            continue;
                        }
                    }

                    position = candidate;
                    return true;
                }

                return false;
            }

            private bool TryGetNavigablePosition(Vector3 sourcePosition, out Vector3 position)
            {
                UnityEngine.AI.NavMeshHit navMeshHit;
                if (UnityEngine.AI.NavMesh.SamplePosition(sourcePosition, out navMeshHit, 12f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    position = navMeshHit.position;
                    position.y += 0.1f;
                    return true;
                }

                if (UnityEngine.AI.NavMesh.SamplePosition(sourcePosition, out navMeshHit, 30f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    position = navMeshHit.position;
                    position.y += 0.1f;
                    return true;
                }

                position = Vector3.zero;
                return false;
            }

            private void CacheRoadPoints()
            {
                if (_roadPointsInitialized)
                {
                    return;
                }

                _roadPointsInitialized = true;

                try
                {
                    object terrainPath = GetMemberValue(typeof(TerrainMeta), "Path");
                    AddRoadPoints(GetMemberValue(terrainPath, "Roads") ?? GetMemberValue(terrainPath, "roads"));

                    if (_roadPoints.Count == 0)
                    {
                        AddRoadPoints(terrainPath);
                    }
                }
                catch (Exception ex)
                {
                    if (!_roadWarningShown)
                    {
                        _roadWarningShown = true;
                        _instance.PrintWarning($"Failed to resolve road points, falling back to generic spawn positions: {ex.Message}");
                    }
                }

                if (_roadPoints.Count == 0 && !_roadWarningShown)
                {
                    _roadWarningShown = true;
                    _instance.PrintWarning("Could not resolve road points in TerrainMeta.Path, falling back to generic spawn positions");
                }
            }

            private void AddRoadPoints(object source)
            {
                if (source == null)
                {
                    return;
                }

                if (TryAddVectorPoints(source))
                {
                    return;
                }

                if (source is not IEnumerable enumerable)
                {
                    return;
                }

                foreach (object entry in enumerable)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    if (entry is Vector3 point)
                    {
                        _roadPoints.Add(point);
                        continue;
                    }

                    object nestedPoints = GetMemberValue(entry, "Points") ?? GetMemberValue(entry, "points") ?? GetMemberValue(entry, "Path") ?? GetMemberValue(entry, "path");
                    TryAddVectorPoints(nestedPoints);
                }
            }

            private bool TryAddVectorPoints(object source)
            {
                if (source is not IEnumerable enumerable)
                {
                    return false;
                }

                bool addedAny = false;
                foreach (object entry in enumerable)
                {
                    if (entry is Vector3 point)
                    {
                        _roadPoints.Add(point);
                        addedAny = true;
                        continue;
                    }

                    object nestedPosition = GetMemberValue(entry, "Position") ?? GetMemberValue(entry, "position");
                    if (nestedPosition is Vector3 vector)
                    {
                        _roadPoints.Add(vector);
                        addedAny = true;
                    }
                }

                return addedAny;
            }

            private static object GetMemberValue(object source, string name)
            {
                if (source == null)
                {
                    return null;
                }

                Type type = source as Type ?? source.GetType();
                object instance = source is Type ? null : source;

                PropertyInfo property = type.GetProperty(name, ReflectionFlags);
                if (property != null)
                {
                    return property.GetValue(instance, null);
                }

                FieldInfo field = type.GetField(name, ReflectionFlags);
                if (field != null)
                {
                    return field.GetValue(instance);
                }

                return null;
            }

            private int GetPopulation(SpawnProfile profile)
            {
                CleanupNpcs();

                int count = 0;
                foreach (SpawnProfile activeProfile in _npcs.Values)
                {
                    if (activeProfile == profile)
                    {
                        count++;
                    }
                }

                return count;
            }

            private void CleanupNpcs()
            {
                List<BasePlayer> invalidNpcs = null;

                foreach (KeyValuePair<BasePlayer, SpawnProfile> entry in _npcs)
                {
                    if (entry.Key != null && !entry.Key.IsDestroyed)
                    {
                        continue;
                    }

                    invalidNpcs ??= new List<BasePlayer>();
                    invalidNpcs.Add(entry.Key);
                }

                if (invalidNpcs == null)
                {
                    return;
                }

                foreach (BasePlayer invalidNpc in invalidNpcs)
                {
                    _npcs.Remove(invalidNpc);
                }
            }

            private bool CanSpawn()
            {
                return !_spawned && DaysSinceLastSpawn >= _spawnConfig.Chance.Days && Random.Range(0f, 100f) < _spawnConfig.Chance.Chance && IsSpawnTime;
            }

            private bool IsValidPosition(Vector3 position)
            {
                return !AntiHack.TestInsideTerrain(position) && !IsInObject(position) && !IsInOcean(position);
            }

            private bool IsInObject(Vector3 position)
            {
                return Physics.OverlapSphere(position, 0.5f, _spawnLayerMask).Length > 0;
            }

            private bool IsInOcean(Vector3 position)
            {
                return WaterLevel.GetWaterDepth(position, true, true) > 0.25f;
            }

            private void Broadcast(string key, params object[] values)
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    player.ChatMessage(string.Format(_instance.GetMessage(key, player.UserIDString), values));
                }
            }

            #endregion

            public void Shutdown()
            {
                ServerMgr.Instance.StartCoroutine(RemoveNpcs(true));
            }
        }

        #endregion
        
        #region -Configuration-

        private class Configuration
        {
            [JsonProperty("Spawn Settings")]
            public SpawnSettings Spawn = new SpawnSettings();

            [JsonProperty("Destroy Settings")]
            public DestroySettings Destroy = new DestroySettings();

            [JsonProperty("Behaviour Settings")]
            public BehaviourSettings Behaviour = new BehaviourSettings();

            [JsonProperty("Command Settings")]
            public CommandSettings Commands = new CommandSettings();

            [JsonProperty("Broadcast Settings")]
            public ChatSettings Broadcast = new ChatSettings();

            public class SpawnSettings
            {
                [JsonProperty("Always Spawned")]
                public bool AlwaysSpawned = false;

                [JsonProperty("Spawn Time")]
                public float SpawnTime = 19.8f;

                [JsonProperty("Destroy Time")]
                public float DestroyTime = 7.3f;
                
                [JsonProperty("Spawn near players")]
                public bool SpawnNearPlayers = false;

                [JsonProperty("Min pop for near player spawn")]
                public int MinNearPlayers = 10;

                [JsonProperty("Min distance from player")]
                public float MinDistance = 40;

                [JsonProperty("Max distance from player")]
                public float MaxDistance = 80f;

                [JsonProperty("Wave respawn interval (minutes, 0 = off)")]
                public int WaveRespawnMinutes = 0;

                [JsonProperty("Zombie Settings")]
                public ZombieSettings Zombies = new ZombieSettings();

                [JsonProperty("Scientist Settings")]
                public ScientistSettings Scientists = new ScientistSettings();

                [JsonProperty("Road Settings")]
                public RoadSettings Roads = new RoadSettings();

                public class ZombieSettings
                {
                    [JsonProperty("Display Name")] 
                    public string DisplayName = "Scarecrow";
                    
                    [JsonProperty("Scarecrow Population (total amount)")]
                    public int Population = 50;
                    
                    [JsonProperty("Scarecrow Health")]
                    public float Health = 200f;

                    [JsonProperty("Scarecrow Kits")]
                    public List<string> Kits = new List<string>();
                }

                public class ScientistSettings
                {
                    [JsonProperty("Display Name")]
                    public string DisplayName = "Scientist";

                    [JsonProperty("Scientist Population (total amount)")]
                    public int Population = 20;

                    [JsonProperty("Scientist Health")]
                    public float Health = 150f;

                    [JsonProperty("Scientist Kits")]
                    public List<string> Kits = new List<string>();
                }

                public class RoadSettings
                {
                    [JsonProperty("Prefer roads")]
                    public bool PreferRoads = true;

                    [JsonProperty("Road spawn chance")]
                    public float RoadChance = 80f;

                    [JsonProperty("Max distance from road")]
                    public float MaxDistanceFromRoad = 16f;
                }

                [JsonProperty("Chance Settings")]
                public ChanceSetings Chance = new ChanceSetings();

                public class ChanceSetings
                {
                    [JsonProperty("Chance per cycle")]
                    public float Chance = 100f;
                    
                    [JsonProperty("Days betewen spawn")]
                    public int Days = 0;
                }
            }

            public class DestroySettings
            {
                [JsonProperty("Leave Corpse, when destroyed")]
                public bool LeaveCorpse = false;
                
                [JsonProperty("Leave Corpse, when killed by player")]
                public bool LeaveCorpseKilled = true;

                [JsonProperty("Spawn Loot")]
                public bool SpawnLoot = true;

                [JsonProperty("Half bodybag despawn time")]
                public bool HalfBodybagDespawn = true;
            }

            public class BehaviourSettings
            {
                [JsonProperty("Attack sleeping players")]
                public bool AttackSleepers = false;

                [JsonProperty("Zombies attacked by outpost sentries")]
                public bool SentriesAttackZombies = true;
                
                [JsonProperty("Throw Grenades")]
                public bool ThrowGrenades = true;

                [JsonProperty("Ignore Human NPCs")]
                public bool IgnoreHumanNpc = true;

                [JsonProperty("Ignored entities (full entity shortname)")]
                public List<string> Ignored = new List<string>();
            }

            public class ChatSettings
            {
                [JsonProperty("Broadcast spawn amount")]
                public bool DoBroadcast = false;
            }

            public class CommandSettings
            {
                [JsonProperty("Chat spawn command")]
                public string ChatSpawnCommand = "nzspawn";

                [JsonProperty("Chat respawn command")]
                public string ChatRespawnCommand = "nzrespawn";

                [JsonProperty("Chat clear command")]
                public string ChatClearCommand = "nzclear";

                [JsonProperty("Chat status command")]
                public string ChatStatusCommand = "nzstatus";

                [JsonProperty("Console spawn command")]
                public string ConsoleSpawnCommand = "nightzombies.spawn";

                [JsonProperty("Console respawn command")]
                public string ConsoleRespawnCommand = "nightzombies.respawn";

                [JsonProperty("Console clear command")]
                public string ConsoleClearCommand = "nightzombies.clear";

                [JsonProperty("Console status command")]
                public string ConsoleStatusCommand = "nightzombies.status";
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                _config.Commands.ChatSpawnCommand = SanitizeChatCommand(_config.Commands.ChatSpawnCommand, "nzspawn");
                _config.Commands.ChatRespawnCommand = SanitizeChatCommand(_config.Commands.ChatRespawnCommand, "nzrespawn");
                _config.Commands.ChatClearCommand = SanitizeChatCommand(_config.Commands.ChatClearCommand, "nzclear");
                _config.Commands.ChatStatusCommand = SanitizeChatCommand(_config.Commands.ChatStatusCommand, "nzstatus");

                _config.Commands.ConsoleSpawnCommand = SanitizeConsoleCommand(_config.Commands.ConsoleSpawnCommand, "nightzombies.spawn");
                _config.Commands.ConsoleRespawnCommand = SanitizeConsoleCommand(_config.Commands.ConsoleRespawnCommand, "nightzombies.respawn");
                _config.Commands.ConsoleClearCommand = SanitizeConsoleCommand(_config.Commands.ConsoleClearCommand, "nightzombies.clear");
                _config.Commands.ConsoleStatusCommand = SanitizeConsoleCommand(_config.Commands.ConsoleStatusCommand, "nightzombies.status");

                if (_config.Spawn.SpawnTime >= 0 && _config.Spawn.DestroyTime >= 0)
                {
                    if (_config.Spawn.SpawnTime > 24 || _config.Spawn.SpawnTime < 0)
                    {
                        PrintWarning("Invalid spawn time (must be in 24 hour time)");
                        _config.Spawn.SpawnTime = 19.5f;
                    }
                    if (_config.Spawn.DestroyTime > 24 || _config.Spawn.DestroyTime < 0)
                    {
                        PrintWarning("Invalid destroy time (must be in 24 hour time)");
                        _config.Spawn.DestroyTime = 7f;
                    }
                }

                _config.Spawn.Roads.RoadChance = Mathf.Clamp(_config.Spawn.Roads.RoadChance, 0f, 100f);
                if (_config.Spawn.WaveRespawnMinutes < 0)
                {
                    PrintWarning("Invalid wave respawn interval, defaulting to 0");
                    _config.Spawn.WaveRespawnMinutes = 0;
                }

                if (_config.Spawn.Roads.MaxDistanceFromRoad < 0)
                {
                    PrintWarning("Invalid max road distance, defaulting to 16");
                    _config.Spawn.Roads.MaxDistanceFromRoad = 16f;
                }

                SaveConfig();
            }
            catch
            {
                PrintError("Failed to load _config, using default values");
                LoadDefaultConfig();
            }
        }

        private string SanitizeChatCommand(string command, string fallback)
        {
            command = (command ?? string.Empty).Trim().TrimStart('/');
            if (string.IsNullOrEmpty(command))
            {
                PrintWarning($"Invalid chat command, defaulting to '{fallback}'");
                return fallback;
            }

            return command.ToLowerInvariant();
        }

        private string SanitizeConsoleCommand(string command, string fallback)
        {
            command = (command ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(command))
            {
                PrintWarning($"Invalid console command, defaulting to '{fallback}'");
                return fallback;
            }

            return command.ToLowerInvariant();
        }

        protected override void LoadDefaultConfig() => _config = new Configuration
        {
            Behaviour = new Configuration.BehaviourSettings
            {
                Ignored = new List<string>
                {
                    "scientistjunkpile.prefab",
                    "scarecrow.prefab"
                }
            }
        };

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region -Localisation-

        private string GetMessage(string key, string userId = null)
        {
            return lang.GetMessage(key, this, userId);
        }
        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["ChatBroadcast"] = "[Night Zombies] Spawned {0} NPCs",
                ["ChatBroadcastSeparate"] = "[Night Zombies] Spawned {0} scientists | Spawned {1} scarecrows"
            }, this);
        }

        #endregion
    }
}