/*
 * Estate Upkeep
 * Copyright © 2026 CrabSerg
 *
 * A Copper Dreams project
 *
 * Author: CrabSerg
 * Official identity: Estate Upkeep by CrabSerg / Copper Dreams
 * License: GPL-3.0-or-later
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * This source code is free software distributed under the
 * GNU General Public License v3.0 or later.
 *
 * Modified versions must not be represented as official
 * CrabSerg / Copper Dreams releases without permission.
 */

using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Configuration;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Estate Upkeep", "CrabSerg", "1.0.1")]
    [Description("Extends TC upkeep to authorized detached estate structures and optionally protects transport from decay inside TC privilege zones.")]
    public class EstateUpkeep : RustPlugin
    {
        private const string ProjectAuthor = "CrabSerg";
        private const string ProjectFamily = "Copper Dreams";
        private const string ProjectIdentity = "Estate Upkeep by CrabSerg • Copper Dreams";
        private const string AdminPermission = "estateupkeep.admin";

        private const float RayDistance = 10f;
        private const float MinutesPerDay = 1440f;
        private const float SecondsPerDay = 86400f;

        // Estate association for doors, batteries, solar panels and other deployables.
        private const float DeployableAssociationRadius = 4.5f;

        // Runtime billing tick. Exact fractional debt is accumulated, so this does
        // not mean one whole resource is charged every minute.
        private const float BillingTickSeconds = 60f;

        // Production cycle is now enabled.
        private const bool AutomaticBillingEnabled = true;

        // Persistent runtime state is flushed periodically and on important state changes.
        private const float DataSaveIntervalSeconds = 60f;

        // Repair only HP that Estate Upkeep has tracked as decay damage.
        // This prevents free healing of raid/combat/other damage.
        private const float RepairTickSeconds = 30f;
        private const float RepairFractionOfMaxHealthPerMinute = 0.01f;
        private const float MembershipRefreshSeconds = 120f;
        private const float ProtectedTransportRefreshSeconds = 5f;
        private const float ServerSaveCheckpointFreezeSeconds = 30f;
        private const float StartupTransportReconcileFirstDelaySeconds = 5f;
        private const float StartupTransportReconcileSecondDelaySeconds = 20f;
        private const float StartupTransportReconcileThirdDelaySeconds = 45f;
        private const float StartupTransportReconcileFinalDelaySeconds = 90f;
        private const float StartupTransportSpawnReconcileDelaySeconds = 0.75f;
        private const float RestartCheckpointMaxBootAgeSeconds = 600f;

        private readonly Dictionary<string, ItemDefinition> _deployableItemByPrefab =
            new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<int, ItemBlueprint> _blueprintByItemId =
            new Dictionary<int, ItemBlueprint>();

        private readonly Dictionary<BuildingPrivlidge, EstateAccount> _accounts =
            new Dictionary<BuildingPrivlidge, EstateAccount>();

        private readonly HashSet<BaseEntity> _protectedEntities =
            new HashSet<BaseEntity>();

        // All currently eligible estate combat entities, paid or unpaid.
        private readonly HashSet<BaseCombatEntity> _eligibleEstateCombatEntities =
            new HashSet<BaseCombatEntity>();

        private readonly Dictionary<BaseCombatEntity, BuildingPrivlidge> _estateTcByCombatEntity =
            new Dictionary<BaseCombatEntity, BuildingPrivlidge>();

        // Only decay-caused missing HP is repaired automatically.
        private readonly Dictionary<BaseCombatEntity, float> _decayRepairDebt =
            new Dictionary<BaseCombatEntity, float>();

        // PERFORMANCE CACHE:
        // Full world enumeration happens once at plugin startup only.
        // Spawn/kill hooks maintain these collections afterwards.
        private readonly HashSet<BuildingBlock> _cachedBuildingBlocks =
            new HashSet<BuildingBlock>();

        private readonly HashSet<BaseEntity> _cachedEstateCandidates =
            new HashSet<BaseEntity>();

        // Transport cache is intentionally independent from OwnerID.
        // Rust vehicles (notably modular cars) may not satisfy the player-owned
        // candidate filter used by Estate deployables.
        private readonly HashSet<BaseCombatEntity> _cachedTransports =
            new HashSet<BaseCombatEntity>();

        // Maintained during normal runtime, when TC privilege queries are reliable.
        // OnServerSave snapshots HP from this set without re-querying TC privilege.
        private readonly HashSet<BaseCombatEntity> _runtimeProtectedTransports =
            new HashSet<BaseCombatEntity>();

        private Dictionary<string, StoredTransportShutdownState> _startupPendingTransportSnapshot =
            new Dictionary<string, StoredTransportShutdownState>(StringComparer.Ordinal);

        private readonly HashSet<BaseCombatEntity> _startupReconcileDisqualifiedEntities =
            new HashSet<BaseCombatEntity>();

        private long _transportCheckpointFrozenUntilUnix;
        private bool _startupTransportReconcileActive;
        private bool _startupSpawnReconcileQueued;

        private int _initialWorldEntityCount;
        private double _lastSnapshotMilliseconds;
        private int _lastSnapshotBuildingsScanned;
        private int _lastSnapshotCandidatesScanned;

        private int _blockedTransportDecayEvents;
        private int _startupTransportRestoreEntities;
        private float _startupTransportRestoreHp;
        private int _startupTransportSnapshotRecords;
        private int _startupTransportMatchedRecords;
        private int _startupTransportUnmatchedRecords;
        private bool _startupRestartCheckpointAccepted;
        private long _startupRestartCheckpointAgeAtShutdownSeconds;
        private string _startupRestartCheckpointSource = string.Empty;
        private int _lastProtectedTransportRefreshScanned;
        private int _lastProtectedTransportRefreshFound;
        private int _lastRestartCheckpointRootCount;
        private int _lastRestartCheckpointRecordCount;
        private int _startupTransportReconcilePasses;
        private float _startupTransportDelayedRestoreHp;
        private int _startupTransportDelayedRestoreEntities;
        private int _startupRestartCandidateRoots;
        private int _startupRestartCandidateHealthEntities;
        private int _startupSpawnTriggeredReconcilePasses;
        private int _startupUnsourcedTransportDamageEvents;
        private int _startupSelfSourcedTransportDamageEvents;
        private int _startupExternalSourcedTransportDamageEvents;
        private int _startupSourcedTransportDamageDisqualifications;
        private int _startupDisqualifiedTransportEntities;
        private string _lastStartupDamageDiagnostic = "NONE";

        private DynamicConfigFile _dataFile;
        private StoredData _storedData = new StoredData();
        private bool _dataDirty;
        private bool _serverInitializationComplete;

        private string _currentServerBootId = string.Empty;
        private string _startupRestartDetectionMode = "NONE";
        private bool _startupCheckpointBootMatchesCurrent;
        private long _startupRestartCheckpointAgeAtBootSeconds = -1;

        private PluginConfig _config;

        private int _woodItemId;
        private int _stoneItemId;
        private int _metalItemId;
        private int _hqmItemId;

        private void Init()
        {
            permission.RegisterPermission(AdminPermission, this);
        }

        private bool HasAdminAccess(BasePlayer player)
        {
            return
                player != null &&
                (
                    player.IsAdmin ||
                    permission.UserHasPermission(player.UserIDString, AdminPermission)
                );
        }

        private bool RequireAdmin(BasePlayer player)
        {
            if (HasAdminAccess(player))
                return true;

            if (player != null)
            {
                player.ChatMessage(
                    "<color=#ff8c42>[Estate Upkeep]</color> Admin permission required."
                );
            }

            return false;
        }

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.CreateDefault();
            SavePluginConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();

                if (_config == null)
                    throw new Exception("Config returned null.");
            }
            catch (Exception ex)
            {
                PrintWarning($"Invalid config; using defaults. {ex.Message}");
                _config = PluginConfig.CreateDefault();
            }

            NormalizeConfig();
            SavePluginConfig();
        }

        private void NormalizeConfig()
        {
            if (_config == null)
                _config = PluginConfig.CreateDefault();

            if (_config.VehicleProtection == null)
                _config.VehicleProtection = new VehicleProtectionConfig();

            if (_config.VehicleProtection.RestartMatchRadiusMeters < 1f)
                _config.VehicleProtection.RestartMatchRadiusMeters = 8f;

            if (_config.VehicleProtection.RestartCheckpointMaxAgeAtShutdownSeconds < 10f)
                _config.VehicleProtection.RestartCheckpointMaxAgeAtShutdownSeconds = 90f;
        }

        private void SavePluginConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void OnServerInitialized()
        {
            if (_serverInitializationComplete)
            {
                Puts(
                    "Duplicate OnServerInitialized hook ignored; " +
                    "existing Estate Upkeep timers/state remain active."
                );
                return;
            }

            _serverInitializationComplete = true;

            NormalizeConfig();

            _currentServerBootId = GetCurrentServerBootId();

            CacheResourceItemIds();
            BuildDeployableItemIndex();
            BuildBlueprintIndex();
            LoadPersistentData();

            BuildInitialEntityCache();

            // Restore only HP that disappeared across the immediately preceding
            // clean server restart. Existing pre-shutdown damage is preserved.
            RestoreTransportAfterServerRestart();

            EstateSnapshot initialSnapshot = BuildEstateSnapshot();
            RefreshEstateMembership(initialSnapshot);
            RestorePersistentState(initialSnapshot);

            // Build transport protection membership during normal runtime.
            // Restart checkpointing later uses this cache directly.
            RefreshRuntimeProtectedTransportCache();
            RefreshLiveTransportRestartCheckpoint("StartupRuntime");

            // Run one production billing pass after restore. Restored accounts have
            // LastTick reset to "now", so server/plugin downtime is not back-billed.
            ProcessEstateBilling(true);

            timer.Every(MembershipRefreshSeconds, () =>
            {
                PruneEntityCaches();

                EstateSnapshot snapshot = BuildEstateSnapshot();
                RefreshEstateMembership(snapshot);
                RebuildProtectedEntitySet(snapshot);
            });

            timer.Every(RepairTickSeconds, () =>
            {
                ProcessEstateRepair();
            });

            timer.Every(ProtectedTransportRefreshSeconds, () =>
            {
                RefreshRuntimeProtectedTransportCache();
                RefreshLiveTransportRestartCheckpoint("Runtime");
            });

            timer.Every(BillingTickSeconds, () =>
            {
                ProcessEstateBilling(false);
            });

            timer.Every(DataSaveIntervalSeconds, () =>
            {
                SavePersistentData(false);
            });

            Puts(
                $"{ProjectIdentity} | v1.0.1 | GPL-3.0-or-later"
            );

            Puts(
                $"CORE MODE: automatic billing ON; persistence ON; " +
                $"cached blocks={_cachedBuildingBlocks.Count}, cached estate candidates={_cachedEstateCandidates.Count}, " +
                $"cached transports={_cachedTransports.Count}; " +
                $"transport anti-decay={(_config.VehicleProtection.Enabled ? "ON" : "OFF")}; " +
                $"process boot detection={(!string.IsNullOrEmpty(_currentServerBootId) ? "READY" : "UNAVAILABLE")}; " +
                $"independent TC estates only."
            );
        }

        private void OnServerSave()
        {
            if (_storedData == null)
                _storedData = new StoredData();

            // A save can be part of shutdown. Do not replace the already-live
            // checkpoint here: testing showed the vehicle can lose restart HP before
            // this hook completes. Freeze periodic checkpoint updates briefly instead.
            _transportCheckpointFrozenUntilUnix =
                GetUnixTimeSeconds() + (long)ServerSaveCheckpointFreezeSeconds;

            SavePersistentData(true);

            Puts(
                $"Server save: preserved live transport checkpoint; records=" +
                $"{(_storedData.TransportShutdown != null ? _storedData.TransportShutdown.Count : 0)}, " +
                $"updates frozen for {ServerSaveCheckpointFreezeSeconds:0}s."
            );
        }

        private void OnServerShutdown()
        {
            if (_storedData == null)
                _storedData = new StoredData();

            _storedData.CleanShutdownPending = true;
            _storedData.CleanShutdownMarkedUnix = GetUnixTimeSeconds();

            // IMPORTANT: do NOT recapture transport HP here. Testing showed Rust can
            // already have applied restart/shutdown decay by the time this hook runs.
            MarkDataDirty();
            SavePersistentData(true);

            long age =
                _storedData.TransportCheckpointCapturedUnix > 0
                    ? Mathf.Max(
                        0,
                        (int)(
                            _storedData.CleanShutdownMarkedUnix -
                            _storedData.TransportCheckpointCapturedUnix
                        )
                    )
                    : -1;

            Puts(
                $"Best-effort clean shutdown marker saved. Restart detection primarily uses " +
                $"Rust process boot identity. Checkpoint source=" +
                $"{_storedData.TransportCheckpointSource}, records=" +
                $"{(_storedData.TransportShutdown != null ? _storedData.TransportShutdown.Count : 0)}, " +
                $"ageAtShutdown={(age >= 0 ? age.ToString() + "s" : "none")}."
            );
        }

        private void Unload()
        {
            SavePersistentData(true);
            _serverInitializationComplete = false;

            _protectedEntities.Clear();
            _eligibleEstateCombatEntities.Clear();
            _estateTcByCombatEntity.Clear();
            _decayRepairDebt.Clear();
            _accounts.Clear();
            _cachedBuildingBlocks.Clear();
            _cachedEstateCandidates.Clear();
            _cachedTransports.Clear();
            _runtimeProtectedTransports.Clear();
            _startupPendingTransportSnapshot.Clear();
            _startupReconcileDisqualifiedEntities.Clear();
        }

        [ChatCommand("estateperf")]
        private void EstatePerformanceCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!RequireAdmin(player))
                return;

            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1 — Performance]</color>");
            player.ChatMessage($"Initial full-world scan: {_initialWorldEntityCount} entities (startup only)");
            player.ChatMessage($"Cached BuildingBlocks: {_cachedBuildingBlocks.Count}");
            player.ChatMessage($"Cached player-owned estate candidates: {_cachedEstateCandidates.Count}");
            player.ChatMessage($"Cached supported transports: {_cachedTransports.Count}");
            player.ChatMessage($"Last snapshot blocks scanned: {_lastSnapshotBuildingsScanned}");
            player.ChatMessage($"Last snapshot candidates scanned: {_lastSnapshotCandidatesScanned}");
            player.ChatMessage($"Last snapshot build time: {_lastSnapshotMilliseconds:0.###} ms");
            player.ChatMessage($"Active TC accounts: {_accounts.Count}");
            player.ChatMessage($"Tracked repair debts: {_decayRepairDebt.Count}");
            player.ChatMessage($"Transport anti-decay: {(_config.VehicleProtection.Enabled ? "ON" : "OFF")}");
            player.ChatMessage($"Blocked transport decay events since load: {_blockedTransportDecayEvents}");
            player.ChatMessage($"Runtime protected transport cache: {_runtimeProtectedTransports.Count}");
            player.ChatMessage($"Last protection refresh: scanned {_lastProtectedTransportRefreshScanned}, protected {_lastProtectedTransportRefreshFound}");
            player.ChatMessage($"Last restart checkpoint capture: roots {_lastRestartCheckpointRootCount}, records {_lastRestartCheckpointRecordCount}");
            player.ChatMessage($"Checkpoint updates frozen: {(GetUnixTimeSeconds() < _transportCheckpointFrozenUntilUnix ? "YES" : "NO")}");
            player.ChatMessage($"Restart checkpoint accepted: {(_startupRestartCheckpointAccepted ? "YES" : "NO")}");
            player.ChatMessage($"Restart detection mode: {_startupRestartDetectionMode}");
            player.ChatMessage($"Checkpoint process matches current: {(_startupCheckpointBootMatchesCurrent ? "YES" : "NO")}");
            player.ChatMessage($"Restart checkpoint source: {(string.IsNullOrEmpty(_startupRestartCheckpointSource) ? "NONE" : _startupRestartCheckpointSource)}");
            player.ChatMessage($"Checkpoint age at boot: {(_startupRestartCheckpointAgeAtBootSeconds >= 0 ? _startupRestartCheckpointAgeAtBootSeconds.ToString() + "s" : "N/A")}");
            player.ChatMessage($"Legacy checkpoint age at shutdown: {(_startupRestartCheckpointAgeAtShutdownSeconds >= 0 ? _startupRestartCheckpointAgeAtShutdownSeconds.ToString() + "s" : "N/A")}");
            player.ChatMessage($"Restart snapshot records loaded: {_startupTransportSnapshotRecords}");
            player.ChatMessage($"Restart snapshot records matched: {_startupTransportMatchedRecords}");
            player.ChatMessage($"Restart snapshot records unmatched: {_startupTransportUnmatchedRecords}");
            player.ChatMessage($"Startup transport HP restored: {_startupTransportRestoreHp:0.##} across {_startupTransportRestoreEntities} entity(s)");
            player.ChatMessage($"Startup delayed reconcile active: {(_startupTransportReconcileActive ? "YES" : "NO")}");
            player.ChatMessage($"Startup reconcile passes: {_startupTransportReconcilePasses}");
            player.ChatMessage($"Spawn-triggered reconcile passes: {_startupSpawnTriggeredReconcilePasses}");
            player.ChatMessage($"Startup restart candidates: roots {_startupRestartCandidateRoots}, health entities {_startupRestartCandidateHealthEntities}");
            player.ChatMessage($"Unsourced startup transport damage events: {_startupUnsourcedTransportDamageEvents}");
            player.ChatMessage($"Self-sourced startup transport damage events: {_startupSelfSourcedTransportDamageEvents}");
            player.ChatMessage($"External sourced startup transport damage events: {_startupExternalSourcedTransportDamageEvents}");
            player.ChatMessage($"Sourced startup damage disqualifications: {_startupSourcedTransportDamageDisqualifications}");
            player.ChatMessage($"Disqualified startup transport entities: {_startupDisqualifiedTransportEntities}");
            player.ChatMessage($"Last startup damage diagnostic: {_lastStartupDamageDiagnostic}");
            player.ChatMessage($"Delayed HP restored: {_startupTransportDelayedRestoreHp:0.##} across {_startupTransportDelayedRestoreEntities} entity(s)");
            player.ChatMessage("<color=#9bd36a>Startup restart matching does NOT depend on TC privilege readiness.</color>");
            player.ChatMessage("<color=#9bd36a>Recurring estate snapshots do NOT enumerate BaseNetworkable.serverEntities.</color>");
        }

        [ChatCommand("estatecheck")]
        private void EstateCheckCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!RequireAdmin(player))
                return;

            BuildingBlock block = GetLookedAtBuildingBlock(player);
            if (block == null)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Look directly at a foundation, wall, floor, roof, etc.");
                return;
            }

            BuildingPrivlidge ownTc = block.GetBuildingPrivilege();
            BuildingPrivlidge privilegeTc = GetSpatialPrivilege(block);

            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1]</color>");
            player.ChatMessage($"BuildingID: {block.buildingID}");
            player.ChatMessage($"OwnerID: {block.OwnerID}");
            player.ChatMessage($"Own-building TC: {(ownTc == null ? "NOT FOUND" : "FOUND")}");
            player.ChatMessage($"Privilege TC: {(privilegeTc == null ? "NOT FOUND" : $"FOUND | Net ID: {privilegeTc.net?.ID}")}");

            if (privilegeTc == null)
            {
                player.ChatMessage("Eligible: NO");
                return;
            }

            bool ownerAuthorized = IsAuthorized(privilegeTc, block.OwnerID);
            bool detached = ownTc == null;

            player.ChatMessage($"Detached: {(detached ? "YES" : "NO")}");
            player.ChatMessage($"Block owner authorized: {(ownerAuthorized ? "YES" : "NO")}");
            player.ChatMessage($"Eligible: {(detached && ownerAuthorized ? "YES" : "NO")}");

            EstateSnapshot snapshot = BuildEstateSnapshot();

            EstateTcSnapshot tcSnapshot;
            if (!snapshot.ByTc.TryGetValue(privilegeTc, out tcSnapshot))
            {
                player.ChatMessage("Estate total: no eligible detached structures found for this TC.");
                return;
            }

            player.ChatMessage("<color=#e0a000>--- TC Estate Summary ---</color>");
            player.ChatMessage($"Detached buildings: {tcSnapshot.Buildings.Count}");
            player.ChatMessage($"BuildingBlocks: {tcSnapshot.BlockCount}");
            player.ChatMessage($"Associated entities: {tcSnapshot.AssociatedEntities.Count}");
            player.ChatMessage($"Doors/windows: {tcSnapshot.DoorWindowCount}");
            player.ChatMessage($"Batteries: {tcSnapshot.BatteryCount} | Solar: {tcSnapshot.SolarCount}");
            player.ChatMessage($"Other electrical: {tcSnapshot.OtherElectricalCount}");
            player.ChatMessage($"Other deployables/entities: {tcSnapshot.OtherEntityCount}");

            player.ChatMessage("<color=#e0a000>--- Extra Estate Upkeep / 24h ---</color>");
            player.ChatMessage($"Wood: {CeilPositive(tcSnapshot.Rate.WoodPerDay)}");
            player.ChatMessage($"Stone: {CeilPositive(tcSnapshot.Rate.StonePerDay)}");
            player.ChatMessage($"Metal Fragments: {CeilPositive(tcSnapshot.Rate.MetalPerDay)}");
            player.ChatMessage($"HQM: {CeilPositive(tcSnapshot.Rate.HqmPerDay)}");

            EstateAccount account;
            if (_accounts.TryGetValue(privilegeTc, out account))
            {
                player.ChatMessage($"Billing state: {(account.Paid ? "<color=#9bd36a>PAID / DECAY PROTECTED</color>" : "<color=#ff8c42>UNPAID / DECAY ALLOWED</color>")}");
                player.ChatMessage(
                    $"Pending fractional debt: W {account.DebtWood:0.###}, S {account.DebtStone:0.###}, " +
                    $"M {account.DebtMetal:0.###}, HQM {account.DebtHqm:0.###}"
                );

                if (!string.IsNullOrEmpty(account.LastFailure))
                    player.ChatMessage($"Last billing issue: {account.LastFailure}");
            }

            BaseCombatEntity lookedCombat = block as BaseCombatEntity;
            float trackedRepairDebt;
            if (lookedCombat != null && _decayRepairDebt.TryGetValue(lookedCombat, out trackedRepairDebt))
            {
                player.ChatMessage($"Tracked decay repair debt: {trackedRepairDebt:0.##} HP");
            }

            player.ChatMessage("<color=#9bd36a>Automatic billing: ON.</color> Persistent state: ON. Core scope: one TC manages its own detached Estate.");
        }

        [ChatCommand("estate")]
        private void EstateStatusCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (args != null && args.Length > 0)
            {
                string subCommand = args[0].ToLowerInvariant();

                if (subCommand == "help")
                {
                    SendEstateHelp(player);
                    return;
                }

                if (subCommand == "status")
                {
                    // Continue into the normal Estate status view below.
                }
                else if (subCommand == "transport" || subCommand == "vehicles")
                {
                    HandleTransportProtectionCommand(player, args);
                    return;
                }
                else if (subCommand == "transportcheck" || subCommand == "vehiclecheck")
                {
                    HandleTransportCheckCommand(player);
                    return;
                }
                else
                {
                    player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Unknown subcommand. Use /estate help");
                    return;
                }
            }

            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            if (tc == null)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Stand inside the privilege zone of the TC you want to inspect.");
                return;
            }

            EstateSnapshot snapshot = BuildEstateSnapshot();
            EstateTcSnapshot tcSnapshot;

            if (!snapshot.ByTc.TryGetValue(tc, out tcSnapshot))
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> This TC currently has no eligible detached estate structures.");
                return;
            }

            EstateAccount account;
            _accounts.TryGetValue(tc, out account);

            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1 — Estate]</color>");
            player.ChatMessage($"TC Net ID: {tc.net?.ID}");
            player.ChatMessage($"Detached buildings: {tcSnapshot.Buildings.Count}");
            player.ChatMessage($"Associated entities: {tcSnapshot.AssociatedEntities.Count}");
            player.ChatMessage(
                $"Extra /24h -> Wood {CeilPositive(tcSnapshot.Rate.WoodPerDay)}, " +
                $"Stone {CeilPositive(tcSnapshot.Rate.StonePerDay)}, " +
                $"Metal {CeilPositive(tcSnapshot.Rate.MetalPerDay)}, " +
                $"HQM {CeilPositive(tcSnapshot.Rate.HqmPerDay)}"
            );
            player.ChatMessage($"Status: {(account != null && account.Paid ? "<color=#9bd36a>PAID / PROTECTED / REPAIR ACTIVE</color>" : "<color=#ff8c42>UNPAID / DECAY ALLOWED</color>")}");
            player.ChatMessage("Automatic billing: ON | Persistent state: ON | Decay repair: ON");
            player.ChatMessage($"Transport anti-decay: {(_config.VehicleProtection.Enabled ? "ON" : "OFF")}");
        }

        private void SendEstateHelp(BasePlayer player)
        {
            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1 — Help]</color>");
            player.ChatMessage("<color=#ffd479>by CrabSerg • A Copper Dreams project</color>");
            player.ChatMessage("/estate — status for the TC privilege zone you are standing in");
            player.ChatMessage("/estate status — same status view");
            player.ChatMessage("/estate transport status — transport anti-decay status");

            if (HasAdminAccess(player))
            {
                player.ChatMessage("<color=#ffd479>Admin:</color> /estate transport on|off");
                player.ChatMessage("<color=#ffd479>Admin:</color> /estate transportcheck");
                player.ChatMessage("<color=#ffd479>Admin:</color> /estatecheck");
                player.ChatMessage("<color=#ffd479>Admin:</color> /estateperf");
                player.ChatMessage("<color=#ffd479>Admin:</color> /estatechargecheck");
                player.ChatMessage("<color=#ffd479>Admin:</color> /estatecharge confirm");
                player.ChatMessage("<color=#ffd479>Admin:</color> /estaterepaircheck");
                player.ChatMessage("<color=#ffd479>Admin:</color> /estaterepairmark");
            }

            player.ChatMessage("<color=#9bd36a>Transport protection never consumes TC upkeep resources.</color>");
        }

        private void HandleTransportProtectionCommand(BasePlayer player, string[] args)
        {
            if (args.Length < 2)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Usage: /estate transport on | off | status");
                return;
            }

            string action = args[1].ToLowerInvariant();

            if (action == "status")
            {
                SendTransportProtectionStatus(player);
                return;
            }

            if (!RequireAdmin(player))
                return;

            if (action != "on" && action != "off")
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Usage: /estate transport on | off | status");
                return;
            }

            bool enabled = action == "on";
            _config.VehicleProtection.Enabled = enabled;
            SavePluginConfig();

            player.ChatMessage(
                $"<color=#9bd36a>[Estate Upkeep]</color> Transport anti-decay protection: " +
                $"<color=#{(enabled ? "9bd36a" : "ff8c42")}>{(enabled ? "ON" : "OFF")}</color>"
            );

            player.ChatMessage(
                "Only Rust Decay damage is suppressed while supported transport is inside a TC privilege zone. " +
                "TC resources are never consumed by transport protection."
            );
        }

        private void SendTransportProtectionStatus(BasePlayer player)
        {
            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1 — Transport Protection]</color>");
            player.ChatMessage($"Enabled: {(_config.VehicleProtection.Enabled ? "<color=#9bd36a>YES</color>" : "<color=#ff8c42>NO</color>")}");
            player.ChatMessage($"Prevent decay inside TC: {(_config.VehicleProtection.PreventDecayInsideTc ? "YES" : "NO")}");
            player.ChatMessage($"Land transport: {(_config.VehicleProtection.ProtectLand ? "YES" : "NO")}");
            player.ChatMessage($"Water transport: {(_config.VehicleProtection.ProtectWater ? "YES" : "NO")}");
            player.ChatMessage($"Air transport: {(_config.VehicleProtection.ProtectAir ? "YES" : "NO")}");
            player.ChatMessage($"Restart HP preservation: {(_config.VehicleProtection.RestoreProtectedHpAfterCleanRestart ? "YES" : "NO")}");
            player.ChatMessage($"Restart match radius: {_config.VehicleProtection.RestartMatchRadiusMeters:0.#} m");
            player.ChatMessage($"Protected transport/checkpoint refresh: {ProtectedTransportRefreshSeconds:0}s");
            player.ChatMessage($"Server-save checkpoint freeze: {ServerSaveCheckpointFreezeSeconds:0}s");
            player.ChatMessage(
                $"Startup reconcile window: immediate + " +
                $"{StartupTransportReconcileFirstDelaySeconds:0}s + " +
                $"{StartupTransportReconcileSecondDelaySeconds:0}s + " +
                $"{StartupTransportReconcileThirdDelaySeconds:0}s + " +
                $"{StartupTransportReconcileFinalDelaySeconds:0}s; transport spawns trigger extra passes"
            );
            player.ChatMessage("Startup restart matching requires current TC privilege: NO");
            player.ChatMessage("Restart detection: Rust process boot identity (clean-shutdown hook not required)");
            player.ChatMessage("Startup damage safety: canonical transport roots identify self/module damage; only external sourced damage blocks restore");
            player.ChatMessage($"Max process-restart checkpoint age: {RestartCheckpointMaxBootAgeSeconds:0}s");
            player.ChatMessage($"Legacy max checkpoint age at shutdown: {_config.VehicleProtection.RestartCheckpointMaxAgeAtShutdownSeconds:0}s");
            player.ChatMessage("Upkeep resource cost: 0 (transport protection never withdraws from TC)");
        }

        private void HandleTransportCheckCommand(BasePlayer player)
        {
            if (!RequireAdmin(player))
                return;

            BaseCombatEntity directHit = GetLookedAtCombatEntity(player);

            if (directHit == null)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Look at the transport or something immediately beside/under it.");
                return;
            }

            BaseCombatEntity resolved;
            string category;
            bool resolvedByHierarchy =
                TryResolveProtectedTransport(directHit, out resolved, out category);

            if (!resolvedByHierarchy)
            {
                resolved = FindNearbySupportedTransport(directHit.transform.position, 5f);

                if (resolved != null && !TryClassifyProtectedTransport(resolved, out category))
                    resolved = null;
            }

            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1 — Transport Check]</color>");
            player.ChatMessage($"Direct hit entity: {directHit.ShortPrefabName}");
            player.ChatMessage($"Direct hit runtime type: {directHit.GetType().Name}");

            if (resolved == null)
            {
                player.ChatMessage("<color=#ff8c42>Supported transport nearby/parent: NO</color>");
                player.ChatMessage($"Transport cache currently contains: {_cachedTransports.Count} entity(s).");
                return;
            }

            bool directIsTransport = resolved == directHit;
            bool parentResolution =
                !directIsTransport && IsTransformAncestor(resolved.transform, directHit.transform);
            bool nearbyResolution = !directIsTransport && !parentResolution;

            player.ChatMessage($"Resolved transport: {resolved.ShortPrefabName}");
            player.ChatMessage($"Resolved runtime type: {resolved.GetType().Name}");
            player.ChatMessage($"Resolved via: {(directIsTransport ? "DIRECT" : parentResolution ? "PARENT" : "NEARBY")}");
            player.ChatMessage($"Supported transport: <color=#9bd36a>YES</color>");
            player.ChatMessage($"Category: {category}");

            BuildingPrivlidge tc = GetSpatialPrivilege(resolved);
            player.ChatMessage($"TC privilege at transport: {(tc == null ? "NOT FOUND" : $"FOUND | Net ID: {tc.net?.ID}")}");
            player.ChatMessage($"Protection setting: {(_config.VehicleProtection.Enabled ? "ON" : "OFF")}");

            bool categoryEnabled = IsTransportCategoryEnabled(category);
            player.ChatMessage($"Category enabled: {(categoryEnabled ? "YES" : "NO")}");

            bool protectedNow =
                _config.VehicleProtection.Enabled &&
                _config.VehicleProtection.PreventDecayInsideTc &&
                categoryEnabled &&
                tc != null;

            if (protectedNow)
                _runtimeProtectedTransports.Add(resolved);
            else
                _runtimeProtectedTransports.Remove(resolved);

            player.ChatMessage($"Decay protected right now: {(protectedNow ? "<color=#9bd36a>YES</color>" : "<color=#ff8c42>NO</color>")}");
            player.ChatMessage($"Blocked decay events since load: {_blockedTransportDecayEvents}");
        }

        // Admin dry-run command: calculates the exact 24h estate charge against the
        // current TC inventory and DOES NOT modify any item or protection state.
        [ChatCommand("estatechargecheck")]
        private void EstateChargeCheckCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!RequireAdmin(player))
                return;

            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            if (tc == null)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Stand inside the target TC privilege zone.");
                return;
            }

            EstateSnapshot snapshot = BuildEstateSnapshot();
            EstateTcSnapshot tcSnapshot;

            if (!snapshot.ByTc.TryGetValue(tc, out tcSnapshot))
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> No eligible detached structures for this TC.");
                return;
            }

            int wood = CeilPositive(tcSnapshot.Rate.WoodPerDay);
            int stone = CeilPositive(tcSnapshot.Rate.StonePerDay);
            int metal = CeilPositive(tcSnapshot.Rate.MetalPerDay);
            int hqm = CeilPositive(tcSnapshot.Rate.HqmPerDay);

            int woodBefore = GetResourceAmount(tc.inventory, _woodItemId);
            int stoneBefore = GetResourceAmount(tc.inventory, _stoneItemId);
            int metalBefore = GetResourceAmount(tc.inventory, _metalItemId);
            int hqmBefore = GetResourceAmount(tc.inventory, _hqmItemId);

            bool enough =
                woodBefore >= wood &&
                stoneBefore >= stone &&
                metalBefore >= metal &&
                hqmBefore >= hqm;

            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1 — DRY RUN]</color>");
            player.ChatMessage($"TC Net ID: {tc.net?.ID}");
            player.ChatMessage($"Detached buildings: {tcSnapshot.Buildings.Count}");
            player.ChatMessage($"Associated entities: {tcSnapshot.AssociatedEntities.Count}");

            player.ChatMessage("<color=#e0a000>Would consume for 24h:</color>");
            player.ChatMessage($"Wood {wood}, Stone {stone}, Metal {metal}, HQM {hqm}");

            player.ChatMessage("<color=#e0a000>TC totals BEFORE:</color>");
            player.ChatMessage($"Wood {woodBefore}, Stone {stoneBefore}, Metal {metalBefore}, HQM {hqmBefore}");

            player.ChatMessage("<color=#e0a000>TC totals AFTER (simulation):</color>");
            player.ChatMessage(
                $"Wood {woodBefore - wood}, Stone {stoneBefore - stone}, " +
                $"Metal {metalBefore - metal}, HQM {hqmBefore - hqm}"
            );

            player.ChatMessage($"Enough resources: {(enough ? "<color=#9bd36a>YES</color>" : "<color=#ff8c42>NO</color>")}");
            player.ChatMessage("<color=#9bd36a>DRY RUN ONLY — NOTHING WAS CONSUMED OR MODIFIED. Automatic billing continues independently.</color>");
        }

        [ChatCommand("estaterepaircheck")]
        private void EstateRepairCheckCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!RequireAdmin(player))
                return;

            BaseCombatEntity target = GetLookedAtCombatEntity(player);
            if (target == null)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Look directly at an estate block/entity.");
                return;
            }

            RefreshEstateMembership();

            BuildingPrivlidge tc;
            bool mapped = _estateTcByCombatEntity.TryGetValue(target, out tc);

            EstateAccount account = null;
            bool paid = mapped && tc != null && _accounts.TryGetValue(tc, out account) && account.Paid;

            float debt;
            _decayRepairDebt.TryGetValue(target, out debt);

            player.ChatMessage("<color=#e0a000>[Estate Upkeep v1.0.1 — Repair Check]</color>");
            player.ChatMessage($"Entity: {target.ShortPrefabName}");
            player.ChatMessage($"HP: {target.Health():0.##}/{target.MaxHealth():0.##}");
            player.ChatMessage($"Estate mapped: {(mapped ? "YES" : "NO")}");
            player.ChatMessage($"TC: {(tc == null ? "NONE" : tc.net?.ID.ToString())}");
            player.ChatMessage($"Paid/protected: {(paid ? "<color=#9bd36a>YES</color>" : "<color=#ff8c42>NO</color>")}");
            player.ChatMessage($"Tracked decay repair debt: {debt:0.##} HP");
            player.ChatMessage($"Repair tick: {RepairTickSeconds:0}s | Rate: {RepairFractionOfMaxHealthPerMinute * 100f:0.##}% max HP/min");
        }

        // Admin-only migration/test helper:
        // Marks the CURRENT missing HP on one looked-at eligible estate entity as
        // known historical decay damage. This is intentionally manual so raid or
        // other damage is never assumed to be decay.
        [ChatCommand("estaterepairmark")]
        private void EstateRepairMarkCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!RequireAdmin(player))
                return;

            BaseCombatEntity target = GetLookedAtCombatEntity(player);
            if (target == null)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Look directly at the damaged estate block/entity.");
                return;
            }

            RefreshEstateMembership();

            if (!_eligibleEstateCombatEntities.Contains(target))
            {
                player.ChatMessage("<color=#ff8c42>[Estate Upkeep]</color> Target is not currently an eligible detached-estate entity.");
                return;
            }

            float maxHealth = target.MaxHealth();
            float currentHealth = target.Health();
            float missing = Mathf.Max(0f, maxHealth - currentHealth);

            if (missing <= 0.01f)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Target is already at full health.");
                return;
            }

            float existing;
            _decayRepairDebt.TryGetValue(target, out existing);

            // Set to at least the current missing HP; do not repeatedly stack the same deficit.
            _decayRepairDebt[target] = Mathf.Max(existing, missing);
            MarkDataDirty();

            player.ChatMessage(
                $"<color=#9bd36a>[Estate Upkeep]</color> Marked {missing:0.##} HP as historical decay repair debt."
            );
            player.ChatMessage(
                $"Current HP: {currentHealth:0.##}/{maxHealth:0.##}. Repair starts only while its Estate TC is PAID."
            );
        }

        // Admin test command: forces exactly one 24h estate bill immediately.
        // This REALLY removes resources if enough are present.
        [ChatCommand("estatecharge")]
        private void EstateChargeCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!RequireAdmin(player))
                return;

            if (args == null || args.Length == 0 || !args[0].Equals("confirm", StringComparison.OrdinalIgnoreCase))
            {
                player.ChatMessage("<color=#ffd479>[Estate Upkeep]</color> Real charge is guarded.");
                player.ChatMessage("Run /estatechargecheck first. To actually remove resources, use: /estatecharge confirm");
                return;
            }

            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            if (tc == null)
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> Stand inside the target TC privilege zone.");
                return;
            }

            EstateSnapshot snapshot = BuildEstateSnapshot();
            EstateTcSnapshot tcSnapshot;

            if (!snapshot.ByTc.TryGetValue(tc, out tcSnapshot))
            {
                player.ChatMessage("<color=#e0a000>[Estate Upkeep]</color> No eligible detached structures for this TC.");
                return;
            }

            int wood = CeilPositive(tcSnapshot.Rate.WoodPerDay);
            int stone = CeilPositive(tcSnapshot.Rate.StonePerDay);
            int metal = CeilPositive(tcSnapshot.Rate.MetalPerDay);
            int hqm = CeilPositive(tcSnapshot.Rate.HqmPerDay);

            string failure;
            if (!TryConsumeAtomic(tc, wood, stone, metal, hqm, out failure))
            {
                player.ChatMessage($"<color=#ff8c42>[Estate Upkeep]</color> Forced 24h charge FAILED: {failure}");
                return;
            }

            EstateAccount account = GetOrCreateAccount(tc);
            account.Paid = true;
            account.LastFailure = string.Empty;

            RebuildProtectedEntitySet(snapshot);
            RefreshEstateMembership(snapshot);
            ProcessEstateRepair();
            MarkDataDirty();
            SavePersistentData(true);

            player.ChatMessage(
                $"<color=#9bd36a>[Estate Upkeep]</color> CONFIRMED 24h test charge PAID: " +
                $"Wood {wood}, Stone {stone}, Metal {metal}, HQM {hqm}."
            );
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || info.damageTypes == null)
                return null;

            float decayDamage = info.damageTypes.Get(Rust.DamageType.Decay);

            if (decayDamage <= 0f)
            {
                if (_startupTransportReconcileActive)
                {
                    BaseCombatEntity startupTransportRoot;
                    string startupTransportCategory;

                    if (
                        TryResolveProtectedTransport(
                            entity,
                            out startupTransportRoot,
                            out startupTransportCategory
                        ) &&
                        IsTransportCategoryEnabled(startupTransportCategory)
                    )
                    {
                        // Important restart distinction:
                        //
                        // Rust can apply unsourced/server-side/physics HP changes while
                        // vehicles are settling during world startup. Those are exactly
                        // the losses the restart checkpoint is intended to reconcile.
                        //
                        // Real sourced damage has an Initiator (player/NPC/entity). In
                        // that case we disqualify the transport from startup healing so
                        // the plugin cannot undo legitimate damage after the server opens.
                        if (info.Initiator == null)
                        {
                            _startupUnsourcedTransportDamageEvents++;
                            _lastStartupDamageDiagnostic =
                                $"UNSOURCED target={entity.ShortPrefabName ?? entity.GetType().Name}";
                        }
                        else
                        {
                            BaseCombatEntity initiatorCombat =
                                info.Initiator as BaseCombatEntity;

                            BaseCombatEntity initiatorTransportRoot = null;
                            string initiatorTransportCategory = null;

                            bool initiatorIsSameTransport = false;

                            if (
                                initiatorCombat != null &&
                                TryResolveProtectedTransport(
                                    initiatorCombat,
                                    out initiatorTransportRoot,
                                    out initiatorTransportCategory
                                ) &&
                                initiatorTransportRoot != null &&
                                startupTransportRoot != null
                            )
                            {
                                if (ReferenceEquals(
                                    initiatorTransportRoot,
                                    startupTransportRoot
                                ))
                                {
                                    initiatorIsSameTransport = true;
                                }
                                else if (
                                    initiatorTransportRoot.net != null &&
                                    startupTransportRoot.net != null &&
                                    initiatorTransportRoot.net.ID ==
                                        startupTransportRoot.net.ID
                                )
                                {
                                    initiatorIsSameTransport = true;
                                }
                            }

                            string initiatorPrefab =
                                info.Initiator.ShortPrefabName ??
                                info.Initiator.GetType().Name;

                            string initiatorType =
                                info.Initiator.GetType().Name;

                            string targetPrefab =
                                entity.ShortPrefabName ??
                                entity.GetType().Name;

                            string targetType =
                                entity.GetType().Name;

                            string targetRootPrefab =
                                startupTransportRoot != null
                                    ? (
                                        startupTransportRoot.ShortPrefabName ??
                                        startupTransportRoot.GetType().Name
                                    )
                                    : "NONE";

                            string initiatorRootPrefab =
                                initiatorTransportRoot != null
                                    ? (
                                        initiatorTransportRoot.ShortPrefabName ??
                                        initiatorTransportRoot.GetType().Name
                                    )
                                    : "NONE";

                            if (initiatorIsSameTransport)
                            {
                                // Rust vehicle/module initialization can report damage
                                // with another entity from the SAME vehicle as Initiator.
                                // This is not an external attack and must remain eligible
                                // for restart checkpoint reconciliation.
                                _startupSelfSourcedTransportDamageEvents++;

                                _lastStartupDamageDiagnostic =
                                    $"SELF target={targetPrefab} ({targetType}), " +
                                    $"targetRoot={targetRootPrefab}, " +
                                    $"initiator={initiatorPrefab} ({initiatorType}), " +
                                    $"initiatorRoot={initiatorRootPrefab}";
                            }
                            else
                            {
                                // A genuinely external source touched the protected
                                // transport during the reconcile window. Do not heal it.
                                _startupExternalSourcedTransportDamageEvents++;
                                _startupSourcedTransportDamageDisqualifications++;

                                _lastStartupDamageDiagnostic =
                                    $"EXTERNAL target={targetPrefab} ({targetType}), " +
                                    $"targetRoot={targetRootPrefab}, " +
                                    $"initiator={initiatorPrefab} ({initiatorType}), " +
                                    $"initiatorRoot={initiatorRootPrefab}";

                                DisqualifyStartupReconcileEntity(entity);
                            }
                        }
                    }
                }

                return null;
            }

            // OPTIONAL TRANSPORT MODULE:
            // The decay hit may land on the root vehicle or on a child/module entity.
            // Resolve upward to the protected transport root, then use that root for
            // TC privilege. Only Decay is removed.
            if (
                _config != null &&
                _config.VehicleProtection != null &&
                _config.VehicleProtection.Enabled &&
                _config.VehicleProtection.PreventDecayInsideTc
            )
            {
                BaseCombatEntity transportRoot;
                string transportCategory;

                if (
                    TryResolveProtectedTransport(entity, out transportRoot, out transportCategory) &&
                    IsTransportCategoryEnabled(transportCategory)
                )
                {
                    BuildingPrivlidge transportTc = GetSpatialPrivilege(transportRoot);

                    if (transportTc != null)
                    {
                        _runtimeProtectedTransports.Add(transportRoot);
                        info.damageTypes.Scale(Rust.DamageType.Decay, 0f);
                        _blockedTransportDecayEvents++;
                        return null;
                    }
                }
            }

            BuildingPrivlidge estateTc;
            if (!_estateTcByCombatEntity.TryGetValue(entity, out estateTc))
                return null;

            EstateAccount account;
            bool paid = estateTc != null && _accounts.TryGetValue(estateTc, out account) && account.Paid;

            if (paid)
            {
                // Paid Estate: block only decay damage.
                info.damageTypes.Scale(Rust.DamageType.Decay, 0f);
                return null;
            }

            // Unpaid Estate: allow vanilla decay, but remember exactly how much
            // decay-caused HP may later be restored after upkeep becomes paid.
            float existing;
            _decayRepairDebt.TryGetValue(entity, out existing);
            _decayRepairDebt[entity] = existing + decayDamage;
            MarkDataDirty();

            return null;
        }

        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            CacheNetworkable(networkable);

            if (!_startupTransportReconcileActive || networkable == null)
                return;

            BaseCombatEntity combat = networkable as BaseCombatEntity;
            if (
                combat == null ||
                combat.IsDestroyed ||
                !_cachedTransports.Contains(combat)
            )
                return;

            QueueStartupSpawnReconcile();
        }

        private void QueueStartupSpawnReconcile()
        {
            if (
                !_startupTransportReconcileActive ||
                _startupSpawnReconcileQueued
            )
                return;

            _startupSpawnReconcileQueued = true;

            timer.Once(StartupTransportSpawnReconcileDelaySeconds, () =>
            {
                _startupSpawnReconcileQueued = false;

                if (!_startupTransportReconcileActive)
                    return;

                _startupSpawnTriggeredReconcilePasses++;
                ReconcileStartupTransportCheckpoint(true);
            });
        }

        private void OnEntityKill(BaseNetworkable networkable)
        {
            BuildingBlock block = networkable as BuildingBlock;
            if (block != null)
                _cachedBuildingBlocks.Remove(block);

            BaseEntity baseEntity = networkable as BaseEntity;
            if (baseEntity != null)
                _cachedEstateCandidates.Remove(baseEntity);

            BaseCombatEntity entity = networkable as BaseCombatEntity;
            if (entity != null)
            {
                _cachedTransports.Remove(entity);
                _runtimeProtectedTransports.Remove(entity);
            }

            if (entity == null)
                return;

            _eligibleEstateCombatEntities.Remove(entity);
            _estateTcByCombatEntity.Remove(entity);

            if (_decayRepairDebt.Remove(entity))
                MarkDataDirty();
        }

        private void DisqualifyStartupReconcileEntity(BaseCombatEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
                return;

            _startupReconcileDisqualifiedEntities.Add(entity);

            BaseCombatEntity root;
            string category;

            if (TryResolveProtectedTransport(entity, out root, out category) && root != null)
                _startupReconcileDisqualifiedEntities.Add(root);

            _startupDisqualifiedTransportEntities =
                _startupReconcileDisqualifiedEntities.Count;
        }

        private void BuildInitialEntityCache()
        {
            _cachedBuildingBlocks.Clear();
            _cachedEstateCandidates.Clear();
            _cachedTransports.Clear();

            int scanned = 0;

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                scanned++;
                CacheNetworkable(networkable);
            }

            _initialWorldEntityCount = scanned;

            Puts(
                $"Initial entity cache built from {scanned} world entities: " +
                $"{_cachedBuildingBlocks.Count} BuildingBlocks, " +
                $"{_cachedEstateCandidates.Count} player-owned estate candidates, " +
                $"{_cachedTransports.Count} supported transports."
            );
        }

        private void CacheNetworkable(BaseNetworkable networkable)
        {
            if (networkable == null)
                return;

            BuildingBlock block = networkable as BuildingBlock;
            if (block != null)
            {
                if (!block.IsDestroyed)
                    _cachedBuildingBlocks.Add(block);

                return;
            }

            BaseEntity entity = networkable as BaseEntity;

            BaseCombatEntity combatEntity = entity as BaseCombatEntity;
            if (combatEntity != null)
            {
                string transportCategory;
                if (TryClassifyProtectedTransport(combatEntity, out transportCategory))
                    _cachedTransports.Add(combatEntity);
            }

            if (ShouldCacheEstateCandidate(entity))
                _cachedEstateCandidates.Add(entity);
        }

        private bool ShouldCacheEstateCandidate(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
                return false;

            if (entity is BuildingPrivlidge || entity is BasePlayer)
                return false;

            // Estate deployables must be player-owned. This excludes monuments,
            // NPCs and most world clutter from the persistent runtime cache.
            if (entity.OwnerID == 0)
                return false;

            if (IsObviouslyTransient(entity))
                return false;

            return true;
        }

        private void PruneEntityCaches()
        {
            _cachedBuildingBlocks.RemoveWhere(block => block == null || block.IsDestroyed);
            _cachedEstateCandidates.RemoveWhere(entity => entity == null || entity.IsDestroyed);
            _cachedTransports.RemoveWhere(entity => entity == null || entity.IsDestroyed);
        }

        private void RefreshEstateMembership()
        {
            PruneEntityCaches();

            EstateSnapshot snapshot = BuildEstateSnapshot();
            RefreshEstateMembership(snapshot);
        }

        private void RefreshEstateMembership(EstateSnapshot snapshot)
        {
            _eligibleEstateCombatEntities.Clear();
            _estateTcByCombatEntity.Clear();

            if (snapshot == null)
                return;

            foreach (KeyValuePair<BuildingPrivlidge, EstateTcSnapshot> pair in snapshot.ByTc)
            {
                BuildingPrivlidge tc = pair.Key;
                EstateTcSnapshot tcSnapshot = pair.Value;

                if (tc == null || tcSnapshot == null)
                    continue;

                foreach (DetachedBuildingSnapshot building in tcSnapshot.Buildings)
                {
                    foreach (BuildingBlock block in building.Blocks)
                    {
                        BaseCombatEntity combat = block as BaseCombatEntity;
                        if (combat == null || combat.IsDestroyed)
                            continue;

                        _eligibleEstateCombatEntities.Add(combat);
                        _estateTcByCombatEntity[combat] = tc;
                    }
                }

                foreach (BaseEntity associated in tcSnapshot.AssociatedEntities)
                {
                    BaseCombatEntity combat = associated as BaseCombatEntity;
                    if (combat == null || combat.IsDestroyed)
                        continue;

                    _eligibleEstateCombatEntities.Add(combat);
                    _estateTcByCombatEntity[combat] = tc;
                }
            }

            // Remove debt records for entities that no longer exist.
            List<BaseCombatEntity> stale = new List<BaseCombatEntity>();
            foreach (BaseCombatEntity entity in _decayRepairDebt.Keys)
            {
                if (entity == null || entity.IsDestroyed)
                    stale.Add(entity);
            }

            foreach (BaseCombatEntity entity in stale)
                _decayRepairDebt.Remove(entity);
        }

        private void ProcessEstateRepair()
        {
            if (_decayRepairDebt.Count == 0)
                return;

            bool changed = false;

            // IMPORTANT: iterate a snapshot. Mutating Dictionary values while directly
            // enumerating the dictionary can invalidate the enumerator on Rust's runtime
            // and stop the repeating repair callback after its first successful heal.
            List<KeyValuePair<BaseCombatEntity, float>> entries =
                new List<KeyValuePair<BaseCombatEntity, float>>(_decayRepairDebt);

            foreach (KeyValuePair<BaseCombatEntity, float> pair in entries)
            {
                BaseCombatEntity entity = pair.Key;
                float debt = pair.Value;

                if (entity == null || entity.IsDestroyed || debt <= 0.01f)
                {
                    if (entity != null)
                    {
                        _decayRepairDebt.Remove(entity);
                        changed = true;
                    }
                    continue;
                }

                BuildingPrivlidge tc;
                if (!_estateTcByCombatEntity.TryGetValue(entity, out tc) || tc == null)
                    continue;

                EstateAccount account;
                if (!_accounts.TryGetValue(tc, out account) || !account.Paid)
                    continue;

                float maxHealth = entity.MaxHealth();
                float currentHealth = entity.Health();
                float missing = Mathf.Max(0f, maxHealth - currentHealth);

                if (missing <= 0.01f)
                {
                    _decayRepairDebt.Remove(entity);
                    changed = true;
                    continue;
                }

                float repairPerTick =
                    maxHealth *
                    RepairFractionOfMaxHealthPerMinute *
                    (RepairTickSeconds / 60f);

                float healAmount = Mathf.Min(debt, Mathf.Min(missing, repairPerTick));

                if (healAmount <= 0.01f)
                    continue;

                entity.Heal(healAmount);

                float remainingDebt = Mathf.Max(0f, debt - healAmount);

                if (remainingDebt <= 0.01f || entity.Health() >= entity.MaxHealth() - 0.01f)
                    {
                        _decayRepairDebt.Remove(entity);
                        changed = true;
                    }
                else
                {
                    _decayRepairDebt[entity] = remainingDebt;
                    changed = true;
                }
            }

            if (changed)
                MarkDataDirty();
        }

        private void ProcessEstateBilling(bool forceSave)
        {
            EstateSnapshot snapshot = BuildEstateSnapshot();
            RefreshEstateMembership(snapshot);

            double now = CurrentUnixSeconds();
            bool importantStateChange = false;
            bool anyStateChange = false;

            HashSet<BuildingPrivlidge> seenTcs = new HashSet<BuildingPrivlidge>();

            foreach (KeyValuePair<BuildingPrivlidge, EstateTcSnapshot> pair in snapshot.ByTc)
            {
                BuildingPrivlidge tc = pair.Key;
                EstateTcSnapshot estate = pair.Value;

                if (tc == null || tc.IsDestroyed)
                    continue;

                seenTcs.Add(tc);

                EstateAccount account = GetOrCreateAccount(tc);

                if (account.LastTick <= 0d)
                {
                    account.LastTick = now;
                    account.Paid = CanStartProtection(tc, estate.Rate);
                    account.LastFailure = account.Paid ? string.Empty : "Required estate upkeep resource is missing.";
                    anyStateChange = true;
                    importantStateChange = true;
                    continue;
                }

                double elapsedSeconds = Math.Max(0d, now - account.LastTick);
                account.LastTick = now;
                anyStateChange = true;

                if (elapsedSeconds <= 0d)
                    continue;

                // If unpaid, protection resumes from "now" once the required resource
                // categories exist again. Unprotected time is intentionally not back-billed.
                if (!account.Paid)
                {
                    if (CanStartProtection(tc, estate.Rate))
                    {
                        account.Paid = true;
                        account.LastFailure = string.Empty;
                        account.DebtWood = 0f;
                        account.DebtStone = 0f;
                        account.DebtMetal = 0f;
                        account.DebtHqm = 0f;
                        importantStateChange = true;
                    }

                    continue;
                }

                float elapsedDayFraction = (float)(elapsedSeconds / SecondsPerDay);

                account.DebtWood += estate.Rate.WoodPerDay * elapsedDayFraction;
                account.DebtStone += estate.Rate.StonePerDay * elapsedDayFraction;
                account.DebtMetal += estate.Rate.MetalPerDay * elapsedDayFraction;
                account.DebtHqm += estate.Rate.HqmPerDay * elapsedDayFraction;

                int dueWood = Mathf.FloorToInt(account.DebtWood);
                int dueStone = Mathf.FloorToInt(account.DebtStone);
                int dueMetal = Mathf.FloorToInt(account.DebtMetal);
                int dueHqm = Mathf.FloorToInt(account.DebtHqm);

                if (dueWood <= 0 && dueStone <= 0 && dueMetal <= 0 && dueHqm <= 0)
                    continue;

                string failure;
                if (TryConsumeAtomic(tc, dueWood, dueStone, dueMetal, dueHqm, out failure))
                {
                    account.DebtWood -= dueWood;
                    account.DebtStone -= dueStone;
                    account.DebtMetal -= dueMetal;
                    account.DebtHqm -= dueHqm;

                    account.Paid = true;
                    account.LastFailure = string.Empty;
                    importantStateChange = true;
                }
                else
                {
                    // No partial payment. This elapsed interval becomes unpaid and is
                    // not queued as a future back-charge.
                    account.DebtWood -= dueWood;
                    account.DebtStone -= dueStone;
                    account.DebtMetal -= dueMetal;
                    account.DebtHqm -= dueHqm;

                    account.Paid = false;
                    account.LastFailure = failure;
                    importantStateChange = true;
                }
            }

            // Remove runtime accounts for TCs that no longer have an eligible estate.
            List<BuildingPrivlidge> stale = new List<BuildingPrivlidge>();

            foreach (BuildingPrivlidge tc in _accounts.Keys)
            {
                if (tc == null || tc.IsDestroyed || !seenTcs.Contains(tc))
                    stale.Add(tc);
            }

            foreach (BuildingPrivlidge tc in stale)
            {
                _accounts.Remove(tc);
                anyStateChange = true;
                importantStateChange = true;
            }

            RebuildProtectedEntitySet(snapshot);

            if (anyStateChange)
                MarkDataDirty();

            if (forceSave || importantStateChange)
                SavePersistentData(true);
        }

        private EstateSnapshot BuildEstateSnapshot()
        {
            double snapshotStarted = CurrentUnixMilliseconds();

            EstateSnapshot snapshot = new EstateSnapshot();
            int blocksScanned = 0;
            int candidatesScanned = 0;

            Dictionary<uint, DetachedBuildingSnapshot> detachedByBuildingId =
                new Dictionary<uint, DetachedBuildingSnapshot>();

            // 1) Group all BuildingBlocks by BuildingID.
            Dictionary<uint, List<BuildingBlock>> allBuildings =
                new Dictionary<uint, List<BuildingBlock>>();

            // PERFORMANCE: cached BuildingBlocks only; no full-world enumeration.
            foreach (BuildingBlock block in _cachedBuildingBlocks)
            {
                blocksScanned++;

                if (block == null || block.IsDestroyed)
                    continue;

                List<BuildingBlock> list;
                if (!allBuildings.TryGetValue(block.buildingID, out list))
                {
                    list = new List<BuildingBlock>();
                    allBuildings[block.buildingID] = list;
                }

                list.Add(block);
            }

            // 2) Determine which physical buildings are detached and eligible.
            foreach (KeyValuePair<uint, List<BuildingBlock>> pair in allBuildings)
            {
                List<BuildingBlock> blocks = pair.Value;
                if (blocks == null || blocks.Count == 0)
                    continue;

                BuildingBlock anchor = blocks[0];
                if (anchor == null || anchor.IsDestroyed)
                    continue;

                // A building with its own TC remains vanilla and is NOT estate-charged.
                if (anchor.GetBuildingPrivilege() != null)
                    continue;

                BuildingPrivlidge spatialTc = GetSpatialPrivilege(anchor);
                if (spatialTc == null || spatialTc.IsDestroyed)
                    continue;

                // Every owned block in the detached physical building must belong to
                // a player authorized on the covering privilege TC.
                bool ownersEligible = true;

                foreach (BuildingBlock buildingBlock in blocks)
                {
                    if (buildingBlock == null || buildingBlock.OwnerID == 0)
                    {
                        ownersEligible = false;
                        break;
                    }

                    if (!IsAuthorized(spatialTc, buildingBlock.OwnerID))
                    {
                        ownersEligible = false;
                        break;
                    }

                    // The whole detached building must still resolve to the same TC.
                    BuildingPrivlidge blockTc = GetSpatialPrivilege(buildingBlock);
                    if (blockTc == null || blockTc.net == null || spatialTc.net == null || blockTc.net.ID != spatialTc.net.ID)
                    {
                        ownersEligible = false;
                        break;
                    }
                }

                if (!ownersEligible)
                    continue;

                DetachedBuildingSnapshot detached = new DetachedBuildingSnapshot();
                detached.BuildingId = pair.Key;
                detached.Tc = spatialTc;
                detached.Blocks.AddRange(blocks);
                detached.OwnerId = anchor.OwnerID;
                detached.BlockRate = CalculateBuildingBlockRate(blocks);

                detachedByBuildingId[pair.Key] = detached;

                EstateTcSnapshot tcSnapshot = GetOrCreateTcSnapshot(snapshot, spatialTc);
                tcSnapshot.Buildings.Add(detached);
                tcSnapshot.BlockCount += blocks.Count;
                AddRate(tcSnapshot.Rate, detached.BlockRate);
            }

            if (detachedByBuildingId.Count == 0)
            {
                RecordSnapshotMetrics(snapshotStarted, blocksScanned, candidatesScanned);
                return snapshot;
            }

            // 3) Associate every eligible non-building entity to exactly ONE nearest
            // detached building, preventing double counting between nearby structures.
            // PERFORMANCE: player-owned cached estate candidates only.
            foreach (BaseEntity candidate in _cachedEstateCandidates)
            {
                candidatesScanned++;

                if (candidate == null || candidate.IsDestroyed)
                    continue;

                if (candidate.OwnerID == 0 || IsObviouslyTransient(candidate))
                    continue;

                DetachedBuildingSnapshot nearestBuilding;
                float nearestDistance;

                if (!FindNearestEligibleDetachedBuilding(
                    candidate,
                    detachedByBuildingId.Values,
                    out nearestBuilding,
                    out nearestDistance))
                {
                    continue;
                }

                // Candidate owner must also be authorized on that estate TC.
                if (!IsAuthorized(nearestBuilding.Tc, candidate.OwnerID))
                    continue;

                BuildingPrivlidge candidateTc = GetSpatialPrivilege(candidate);
                if (candidateTc == null || candidateTc.net == null || nearestBuilding.Tc.net == null)
                    continue;

                if (candidateTc.net.ID != nearestBuilding.Tc.net.ID)
                    continue;

                EstateTcSnapshot tcSnapshot = GetOrCreateTcSnapshot(snapshot, nearestBuilding.Tc);

                tcSnapshot.AssociatedEntities.Add(candidate);

                string prefab = candidate.ShortPrefabName ?? candidate.GetType().Name;
                EntityCategory category = CategorizeEntity(prefab);

                switch (category)
                {
                    case EntityCategory.DoorWindow:
                        tcSnapshot.DoorWindowCount++;
                        nearestBuilding.DoorWindowEntities.Add(candidate);
                        break;

                    case EntityCategory.Battery:
                        tcSnapshot.BatteryCount++;
                        break;

                    case EntityCategory.Solar:
                        tcSnapshot.SolarCount++;
                        break;

                    case EntityCategory.Electrical:
                        tcSnapshot.OtherElectricalCount++;
                        break;

                    default:
                        tcSnapshot.OtherEntityCount++;
                        break;
                }
            }

            // 4) Add door/window upkeep to each detached building using the same
            // building tax rate unless the server explicitly uses separate door brackets.
            foreach (EstateTcSnapshot tcSnapshot in snapshot.ByTc.Values)
            {
                foreach (DetachedBuildingSnapshot building in tcSnapshot.Buildings)
                {
                    ResourceRate doorRate = CalculateDoorWindowRate(
                        building.DoorWindowEntities,
                        building.BlockRate.TaxRate
                    );

                    building.DoorRate = doorRate;
                    AddRate(tcSnapshot.Rate, doorRate);
                }
            }

            RecordSnapshotMetrics(snapshotStarted, blocksScanned, candidatesScanned);
            return snapshot;
        }

        private bool FindNearestEligibleDetachedBuilding(
            BaseEntity entity,
            ICollection<DetachedBuildingSnapshot> buildings,
            out DetachedBuildingSnapshot nearestBuilding,
            out float nearestDistance)
        {
            nearestBuilding = null;
            nearestDistance = float.MaxValue;

            foreach (DetachedBuildingSnapshot building in buildings)
            {
                if (building == null)
                    continue;

                foreach (BuildingBlock block in building.Blocks)
                {
                    if (block == null || block.IsDestroyed)
                        continue;

                    float distance = Vector3.Distance(entity.transform.position, block.transform.position);

                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestBuilding = building;
                    }
                }
            }

            if (nearestBuilding == null || nearestDistance > DeployableAssociationRadius)
            {
                nearestBuilding = null;
                return false;
            }

            return true;
        }

        private ResourceRate CalculateBuildingBlockRate(List<BuildingBlock> blocks)
        {
            ResourceRate result = new ResourceRate();

            if (blocks == null || blocks.Count == 0)
                return result;

            float rawWood = 0f;
            float rawStone = 0f;
            float rawMetal = 0f;
            float rawHqm = 0f;

            foreach (BuildingBlock block in blocks)
            {
                if (block == null || block.blockDefinition == null)
                    continue;

                int gradeIndex = (int)block.grade;
                if (gradeIndex < 0 || gradeIndex >= block.blockDefinition.grades.Length)
                    continue;

                ConstructionGrade constructionGrade = block.blockDefinition.grades[gradeIndex];
                if (constructionGrade == null)
                    continue;

                List<ItemAmount> costs = constructionGrade.CostToBuild();
                AddStandardResourceCosts(costs, ref rawWood, ref rawStone, ref rawMetal, ref rawHqm);
            }

            result.TaxRate = GetVanillaTaxRate(blocks.Count);

            float dailyScale = MinutesPerDay / GetUpkeepPeriodMinutes();

            result.WoodPerDay = rawWood * result.TaxRate * dailyScale;
            result.StonePerDay = rawStone * result.TaxRate * dailyScale;
            result.MetalPerDay = rawMetal * result.TaxRate * dailyScale;
            result.HqmPerDay = rawHqm * result.TaxRate * dailyScale;

            return result;
        }

        private ResourceRate CalculateDoorWindowRate(List<BaseEntity> entities, float buildingTaxRate)
        {
            ResourceRate result = new ResourceRate();

            if (entities == null || entities.Count == 0)
                return result;

            float rawWood = 0f;
            float rawStone = 0f;
            float rawMetal = 0f;
            float rawHqm = 0f;
            int mapped = 0;

            foreach (BaseEntity entity in entities)
            {
                if (entity == null)
                    continue;

                ItemDefinition itemDefinition;
                if (!_deployableItemByPrefab.TryGetValue(entity.ShortPrefabName, out itemDefinition) || itemDefinition == null)
                    continue;

                ItemBlueprint blueprint;
                if (!_blueprintByItemId.TryGetValue(itemDefinition.itemid, out blueprint) ||
                    blueprint == null ||
                    blueprint.ingredients == null)
                {
                    continue;
                }

                mapped++;
                AddStandardResourceCosts(
                    blueprint.ingredients,
                    ref rawWood,
                    ref rawStone,
                    ref rawMetal,
                    ref rawHqm
                );
            }

            if (mapped <= 0)
                return result;

            result.TaxRate = ConVar.Decay.use_door_upkeep_brackets
                ? GetVanillaTaxRate(mapped)
                : buildingTaxRate;

            float dailyScale = MinutesPerDay / GetUpkeepPeriodMinutes();

            result.WoodPerDay = rawWood * result.TaxRate * dailyScale;
            result.StonePerDay = rawStone * result.TaxRate * dailyScale;
            result.MetalPerDay = rawMetal * result.TaxRate * dailyScale;
            result.HqmPerDay = rawHqm * result.TaxRate * dailyScale;

            return result;
        }

        private void RebuildProtectedEntitySet(EstateSnapshot snapshot)
        {
            _protectedEntities.Clear();

            foreach (KeyValuePair<BuildingPrivlidge, EstateTcSnapshot> pair in snapshot.ByTc)
            {
                EstateAccount account;
                if (!_accounts.TryGetValue(pair.Key, out account) || !account.Paid)
                    continue;

                EstateTcSnapshot tcSnapshot = pair.Value;

                foreach (DetachedBuildingSnapshot building in tcSnapshot.Buildings)
                {
                    foreach (BuildingBlock block in building.Blocks)
                    {
                        if (block != null && !block.IsDestroyed)
                            _protectedEntities.Add(block);
                    }
                }

                foreach (BaseEntity entity in tcSnapshot.AssociatedEntities)
                {
                    if (entity != null && !entity.IsDestroyed)
                        _protectedEntities.Add(entity);
                }
            }
        }

        private bool CanStartProtection(BuildingPrivlidge tc, ResourceRate rate)
        {
            if (tc == null || tc.inventory == null || rate == null)
                return false;

            if (rate.WoodPerDay > 0f && GetResourceAmount(tc.inventory, _woodItemId) <= 0)
                return false;

            if (rate.StonePerDay > 0f && GetResourceAmount(tc.inventory, _stoneItemId) <= 0)
                return false;

            if (rate.MetalPerDay > 0f && GetResourceAmount(tc.inventory, _metalItemId) <= 0)
                return false;

            if (rate.HqmPerDay > 0f && GetResourceAmount(tc.inventory, _hqmItemId) <= 0)
                return false;

            return true;
        }

        private bool TryConsumeAtomic(
            BuildingPrivlidge tc,
            int wood,
            int stone,
            int metal,
            int hqm,
            out string failure)
        {
            failure = string.Empty;

            if (tc == null || tc.inventory == null)
            {
                failure = "TC inventory not available.";
                return false;
            }

            if (wood > 0 && GetResourceAmount(tc.inventory, _woodItemId) < wood)
            {
                failure = $"Not enough Wood ({wood} required).";
                return false;
            }

            if (stone > 0 && GetResourceAmount(tc.inventory, _stoneItemId) < stone)
            {
                failure = $"Not enough Stone ({stone} required).";
                return false;
            }

            if (metal > 0 && GetResourceAmount(tc.inventory, _metalItemId) < metal)
            {
                failure = $"Not enough Metal Fragments ({metal} required).";
                return false;
            }

            if (hqm > 0 && GetResourceAmount(tc.inventory, _hqmItemId) < hqm)
            {
                failure = $"Not enough HQM ({hqm} required).";
                return false;
            }

            // Atomic after pre-check: no category is consumed unless all categories exist.
            if (wood > 0) ConsumeResource(tc.inventory, _woodItemId, wood);
            if (stone > 0) ConsumeResource(tc.inventory, _stoneItemId, stone);
            if (metal > 0) ConsumeResource(tc.inventory, _metalItemId, metal);
            if (hqm > 0) ConsumeResource(tc.inventory, _hqmItemId, hqm);

            tc.SendNetworkUpdate();
            return true;
        }

        private int GetResourceAmount(ItemContainer inventory, int itemId)
        {
            if (inventory == null || itemId == 0)
                return 0;

            int total = 0;

            foreach (Item item in inventory.itemList)
            {
                if (item == null || item.info == null || item.info.itemid != itemId)
                    continue;

                total += item.amount;
            }

            return total;
        }

        private void ConsumeResource(ItemContainer inventory, int itemId, int amount)
        {
            if (inventory == null || itemId == 0 || amount <= 0)
                return;

            int remaining = amount;
            List<Item> items = new List<Item>(inventory.itemList);

            foreach (Item item in items)
            {
                if (remaining <= 0)
                    break;

                if (item == null || item.info == null || item.info.itemid != itemId)
                    continue;

                int take = Math.Min(item.amount, remaining);
                item.UseItem(take);
                remaining -= take;
            }
        }

        private EstateAccount GetOrCreateAccount(BuildingPrivlidge tc)
        {
            EstateAccount account;

            if (!_accounts.TryGetValue(tc, out account))
            {
                account = new EstateAccount();
                account.LastTick = 0d;
                _accounts[tc] = account;
            }

            return account;
        }

        private EstateTcSnapshot GetOrCreateTcSnapshot(EstateSnapshot snapshot, BuildingPrivlidge tc)
        {
            EstateTcSnapshot tcSnapshot;

            if (!snapshot.ByTc.TryGetValue(tc, out tcSnapshot))
            {
                tcSnapshot = new EstateTcSnapshot();
                tcSnapshot.Tc = tc;
                snapshot.ByTc[tc] = tcSnapshot;
            }

            return tcSnapshot;
        }

        private BuildingBlock GetLookedAtBuildingBlock(BasePlayer player)
        {
            RaycastHit hit;

            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, RayDistance))
                return null;

            BaseEntity entity = hit.GetEntity();
            return entity as BuildingBlock;
        }

        private BaseCombatEntity GetLookedAtCombatEntity(BasePlayer player)
        {
            RaycastHit hit;

            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, RayDistance))
                return null;

            BaseEntity entity = hit.GetEntity();
            return entity as BaseCombatEntity;
        }

        private BuildingPrivlidge GetSpatialPrivilege(BaseEntity entity)
        {
            if (entity == null)
                return null;

            OBB obb = new OBB(entity.transform.position, entity.transform.rotation, entity.bounds);
            return entity.GetBuildingPrivilege(obb, false);
        }

        private bool TryResolveProtectedTransport(
            BaseCombatEntity entity,
            out BaseCombatEntity transportRoot,
            out string category
        )
        {
            transportRoot = null;
            category = string.Empty;

            if (entity == null || entity.IsDestroyed)
                return false;

            // IMPORTANT:
            // Some modular-car modules can themselves satisfy the broad transport
            // classifier (for example through BaseVehicle inheritance). Returning
            // the first classified entity would therefore treat a child module as
            // a separate vehicle.
            //
            // Walk the complete transform ancestry and keep the HIGHEST classified
            // transport entity. A standalone vehicle still resolves to itself,
            // while 1module_* children resolve to the parent ModularCar root.
            Transform cursor = entity.transform;
            int depth = 0;

            BaseCombatEntity best = null;
            string bestCategory = string.Empty;

            while (cursor != null && depth++ < 24)
            {
                BaseCombatEntity candidate =
                    cursor.GetComponent<BaseCombatEntity>();

                string candidateCategory;

                if (
                    candidate != null &&
                    !candidate.IsDestroyed &&
                    TryClassifyProtectedTransport(
                        candidate,
                        out candidateCategory
                    )
                )
                {
                    best = candidate;
                    bestCategory = candidateCategory;
                }

                cursor = cursor.parent;
            }

            if (best == null)
                return false;

            transportRoot = best;
            category = bestCategory;
            return true;
        }

        private bool IsTransformAncestor(Transform possibleAncestor, Transform child)
        {
            if (possibleAncestor == null || child == null)
                return false;

            Transform cursor = child.parent;
            int depth = 0;

            while (cursor != null && depth++ < 16)
            {
                if (cursor == possibleAncestor)
                    return true;

                cursor = cursor.parent;
            }

            return false;
        }

        private List<BaseCombatEntity> GetTransportHealthEntities(BaseCombatEntity transportRoot)
        {
            List<BaseCombatEntity> result = new List<BaseCombatEntity>();

            if (transportRoot == null || transportRoot.IsDestroyed)
                return result;

            HashSet<BaseCombatEntity> seen = new HashSet<BaseCombatEntity>();
            BaseCombatEntity[] components =
                transportRoot.GetComponentsInChildren<BaseCombatEntity>(true);

            if (components != null)
            {
                foreach (BaseCombatEntity entity in components)
                {
                    if (entity == null || entity.IsDestroyed || !seen.Add(entity))
                        continue;

                    result.Add(entity);
                }
            }

            if (seen.Add(transportRoot))
                result.Add(transportRoot);

            return result;
        }

        private void RefreshRuntimeProtectedTransportCache()
        {
            _lastProtectedTransportRefreshScanned = 0;
            _lastProtectedTransportRefreshFound = 0;

            if (
                _config == null ||
                _config.VehicleProtection == null ||
                !_config.VehicleProtection.Enabled ||
                !_config.VehicleProtection.PreventDecayInsideTc
            )
            {
                _runtimeProtectedTransports.Clear();
                return;
            }

            HashSet<BaseCombatEntity> next =
                new HashSet<BaseCombatEntity>();

            foreach (BaseCombatEntity root in _cachedTransports)
            {
                if (root == null || root.IsDestroyed)
                    continue;

                _lastProtectedTransportRefreshScanned++;

                string category;
                if (
                    !TryClassifyProtectedTransport(root, out category) ||
                    !IsTransportCategoryEnabled(category)
                )
                    continue;

                BuildingPrivlidge tc = GetSpatialPrivilege(root);
                if (tc == null)
                    continue;

                next.Add(root);
            }

            _runtimeProtectedTransports.Clear();

            foreach (BaseCombatEntity root in next)
                _runtimeProtectedTransports.Add(root);

            _lastProtectedTransportRefreshFound =
                _runtimeProtectedTransports.Count;
        }

        private string GetCurrentServerBootId()
        {
            try
            {
                System.Diagnostics.Process process =
                    System.Diagnostics.Process.GetCurrentProcess();

                long startTicks = 0;

                try
                {
                    startTicks = process.StartTime.ToUniversalTime().Ticks;
                }
                catch
                {
                    // Process ID alone still distinguishes a hot plugin reload from
                    // the current Rust process in normal operation.
                }

                return process.Id.ToString() + ":" + startTicks.ToString();
            }
            catch (Exception ex)
            {
                PrintWarning(
                    $"Could not determine Rust process boot identity: {ex.Message}"
                );
                return string.Empty;
            }
        }

        private long GetUnixTimeSeconds()
        {
            return System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private void RefreshLiveTransportRestartCheckpoint(string source)
        {
            if (_storedData == null)
                _storedData = new StoredData();

            if (GetUnixTimeSeconds() < _transportCheckpointFrozenUntilUnix)
                return;

            if (
                _config == null ||
                _config.VehicleProtection == null ||
                !_config.VehicleProtection.Enabled ||
                !_config.VehicleProtection.PreventDecayInsideTc ||
                !_config.VehicleProtection.RestoreProtectedHpAfterCleanRestart
            )
                return;

            Dictionary<string, StoredTransportShutdownState> next =
                new Dictionary<string, StoredTransportShutdownState>(StringComparer.Ordinal);

            _lastRestartCheckpointRootCount = 0;
            _lastRestartCheckpointRecordCount = 0;

            HashSet<BaseCombatEntity> capturedEntities =
                new HashSet<BaseCombatEntity>();

            foreach (BaseCombatEntity root in _runtimeProtectedTransports)
            {
                if (root == null || root.IsDestroyed)
                    continue;

                string category;
                if (
                    !TryClassifyProtectedTransport(root, out category) ||
                    !IsTransportCategoryEnabled(category)
                )
                    continue;

                _lastRestartCheckpointRootCount++;

                foreach (BaseCombatEntity entity in GetTransportHealthEntities(root))
                {
                    if (
                        entity == null ||
                        entity.IsDestroyed ||
                        entity.net == null ||
                        !capturedEntities.Add(entity)
                    )
                        continue;

                    string key = entity.net.ID.ToString();

                    StoredTransportShutdownState stored =
                        new StoredTransportShutdownState();

                    stored.NetId = key;
                    stored.OwnerId = entity.OwnerID;
                    stored.Prefab = entity.ShortPrefabName ?? string.Empty;
                    stored.Position = StoredPosition.FromVector3(entity.transform.position);
                    stored.Health = Mathf.Max(0f, entity.Health());
                    stored.MaxHealth = Mathf.Max(0f, entity.MaxHealth());
                    stored.Category = category;

                    next[key] = stored;
                }
            }

            // Do not replace a useful checkpoint with an empty transient snapshot.
            if (next.Count == 0)
                return;

            _storedData.TransportShutdown = next;
            _storedData.TransportCheckpointCapturedUnix = GetUnixTimeSeconds();
            _storedData.TransportCheckpointSource = source ?? string.Empty;
            _storedData.TransportCheckpointBootId = _currentServerBootId ?? string.Empty;

            _lastRestartCheckpointRecordCount = next.Count;

            MarkDataDirty();
        }

        private void RestoreTransportAfterServerRestart()
        {
            _startupTransportRestoreEntities = 0;
            _startupTransportRestoreHp = 0f;
            _startupTransportSnapshotRecords = 0;
            _startupTransportMatchedRecords = 0;
            _startupTransportUnmatchedRecords = 0;
            _startupRestartCheckpointAccepted = false;
            _startupRestartCheckpointAgeAtShutdownSeconds = -1;
            _startupRestartCheckpointSource = string.Empty;
            _startupTransportReconcilePasses = 0;
            _startupTransportDelayedRestoreHp = 0f;
            _startupTransportDelayedRestoreEntities = 0;
            _startupRestartCandidateRoots = 0;
            _startupRestartCandidateHealthEntities = 0;
            _startupSpawnTriggeredReconcilePasses = 0;
            _startupUnsourcedTransportDamageEvents = 0;
            _startupSelfSourcedTransportDamageEvents = 0;
            _startupExternalSourcedTransportDamageEvents = 0;
            _startupSourcedTransportDamageDisqualifications = 0;
            _startupDisqualifiedTransportEntities = 0;
            _lastStartupDamageDiagnostic = "NONE";
            _startupSpawnReconcileQueued = false;
            _startupTransportReconcileActive = false;
            _startupPendingTransportSnapshot.Clear();
            _startupReconcileDisqualifiedEntities.Clear();

            _startupRestartDetectionMode = "NONE";
            _startupCheckpointBootMatchesCurrent = false;
            _startupRestartCheckpointAgeAtBootSeconds = -1;

            if (_storedData == null)
                return;

            _startupRestartCheckpointSource =
                _storedData.TransportCheckpointSource ?? string.Empty;

            string checkpointBootId =
                _storedData.TransportCheckpointBootId ?? string.Empty;

            bool haveCurrentBootId =
                !string.IsNullOrEmpty(_currentServerBootId);

            bool haveCheckpointBootId =
                !string.IsNullOrEmpty(checkpointBootId);

            _startupCheckpointBootMatchesCurrent =
                haveCurrentBootId &&
                haveCheckpointBootId &&
                string.Equals(
                    checkpointBootId,
                    _currentServerBootId,
                    StringComparison.Ordinal
                );

            if (_storedData.TransportCheckpointCapturedUnix > 0)
            {
                _startupRestartCheckpointAgeAtBootSeconds =
                    System.Math.Max(
                        0L,
                        GetUnixTimeSeconds() -
                        _storedData.TransportCheckpointCapturedUnix
                    );
            }

            // Same Rust OS process means c.reload/hot reload, not server restart.
            // Keep the checkpoint untouched for the next real process restart.
            if (_startupCheckpointBootMatchesCurrent)
            {
                _startupRestartDetectionMode = "SAME_PROCESS_RELOAD";
                Puts(
                    "Same Rust process detected; transport restart restore skipped. " +
                    "Checkpoint preserved for a real server restart."
                );
                return;
            }

            bool differentProcessBoot =
                haveCurrentBootId &&
                haveCheckpointBootId &&
                !_startupCheckpointBootMatchesCurrent;

            bool legacyCleanShutdownFallback =
                !haveCheckpointBootId &&
                _storedData.CleanShutdownPending;

            if (differentProcessBoot)
            {
                _startupRestartDetectionMode = "NEW_PROCESS";
            }
            else if (legacyCleanShutdownFallback)
            {
                _startupRestartDetectionMode = "LEGACY_CLEAN_MARKER";
            }
            else
            {
                _startupRestartDetectionMode = "NO_RESTART_EVIDENCE";
                Puts(
                    "No reliable server-restart evidence found; transport restore skipped."
                );
                return;
            }

            // The old hook marker is now only a migration/best-effort signal.
            _storedData.CleanShutdownPending = false;

            if (
                _storedData.TransportShutdown == null ||
                _storedData.TransportShutdown.Count == 0
            )
            {
                MarkDataDirty();
                SavePersistentData(true);
                Puts(
                    "Server restart detected, but no transport checkpoint records were available."
                );
                return;
            }

            _startupTransportSnapshotRecords =
                _storedData.TransportShutdown.Count;

            bool checkpointFresh;

            if (differentProcessBoot)
            {
                checkpointFresh =
                    _startupRestartCheckpointAgeAtBootSeconds >= 0 &&
                    _startupRestartCheckpointAgeAtBootSeconds <=
                        (long)RestartCheckpointMaxBootAgeSeconds;
            }
            else
            {
                // Migration path for <= v0.6.11 data.
                if (
                    _storedData.TransportCheckpointCapturedUnix > 0 &&
                    _storedData.CleanShutdownMarkedUnix > 0
                )
                {
                    _startupRestartCheckpointAgeAtShutdownSeconds =
                        System.Math.Max(
                            0L,
                            _storedData.CleanShutdownMarkedUnix -
                            _storedData.TransportCheckpointCapturedUnix
                        );
                }

                checkpointFresh =
                    _startupRestartCheckpointAgeAtShutdownSeconds >= 0 &&
                    _startupRestartCheckpointAgeAtShutdownSeconds <=
                        (long)_config.VehicleProtection.RestartCheckpointMaxAgeAtShutdownSeconds;
            }

            if (!checkpointFresh)
            {
                _startupTransportUnmatchedRecords =
                    _startupTransportSnapshotRecords;

                _storedData.TransportShutdown =
                    new Dictionary<string, StoredTransportShutdownState>(
                        StringComparer.Ordinal
                    );

                MarkDataDirty();
                SavePersistentData(true);

                Puts(
                    $"Transport restart checkpoint rejected as stale. " +
                    $"mode={_startupRestartDetectionMode}, " +
                    $"ageAtBoot={_startupRestartCheckpointAgeAtBootSeconds}s, " +
                    $"maxBootAge={RestartCheckpointMaxBootAgeSeconds:0}s."
                );
                return;
            }

            if (
                _config == null ||
                _config.VehicleProtection == null ||
                !_config.VehicleProtection.Enabled ||
                !_config.VehicleProtection.PreventDecayInsideTc ||
                !_config.VehicleProtection.RestoreProtectedHpAfterCleanRestart
            )
            {
                _startupTransportUnmatchedRecords = _startupTransportSnapshotRecords;
                _storedData.TransportShutdown =
                    new Dictionary<string, StoredTransportShutdownState>(StringComparer.Ordinal);

                MarkDataDirty();
                SavePersistentData(true);
                return;
            }

            _startupRestartCheckpointAccepted = true;

            _startupPendingTransportSnapshot =
                new Dictionary<string, StoredTransportShutdownState>(
                    _storedData.TransportShutdown,
                    StringComparer.Ordinal
                );

            // Remove persisted copy immediately to prevent replay on plugin reload.
            // The accepted snapshot now lives only in memory for the short startup window.
            _storedData.TransportShutdown =
                new Dictionary<string, StoredTransportShutdownState>(StringComparer.Ordinal);
            _storedData.TransportCheckpointBootId = string.Empty;

            MarkDataDirty();
            SavePersistentData(true);

            _startupTransportReconcileActive = true;

            ReconcileStartupTransportCheckpoint(false);

            timer.Once(StartupTransportReconcileFirstDelaySeconds, () =>
            {
                if (_startupTransportReconcileActive)
                    ReconcileStartupTransportCheckpoint(true);
            });

            timer.Once(StartupTransportReconcileSecondDelaySeconds, () =>
            {
                if (_startupTransportReconcileActive)
                    ReconcileStartupTransportCheckpoint(true);
            });

            timer.Once(StartupTransportReconcileThirdDelaySeconds, () =>
            {
                if (_startupTransportReconcileActive)
                    ReconcileStartupTransportCheckpoint(true);
            });

            timer.Once(StartupTransportReconcileFinalDelaySeconds, () =>
            {
                if (!_startupTransportReconcileActive)
                    return;

                ReconcileStartupTransportCheckpoint(true);
                FinishStartupTransportReconciliation();
            });
        }

        private void ReconcileStartupTransportCheckpoint(bool delayedPass)
        {
            if (
                !_startupTransportReconcileActive ||
                _startupPendingTransportSnapshot == null ||
                _startupPendingTransportSnapshot.Count == 0
            )
                return;

            _startupTransportReconcilePasses++;

            List<LiveTransportHealthCandidate> liveCandidates =
                BuildStartupRestartTransportHealthCandidates();

            Dictionary<string, LiveTransportHealthCandidate> liveByNetId =
                new Dictionary<string, LiveTransportHealthCandidate>(StringComparer.Ordinal);

            foreach (LiveTransportHealthCandidate candidate in liveCandidates)
            {
                if (
                    candidate == null ||
                    candidate.Entity == null ||
                    candidate.Entity.IsDestroyed ||
                    candidate.Entity.net == null ||
                    _startupReconcileDisqualifiedEntities.Contains(candidate.Entity) ||
                    (candidate.Root != null &&
                     _startupReconcileDisqualifiedEntities.Contains(candidate.Root))
                )
                    continue;

                liveByNetId[candidate.Entity.net.ID.ToString()] = candidate;
            }

            HashSet<BaseCombatEntity> usedEntities =
                new HashSet<BaseCombatEntity>();

            int matchedThisPass = 0;
            int unmatchedThisPass = 0;

            foreach (KeyValuePair<string, StoredTransportShutdownState> pair in
                _startupPendingTransportSnapshot)
            {
                StoredTransportShutdownState stored = pair.Value;
                LiveTransportHealthCandidate matched = null;

                LiveTransportHealthCandidate byNetId;
                if (
                    stored != null &&
                    !string.IsNullOrEmpty(stored.NetId) &&
                    liveByNetId.TryGetValue(stored.NetId, out byNetId) &&
                    byNetId != null &&
                    byNetId.Entity != null &&
                    !usedEntities.Contains(byNetId.Entity) &&
                    StoredTransportShutdownMatches(stored, byNetId)
                )
                {
                    matched = byNetId;
                }

                if (matched == null)
                {
                    float bestDistance = float.MaxValue;

                    foreach (LiveTransportHealthCandidate candidate in liveCandidates)
                    {
                        if (
                            candidate == null ||
                            candidate.Entity == null ||
                            candidate.Entity.IsDestroyed ||
                            usedEntities.Contains(candidate.Entity) ||
                            _startupReconcileDisqualifiedEntities.Contains(candidate.Entity) ||
                            (candidate.Root != null &&
                             _startupReconcileDisqualifiedEntities.Contains(candidate.Root)) ||
                            !StoredTransportShutdownMatches(stored, candidate)
                        )
                            continue;

                        float distance =
                            Vector3.Distance(
                                stored.Position.ToVector3(),
                                candidate.Entity.transform.position
                            );

                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            matched = candidate;
                        }
                    }
                }

                if (matched == null || matched.Entity == null)
                {
                    unmatchedThisPass++;
                    continue;
                }

                usedEntities.Add(matched.Entity);
                matchedThisPass++;

                BaseCombatEntity entity = matched.Entity;

                float current = Mathf.Max(0f, entity.Health());
                float target = Mathf.Min(
                    Mathf.Max(0f, stored.Health),
                    entity.MaxHealth()
                );

                if (target <= current + 0.01f)
                    continue;

                float restoreAmount = target - current;
                entity.Heal(restoreAmount);

                _startupTransportRestoreEntities++;
                _startupTransportRestoreHp += restoreAmount;

                if (delayedPass)
                {
                    _startupTransportDelayedRestoreEntities++;
                    _startupTransportDelayedRestoreHp += restoreAmount;
                }
            }

            _startupTransportMatchedRecords = matchedThisPass;
            _startupTransportUnmatchedRecords = unmatchedThisPass;

            Puts(
                $"Startup transport reconcile pass {_startupTransportReconcilePasses}: " +
                $"candidates={_startupRestartCandidateHealthEntities} from " +
                $"{_startupRestartCandidateRoots} transport root(s); " +
                $"matched={matchedThisPass}, unmatched={unmatchedThisPass}, " +
                $"totalRestored={_startupTransportRestoreHp:0.##} HP."
            );
        }

        private void FinishStartupTransportReconciliation()
        {
            _startupTransportReconcileActive = false;
            _startupSpawnReconcileQueued = false;
            _startupPendingTransportSnapshot.Clear();
            _startupReconcileDisqualifiedEntities.Clear();

            Puts(
                $"Startup transport reconciliation finished after " +
                $"{_startupTransportReconcilePasses} pass(es), including " +
                $"{_startupSpawnTriggeredReconcilePasses} spawn-triggered pass(es): restored " +
                $"{_startupTransportRestoreHp:0.##} HP total; delayed=" +
                $"{_startupTransportDelayedRestoreHp:0.##} HP; " +
                $"last matched={_startupTransportMatchedRecords}, " +
                $"last unmatched={_startupTransportUnmatchedRecords}; " +
                $"unsourcedStartupDamage={_startupUnsourcedTransportDamageEvents}, " +
                $"selfSourcedStartupDamage={_startupSelfSourcedTransportDamageEvents}, " +
                $"externalSourcedStartupDamage={_startupExternalSourcedTransportDamageEvents}, " +
                $"sourcedDisqualifications={_startupSourcedTransportDamageDisqualifications}; " +
                $"lastDamage={_lastStartupDamageDiagnostic}."
            );

            if (_startupTransportUnmatchedRecords > 0)
            {
                Puts(
                    "Some restart snapshot records remained unmatched after the full " +
                    "90s startup window. Use /estateperf and server console diagnostics " +
                    "before changing matcher strictness."
                );
            }
        }

        private List<LiveTransportHealthCandidate> BuildStartupRestartTransportHealthCandidates()
        {
            List<LiveTransportHealthCandidate> result =
                new List<LiveTransportHealthCandidate>();

            HashSet<BaseCombatEntity> seen =
                new HashSet<BaseCombatEntity>();

            int roots = 0;

            // Deliberately DO NOT call GetSpatialPrivilege() here.
            //
            // During early server startup, Rust/Carbon can expose the transport entity
            // before TC privilege queries are fully ready. Live testing showed this
            // specifically drops all modular-car health records from restart matching.
            //
            // Safety comes from the accepted pre-shutdown checkpoint plus the strict
            // matcher below: prefab, meaningful owner, max HP, category and position.
            foreach (BaseCombatEntity root in _cachedTransports)
            {
                if (root == null || root.IsDestroyed)
                    continue;

                string category;
                if (
                    !TryClassifyProtectedTransport(root, out category) ||
                    !IsTransportCategoryEnabled(category)
                )
                    continue;

                roots++;

                foreach (BaseCombatEntity entity in GetTransportHealthEntities(root))
                {
                    if (
                        entity == null ||
                        entity.IsDestroyed ||
                        !seen.Add(entity)
                    )
                        continue;

                    LiveTransportHealthCandidate candidate =
                        new LiveTransportHealthCandidate();

                    candidate.Entity = entity;
                    candidate.Root = root;
                    candidate.Category = category;

                    result.Add(candidate);
                }
            }

            _startupRestartCandidateRoots = roots;
            _startupRestartCandidateHealthEntities = result.Count;

            return result;
        }

        private bool StoredTransportShutdownMatches(
            StoredTransportShutdownState stored,
            LiveTransportHealthCandidate candidate
        )
        {
            if (
                stored == null ||
                candidate == null ||
                candidate.Entity == null ||
                candidate.Entity.IsDestroyed ||
                stored.Position == null
            )
                return false;

            BaseCombatEntity entity = candidate.Entity;

            if (
                !string.IsNullOrEmpty(stored.Prefab) &&
                !string.Equals(
                    stored.Prefab,
                    entity.ShortPrefabName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return false;

            // OwnerID is useful when both sides have a meaningful owner. Some Rust
            // transport/module entities legitimately use OwnerID 0, so zero must not
            // make restart matching fail.
            if (
                stored.OwnerId != 0 &&
                entity.OwnerID != 0 &&
                stored.OwnerId != entity.OwnerID
            )
                return false;

            if (
                stored.MaxHealth > 0f &&
                Mathf.Abs(stored.MaxHealth - entity.MaxHealth()) > 1f
            )
                return false;

            if (
                !string.IsNullOrEmpty(stored.Category) &&
                !string.Equals(
                    stored.Category,
                    candidate.Category,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return false;

            // Vehicles can settle slightly as the world is loaded. We allow a small
            // restart-only positional tolerance, then choose the nearest unused match.
            float distance =
                Vector3.Distance(
                    stored.Position.ToVector3(),
                    entity.transform.position
                );

            return distance <= _config.VehicleProtection.RestartMatchRadiusMeters;
        }

        private BaseCombatEntity FindNearbySupportedTransport(Vector3 origin, float radius)
        {
            BaseCombatEntity best = null;
            float bestDistance = radius;

            foreach (BaseCombatEntity candidate in _cachedTransports)
            {
                if (candidate == null || candidate.IsDestroyed)
                    continue;

                string category;
                if (!TryClassifyProtectedTransport(candidate, out category))
                    continue;

                float distance = Vector3.Distance(origin, candidate.transform.position);

                if (distance > bestDistance)
                    continue;

                best = candidate;
                bestDistance = distance;
            }

            return best;
        }

        private bool IsTransportInfrastructure(BaseCombatEntity entity, string prefab, string typeName)
        {
            if (entity == null)
                return true;

            // Important false-positive guard:
            // electrical.modularcarlift.deployed has runtime type ModularCarGarage.
            // It contains "modularcar" in its identity but is infrastructure, not transport.
            if (
                ContainsAny(prefab,
                    "modularcarlift",
                    "modular_car_lift",
                    "carlift",
                    "vehiclelift",
                    "vehicle_lift"
                ) ||
                ContainsAny(typeName,
                    "modularcargarage",
                    "modular_car_garage",
                    "carlift",
                    "vehiclelift"
                )
            )
            {
                return true;
            }

            return false;
        }

        private bool TryClassifyProtectedTransport(BaseCombatEntity entity, out string category)
        {
            category = string.Empty;

            if (entity == null || entity.IsDestroyed)
                return false;

            string prefab = (entity.ShortPrefabName ?? string.Empty).ToLowerInvariant();
            string typeName = entity.GetType().Name.ToLowerInvariant();

            if (IsTransportInfrastructure(entity, prefab, typeName))
                return false;

            // WATER FIRST: Carbon exposes BaseBoat separately, and some watercraft may
            // not share the same inheritance chain as cars.
            if (
                entity is BaseBoat ||
                ContainsAny(prefab, "rowboat", "rhib", "tugboat", "submarine", "kayak") ||
                ContainsAny(typeName, "baseboat", "rowboat", "rhib", "tugboat", "submarine", "kayak")
            )
            {
                category = "Water";
                return true;
            }

            // AIR: use stable runtime/prefab identity instead of assuming every
            // player aircraft is derived from the same BaseVehicle class.
            if (
                entity is HotAirBalloon ||
                ContainsAny(prefab, "minicopter", "scraptransporthelicopter", "attackhelicopter", "hotairballoon") ||
                ContainsAny(typeName, "minicopter", "scraptransporthelicopter", "attackhelicopter", "hotairballoon")
            )
            {
                category = "Air";
                return true;
            }

            // LAND: BaseVehicle catches modular cars and many standard Rust vehicles.
            // Extra names cover transport that may use a specialized runtime class.
            if (
                entity is BaseVehicle ||
                ContainsAny(prefab, "modularcar", "snowmobile", "motorbike", "motorcycle", "pedalbike", "bicycle") ||
                ContainsAny(typeName, "modularcar", "snowmobile", "motorbike", "motorcycle", "pedalbike", "bicycle")
            )
            {
                category = "Land";
                return true;
            }

            return false;
        }

        private bool IsTransportCategoryEnabled(string category)
        {
            if (_config == null || _config.VehicleProtection == null)
                return false;

            switch (category)
            {
                case "Land":
                    return _config.VehicleProtection.ProtectLand;

                case "Water":
                    return _config.VehicleProtection.ProtectWater;

                case "Air":
                    return _config.VehicleProtection.ProtectAir;

                default:
                    return false;
            }
        }

        private bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrEmpty(value) || needles == null)
                return false;

            foreach (string needle in needles)
            {
                if (!string.IsNullOrEmpty(needle) && value.Contains(needle))
                    return true;
            }

            return false;
        }

        private EntityCategory CategorizeEntity(string prefabName)
        {
            string prefab = (prefabName ?? string.Empty).ToLowerInvariant();

            if (prefab.Contains("door") || prefab.Contains("window") || prefab.Contains("shutter"))
                return EntityCategory.DoorWindow;

            if (prefab.Contains("battery"))
                return EntityCategory.Battery;

            if (prefab.Contains("solarpanel") || prefab.Contains("solar_panel") || prefab.Contains("solar.panel"))
                return EntityCategory.Solar;

            if (
                prefab.Contains("electric") ||
                prefab.Contains("switch") ||
                prefab.Contains("splitter") ||
                prefab.Contains("combiner") ||
                prefab.Contains("memorycell") ||
                prefab.Contains("counter") ||
                prefab.Contains("timer") ||
                prefab.Contains("generator") ||
                prefab.Contains("blocker") ||
                prefab.Contains("branch") ||
                prefab.Contains("autoturret")
            )
                return EntityCategory.Electrical;

            return EntityCategory.Other;
        }

        private bool IsObviouslyTransient(BaseEntity entity)
        {
            string prefab = (entity.ShortPrefabName ?? string.Empty).ToLowerInvariant();
            string typeName = entity.GetType().Name.ToLowerInvariant();

            return
                prefab.Contains("item_drop") ||
                prefab.Contains("itemdrop") ||
                prefab.Contains("player_corpse") ||
                prefab.Contains("corpse") ||
                prefab.Contains("backpack") ||
                prefab.Contains("worlditem") ||
                prefab.Contains("projectile") ||
                prefab.Contains("grenade") ||
                prefab.Contains("rocket") ||
                typeName.Contains("droppeditem") ||
                typeName.Contains("corpse") ||
                typeName.Contains("projectile");
        }

        private void AddStandardResourceCosts(
            IEnumerable<ItemAmount> costs,
            ref float wood,
            ref float stone,
            ref float metal,
            ref float hqm)
        {
            if (costs == null)
                return;

            foreach (ItemAmount cost in costs)
            {
                if (cost == null || cost.itemDef == null || cost.amount <= 0f)
                    continue;

                switch (cost.itemDef.shortname)
                {
                    case "wood":
                        wood += cost.amount;
                        break;
                    case "stones":
                        stone += cost.amount;
                        break;
                    case "metal.fragments":
                        metal += cost.amount;
                        break;
                    case "metal.refined":
                        hqm += cost.amount;
                        break;
                }
            }
        }

        private void AddRate(ResourceRate target, ResourceRate source)
        {
            if (target == null || source == null)
                return;

            target.WoodPerDay += source.WoodPerDay;
            target.StonePerDay += source.StonePerDay;
            target.MetalPerDay += source.MetalPerDay;
            target.HqmPerDay += source.HqmPerDay;
        }

        private void CacheResourceItemIds()
        {
            _woodItemId = GetItemId("wood");
            _stoneItemId = GetItemId("stones");
            _metalItemId = GetItemId("metal.fragments");
            _hqmItemId = GetItemId("metal.refined");
        }

        private int GetItemId(string shortName)
        {
            ItemDefinition definition = ItemManager.FindItemDefinition(shortName);
            return definition == null ? 0 : definition.itemid;
        }

        private void BuildDeployableItemIndex()
        {
            _deployableItemByPrefab.Clear();

            foreach (ItemDefinition itemDefinition in ItemManager.itemList)
            {
                if (itemDefinition == null)
                    continue;

                ItemModDeployable deployable = itemDefinition.GetComponent<ItemModDeployable>();
                string resourcePath = deployable?.entityPrefab?.resourcePath;

                if (string.IsNullOrEmpty(resourcePath))
                    continue;

                GameObject prefabObject = GameManager.server.FindPrefab(resourcePath);
                BaseEntity prefabEntity = prefabObject?.GetComponent<BaseEntity>();

                if (prefabEntity == null || string.IsNullOrEmpty(prefabEntity.ShortPrefabName))
                    continue;

                if (!_deployableItemByPrefab.ContainsKey(prefabEntity.ShortPrefabName))
                    _deployableItemByPrefab.Add(prefabEntity.ShortPrefabName, itemDefinition);
            }
        }

        private void BuildBlueprintIndex()
        {
            _blueprintByItemId.Clear();

            foreach (ItemBlueprint blueprint in ItemManager.bpList)
            {
                if (blueprint == null || blueprint.targetItem == null)
                    continue;

                _blueprintByItemId[blueprint.targetItem.itemid] = blueprint;
            }
        }

        private float GetUpkeepPeriodMinutes()
        {
            float upkeepPeriod = ConVar.Decay.upkeep_period_minutes;
            return upkeepPeriod > 0f ? upkeepPeriod : MinutesPerDay;
        }

        private float GetVanillaTaxRate(int entityCount)
        {
            if (entityCount <= 0)
                return 0f;

            int remaining = entityCount;
            float weightedTax = 0f;

            int bracket0 = Mathf.Min(remaining, Mathf.Max(0, ConVar.Decay.bracket_0_blockcount));
            weightedTax += bracket0 * ConVar.Decay.bracket_0_costfraction;
            remaining -= bracket0;

            if (remaining > 0)
            {
                int bracket1 = Mathf.Min(remaining, Mathf.Max(0, ConVar.Decay.bracket_1_blockcount));
                weightedTax += bracket1 * ConVar.Decay.bracket_1_costfraction;
                remaining -= bracket1;
            }

            if (remaining > 0)
            {
                int bracket2 = Mathf.Min(remaining, Mathf.Max(0, ConVar.Decay.bracket_2_blockcount));
                weightedTax += bracket2 * ConVar.Decay.bracket_2_costfraction;
                remaining -= bracket2;
            }

            if (remaining > 0)
                weightedTax += remaining * ConVar.Decay.bracket_3_costfraction;

            return weightedTax / entityCount;
        }

        private int CeilPositive(float value)
        {
            return value <= 0f ? 0 : Mathf.CeilToInt(value);
        }

        private bool IsAuthorized(BuildingPrivlidge tc, ulong userId)
        {
            if (tc == null || userId == 0)
                return false;

            return tc.authorizedPlayers.Contains(userId);
        }

        private void RecordSnapshotMetrics(double startedMilliseconds, int blocksScanned, int candidatesScanned)
        {
            _lastSnapshotMilliseconds = Math.Max(0d, CurrentUnixMilliseconds() - startedMilliseconds);
            _lastSnapshotBuildingsScanned = blocksScanned;
            _lastSnapshotCandidatesScanned = candidatesScanned;
        }

        private double CurrentUnixMilliseconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private void LoadPersistentData()
        {
            try
            {
                _dataFile = Interface.Oxide.DataFileSystem.GetFile(Name);
                _storedData = _dataFile.ReadObject<StoredData>() ?? new StoredData();

                if (_storedData.Tcs == null)
                    _storedData.Tcs = new Dictionary<string, StoredTcState>();

                if (_storedData.Repairs == null)
                    _storedData.Repairs = new Dictionary<string, StoredRepairState>();

                if (_storedData.TransportShutdown == null)
                    _storedData.TransportShutdown =
                        new Dictionary<string, StoredTransportShutdownState>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not read persistent data; starting with empty state. {ex.Message}");
                _dataFile = Interface.Oxide.DataFileSystem.GetFile(Name);
                _storedData = new StoredData();
            }

            _dataDirty = false;
        }

        private void SavePersistentData(bool force)
        {
            if (!force && !_dataDirty)
                return;

            if (_dataFile == null)
                _dataFile = Interface.Oxide.DataFileSystem.GetFile(Name);

            StoredData data = new StoredData();

            if (_storedData != null)
            {
                if (_storedData.TransportShutdown != null)
                {
                    data.TransportShutdown =
                        new Dictionary<string, StoredTransportShutdownState>(
                            _storedData.TransportShutdown,
                            StringComparer.Ordinal
                        );
                }

                data.CleanShutdownPending = _storedData.CleanShutdownPending;
                data.CleanShutdownMarkedUnix = _storedData.CleanShutdownMarkedUnix;
                data.TransportCheckpointCapturedUnix = _storedData.TransportCheckpointCapturedUnix;
                data.TransportCheckpointSource = _storedData.TransportCheckpointSource ?? string.Empty;
                data.TransportCheckpointBootId = _storedData.TransportCheckpointBootId ?? string.Empty;
            }

            foreach (KeyValuePair<BuildingPrivlidge, EstateAccount> pair in _accounts)
            {
                BuildingPrivlidge tc = pair.Key;
                EstateAccount account = pair.Value;

                if (tc == null || tc.IsDestroyed || tc.net == null || account == null)
                    continue;

                string key = tc.net.ID.ToString();

                StoredTcState stored = new StoredTcState();
                stored.NetId = key;
                stored.OwnerId = tc.OwnerID;
                stored.Position = StoredPosition.FromVector3(tc.transform.position);
                stored.Paid = account.Paid;
                stored.LastFailure = account.LastFailure ?? string.Empty;
                stored.DebtWood = account.DebtWood;
                stored.DebtStone = account.DebtStone;
                stored.DebtMetal = account.DebtMetal;
                stored.DebtHqm = account.DebtHqm;

                data.Tcs[key] = stored;
            }

            foreach (KeyValuePair<BaseCombatEntity, float> pair in _decayRepairDebt)
            {
                BaseCombatEntity entity = pair.Key;
                float debt = pair.Value;

                if (entity == null || entity.IsDestroyed || entity.net == null || debt <= 0.01f)
                    continue;

                string key = entity.net.ID.ToString();

                StoredRepairState stored = new StoredRepairState();
                stored.NetId = key;
                stored.OwnerId = entity.OwnerID;
                stored.Prefab = entity.ShortPrefabName ?? string.Empty;
                stored.Position = StoredPosition.FromVector3(entity.transform.position);
                stored.Debt = debt;

                data.Repairs[key] = stored;
            }

            try
            {
                _storedData = data;
                _dataFile.WriteObject(_storedData);
                _dataDirty = false;
            }
            catch (Exception ex)
            {
                PrintError($"Failed to save persistent Estate Upkeep state: {ex}");
            }
        }

        private void RestorePersistentState(EstateSnapshot snapshot)
        {
            _accounts.Clear();
            _decayRepairDebt.Clear();

            if (_storedData == null)
                _storedData = new StoredData();

            Dictionary<string, EstateTcSnapshot> tcByNetId =
                new Dictionary<string, EstateTcSnapshot>(StringComparer.Ordinal);

            foreach (KeyValuePair<BuildingPrivlidge, EstateTcSnapshot> pair in snapshot.ByTc)
            {
                BuildingPrivlidge tc = pair.Key;
                if (tc == null || tc.IsDestroyed || tc.net == null)
                    continue;

                tcByNetId[tc.net.ID.ToString()] = pair.Value;
            }

            double now = CurrentUnixSeconds();

            foreach (KeyValuePair<string, StoredTcState> pair in _storedData.Tcs)
            {
                StoredTcState stored = pair.Value;
                EstateTcSnapshot tcSnapshot;

                if (stored == null || !tcByNetId.TryGetValue(pair.Key, out tcSnapshot))
                    continue;

                BuildingPrivlidge tc = tcSnapshot.Tc;
                if (!StoredTcMatches(stored, tc))
                    continue;

                EstateAccount account = new EstateAccount();
                account.LastTick = now; // no billing for server/plugin downtime
                account.Paid = stored.Paid && CanStartProtection(tc, tcSnapshot.Rate);
                account.LastFailure = account.Paid ? string.Empty : (stored.LastFailure ?? string.Empty);
                account.DebtWood = Mathf.Max(0f, stored.DebtWood);
                account.DebtStone = Mathf.Max(0f, stored.DebtStone);
                account.DebtMetal = Mathf.Max(0f, stored.DebtMetal);
                account.DebtHqm = Mathf.Max(0f, stored.DebtHqm);

                _accounts[tc] = account;
            }

            Dictionary<string, BaseCombatEntity> entityByNetId =
                new Dictionary<string, BaseCombatEntity>(StringComparer.Ordinal);

            foreach (BaseCombatEntity entity in _eligibleEstateCombatEntities)
            {
                if (entity == null || entity.IsDestroyed || entity.net == null)
                    continue;

                entityByNetId[entity.net.ID.ToString()] = entity;
            }

            foreach (KeyValuePair<string, StoredRepairState> pair in _storedData.Repairs)
            {
                StoredRepairState stored = pair.Value;
                BaseCombatEntity entity;

                if (stored == null || !entityByNetId.TryGetValue(pair.Key, out entity))
                    continue;

                if (!StoredEntityMatches(stored, entity))
                    continue;

                float missing = Mathf.Max(0f, entity.MaxHealth() - entity.Health());
                float debt = Mathf.Min(Mathf.Max(0f, stored.Debt), missing);

                if (debt > 0.01f)
                    _decayRepairDebt[entity] = debt;
            }

            RebuildProtectedEntitySet(snapshot);
            _dataDirty = false;

            Puts($"Restored persistent state: {_accounts.Count} TC account(s), {_decayRepairDebt.Count} repair debt record(s).");
        }

        private bool StoredTcMatches(StoredTcState stored, BuildingPrivlidge tc)
        {
            if (stored == null || tc == null)
                return false;

            if (stored.OwnerId != 0 && tc.OwnerID != stored.OwnerId)
                return false;

            return stored.Position == null ||
                   Vector3.Distance(stored.Position.ToVector3(), tc.transform.position) <= 1.5f;
        }

        private bool StoredEntityMatches(StoredRepairState stored, BaseCombatEntity entity)
        {
            if (stored == null || entity == null)
                return false;

            if (stored.OwnerId != 0 && entity.OwnerID != stored.OwnerId)
                return false;

            if (!string.IsNullOrEmpty(stored.Prefab) &&
                !string.Equals(stored.Prefab, entity.ShortPrefabName, StringComparison.OrdinalIgnoreCase))
                return false;

            return stored.Position == null ||
                   Vector3.Distance(stored.Position.ToVector3(), entity.transform.position) <= 1.5f;
        }

        private void MarkDataDirty()
        {
            _dataDirty = true;
        }

        private double CurrentUnixSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        }

        private enum EntityCategory
        {
            DoorWindow,
            Battery,
            Solar,
            Electrical,
            Other
        }

        private class EstateSnapshot
        {
            public readonly Dictionary<BuildingPrivlidge, EstateTcSnapshot> ByTc =
                new Dictionary<BuildingPrivlidge, EstateTcSnapshot>();
        }

        private class EstateTcSnapshot
        {
            public BuildingPrivlidge Tc;

            public readonly List<DetachedBuildingSnapshot> Buildings =
                new List<DetachedBuildingSnapshot>();

            public readonly HashSet<BaseEntity> AssociatedEntities =
                new HashSet<BaseEntity>();

            public readonly ResourceRate Rate = new ResourceRate();

            public int BlockCount;
            public int DoorWindowCount;
            public int BatteryCount;
            public int SolarCount;
            public int OtherElectricalCount;
            public int OtherEntityCount;
        }

        private class DetachedBuildingSnapshot
        {
            public uint BuildingId;
            public ulong OwnerId;
            public BuildingPrivlidge Tc;

            public readonly List<BuildingBlock> Blocks =
                new List<BuildingBlock>();

            public readonly List<BaseEntity> DoorWindowEntities =
                new List<BaseEntity>();

            public ResourceRate BlockRate;
            public ResourceRate DoorRate;
        }

        private class ResourceRate
        {
            public float TaxRate;
            public float WoodPerDay;
            public float StonePerDay;
            public float MetalPerDay;
            public float HqmPerDay;
        }

        private class PluginConfig
        {
            public VehicleProtectionConfig VehicleProtection = new VehicleProtectionConfig();

            public static PluginConfig CreateDefault()
            {
                PluginConfig config = new PluginConfig();
                config.VehicleProtection.Enabled = false;
                config.VehicleProtection.PreventDecayInsideTc = true;
                config.VehicleProtection.ProtectLand = true;
                config.VehicleProtection.ProtectWater = true;
                config.VehicleProtection.ProtectAir = true;
                config.VehicleProtection.RestoreProtectedHpAfterCleanRestart = true;
                config.VehicleProtection.RestartMatchRadiusMeters = 8f;
                config.VehicleProtection.RestartCheckpointMaxAgeAtShutdownSeconds = 90f;
                return config;
            }
        }

        private class VehicleProtectionConfig
        {
            // Public/plugin default stays OFF. Server owners opt in explicitly with /estate transport on.
            public bool Enabled = false;

            // When enabled, only Rust DamageType.Decay is removed for supported
            // transport physically inside any TC building privilege zone.
            public bool PreventDecayInsideTc = true;

            public bool ProtectLand = true;
            public bool ProtectWater = true;
            public bool ProtectAir = true;

            // Preserves exact protected transport HP across a clean server restart.
            // It never heals damage that already existed before shutdown.
            public bool RestoreProtectedHpAfterCleanRestart = true;

            // Runtime Net IDs are not used as persistent transport identity.
            // This radius is only used during one-shot restart snapshot matching.
            public float RestartMatchRadiusMeters = 8f;

            // The checkpoint must have been captured shortly before shutdown.
            // If it is older, it is discarded instead of risking a false heal.
            public float RestartCheckpointMaxAgeAtShutdownSeconds = 90f;
        }

        private class StoredData
        {
            public Dictionary<string, StoredTcState> Tcs =
                new Dictionary<string, StoredTcState>();

            public Dictionary<string, StoredRepairState> Repairs =
                new Dictionary<string, StoredRepairState>();

            public Dictionary<string, StoredTransportShutdownState> TransportShutdown =
                new Dictionary<string, StoredTransportShutdownState>(StringComparer.Ordinal);

            public bool CleanShutdownPending = false;
            public long CleanShutdownMarkedUnix = 0;
            public long TransportCheckpointCapturedUnix = 0;
            public string TransportCheckpointSource = string.Empty;
            public string TransportCheckpointBootId = string.Empty;
        }

        private class StoredTransportShutdownState
        {
            public string NetId;
            public ulong OwnerId;
            public string Prefab = string.Empty;
            public StoredPosition Position;
            public float Health;
            public float MaxHealth;
            public string Category = string.Empty;
        }

        private class LiveTransportHealthCandidate
        {
            public BaseCombatEntity Entity;
            public BaseCombatEntity Root;
            public string Category = string.Empty;
        }

        private class StoredTcState
        {
            public string NetId;
            public ulong OwnerId;
            public StoredPosition Position;

            public bool Paid;
            public string LastFailure = string.Empty;

            public float DebtWood;
            public float DebtStone;
            public float DebtMetal;
            public float DebtHqm;
        }

        private class StoredRepairState
        {
            public string NetId;
            public ulong OwnerId;
            public string Prefab = string.Empty;
            public StoredPosition Position;
            public float Debt;
        }

        private class StoredPosition
        {
            public float X;
            public float Y;
            public float Z;

            public static StoredPosition FromVector3(Vector3 value)
            {
                StoredPosition position = new StoredPosition();
                position.X = value.x;
                position.Y = value.y;
                position.Z = value.z;
                return position;
            }

            public Vector3 ToVector3()
            {
                return new Vector3(X, Y, Z);
            }
        }

        private class EstateAccount
        {
            public double LastTick;
            public bool Paid;
            public string LastFailure = string.Empty;

            public float DebtWood;
            public float DebtStone;
            public float DebtMetal;
            public float DebtHqm;
        }
    }
}
