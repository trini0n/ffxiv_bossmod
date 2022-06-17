﻿using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BossMod
{
    // attribute that specifies object ID for the boss module's "primary" actor - for each such actor we create corresponding boss module
    // by default, module activates (transitions to phase 0) whenever "primary" actor becomes both targetable and in combat (this is how we detect 'pull') - though this can be overridden if needed
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class PrimaryActorOIDAttribute : Attribute
    {
        public uint OID { get; private init; }

        public PrimaryActorOIDAttribute(uint oid)
        {
            OID = oid;
        }
    }

    // base for boss modules - provides all the common features, so that look is standardized
    // TODO: it should not require or know anything about manager...
    public class BossModule : IDisposable
    {
        public BossModuleManager Manager { get; init; }
        public Actor PrimaryActor { get; init; }
        public MiniArena Arena { get; init; }
        public StateMachine? StateMachine { get; protected set; }
        public CooldownPlanExecution? PlanExecution = null;

        public WorldState WorldState => Manager.WorldState;
        public PartyState Raid => WorldState.Party;
        public ArenaBounds Bounds => Arena.Bounds;

        // per-oid enemy lists; filled on first request
        private Dictionary<uint, List<Actor>> _relevantEnemies = new(); // key = actor OID
        public IReadOnlyDictionary<uint, List<Actor>> RelevantEnemies => _relevantEnemies;
        public List<Actor> Enemies<OID>(OID oid) where OID : Enum
        {
            var castOID = (uint)(object)oid;
            var entry = _relevantEnemies.GetValueOrDefault(castOID);
            if (entry == null)
            {
                entry = new();
                foreach (var actor in WorldState.Actors.Where(actor => actor.OID == castOID))
                    entry.Add(actor);
                _relevantEnemies[castOID] = entry;
            }
            return entry;
        }

        private BossComponent _rootComp = new();
        public IReadOnlyList<BossComponent> Components => _rootComp.Subcomponents;
        public void ActivateComponent<T>() where T : BossComponent, new() => _rootComp.AddAndInitSubcomponent<T>(this);
        public void DeactivateComponent<T>() where T : BossComponent => _rootComp.RemoveSubcomponent<T>();
        public T? FindComponent<T>() where T : BossComponent => _rootComp.FindSubcomponent<T>();
        public void ClearComponents() => _rootComp.RemoveAllSubcomponents();

        public BossModule(BossModuleManager manager, Actor primary, ArenaBounds bounds)
        {
            Manager = manager;
            PrimaryActor = primary;
            Arena = new(Manager.WindowConfig, bounds);

            WorldState.Actors.Added += OnActorCreated;
            WorldState.Actors.Removed += OnActorDestroyed;
            WorldState.Actors.CastStarted += OnActorCastStarted;
            WorldState.Actors.CastFinished += OnActorCastFinished;
            WorldState.Actors.Tethered += OnActorTethered;
            WorldState.Actors.Untethered += OnActorUntethered;
            WorldState.Actors.StatusGain += OnActorStatusGain;
            WorldState.Actors.StatusLose += OnActorStatusLose;
            WorldState.Actors.StatusChange += OnActorStatusChange;
            WorldState.Events.Icon += OnEventIcon;
            WorldState.Events.Cast += OnEventCast;
            WorldState.Events.EnvControl += OnEventEnvControl;
            foreach (var v in WorldState.Actors)
                OnActorCreated(null, v);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StateMachine?.Reset();
                ClearComponents();

                WorldState.Actors.Added -= OnActorCreated;
                WorldState.Actors.Removed -= OnActorDestroyed;
                WorldState.Actors.CastStarted -= OnActorCastStarted;
                WorldState.Actors.CastFinished -= OnActorCastFinished;
                WorldState.Actors.Tethered -= OnActorTethered;
                WorldState.Actors.Untethered -= OnActorUntethered;
                WorldState.Actors.StatusGain -= OnActorStatusGain;
                WorldState.Actors.StatusLose -= OnActorStatusLose;
                WorldState.Actors.StatusChange -= OnActorStatusChange;
                WorldState.Events.Icon -= OnEventIcon;
                WorldState.Events.Cast -= OnEventCast;
                WorldState.Events.EnvControl -= OnEventEnvControl;
            }
        }

        public void Update()
        {
            if (StateMachine == null)
                return;

            // update cooldown plan if needed
            var cls = Raid.Player()?.Class ?? Class.None;
            var plan = Manager.CooldownPlanManager.SelectedPlan(PrimaryActor.OID, cls);
            if (PlanExecution == null || PlanExecution?.Plan != plan)
            {
                Service.Log($"[BM] Selected plan for '{GetType()}' ({PrimaryActor.InstanceID:X}) for {cls}: '{(plan?.Name ?? "<none>")}'");
                PlanExecution = new(StateMachine, plan);
            }

            if (StateMachine.ActivePhaseIndex < 0 && CheckPull())
                StateMachine.Start(WorldState.CurrentTime);

            if (StateMachine.ActiveState != null)
                StateMachine.Update(WorldState.CurrentTime);

            if (StateMachine.ActiveState != null)
            {
                UpdateModule();
                _rootComp.UpdateRec(this);
            }
        }

        public virtual void Draw(float cameraAzimuth, int pcSlot, BossComponent.MovementHints? pcMovementHints)
        {
            if (Manager.WindowConfig.ShowMechanicTimers)
                StateMachine?.Draw();

            if (Manager.WindowConfig.ShowGlobalHints)
                DrawGlobalHints();

            if (Manager.WindowConfig.ShowPlayerHints)
                DrawHintForPlayer(pcSlot, pcMovementHints);

            Arena.Begin(cameraAzimuth);
            DrawArena(pcSlot);
            Arena.End();
        }

        public virtual void DrawArena(int pcSlot)
        {
            var pc = Raid[pcSlot];
            if (pc == null)
                return;

            // draw background
            DrawArenaBackground(pcSlot, pc);
            _rootComp.DrawArenaBackgroundRec(this, pcSlot, pc, Arena);

            // draw borders
            Arena.Border();
            if (Manager.WindowConfig.ShowWaymarks)
                DrawWaymarks();

            // draw non-player alive party members
            DrawPartyMembers(pcSlot, pc);

            // draw foreground
            DrawArenaForegroundPre(pcSlot, pc);
            _rootComp.DrawArenaForegroundRec(this, pcSlot, pc, Arena);
            DrawArenaForegroundPost(pcSlot, pc);

            // draw player
            Arena.Actor(pc, ArenaColor.PC);
        }

        public BossComponent.TextHints CalculateHintsForRaidMember(int slot, Actor actor, BossComponent.MovementHints? movementHints = null)
        {
            BossComponent.TextHints hints = new();
            _rootComp.AddHintsRec(this, slot, actor, hints, movementHints);
            return hints;
        }

        public BossComponent.GlobalHints CalculateGlobalHints()
        {
            BossComponent.GlobalHints hints = new();
            _rootComp.AddGlobalHintsRec(this, hints);
            return hints;
        }

        // TODO: move to some better place...
        public static WPos AdjustPositionForKnockback(WPos pos, WPos origin, float distance)
        {
            return pos != origin ? pos + distance * (pos - origin).Normalized() : pos;
        }

        public static WPos AdjustPositionForKnockback(WPos pos, Actor? source, float distance)
        {
            return source != null ? AdjustPositionForKnockback(pos, source.Position, distance) : pos;
        }

        public void ReportError(BossComponent? comp, string message)
        {
            Service.Log($"[ModuleError] [{this.GetType().Name}] [{comp?.GetType().Name}] {message}");
            Manager.HandleError(this, comp, message);
        }

        // called during update if module is not yet active, should return true if it is to be activated
        // default implementation activates if primary target is both targetable and in combat
        protected virtual bool CheckPull() { return PrimaryActor.IsTargetable && PrimaryActor.InCombat; }

        protected virtual void UpdateModule() { }
        protected virtual void DrawArenaBackground(int pcSlot, Actor pc) { } // before modules background
        protected virtual void DrawArenaForegroundPre(int pcSlot, Actor pc) { } // after border, before modules foreground
        protected virtual void DrawArenaForegroundPost(int pcSlot, Actor pc) { } // after modules foreground

        private void DrawGlobalHints()
        {
            var hints = CalculateGlobalHints();
            ImGui.PushStyleColor(ImGuiCol.Text, 0xffffff00);
            foreach (var hint in hints)
            {
                ImGui.TextUnformatted(hint);
                ImGui.SameLine();
            }
            ImGui.PopStyleColor();
            ImGui.NewLine();
        }

        private void DrawHintForPlayer(int pcSlot, BossComponent.MovementHints? movementHints)
        {
            var pc = Raid[pcSlot];
            if (pc == null)
                return;

            var hints = CalculateHintsForRaidMember(pcSlot, pc, movementHints);
            foreach ((var hint, bool risk) in hints)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, risk ? ArenaColor.Danger : ArenaColor.Safe);
                ImGui.TextUnformatted(hint);
                ImGui.PopStyleColor();
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        private void DrawWaymarks()
        {
            DrawWaymark(WorldState.Waymarks[Waymark.A], "A", 0xffff928a);
            DrawWaymark(WorldState.Waymarks[Waymark.B], "B", 0xfff7c139);
            DrawWaymark(WorldState.Waymarks[Waymark.C], "C", 0xff6cb4e0);
            DrawWaymark(WorldState.Waymarks[Waymark.D], "D", 0xffa352fa);
            DrawWaymark(WorldState.Waymarks[Waymark.N1], "1", 0xffff928a);
            DrawWaymark(WorldState.Waymarks[Waymark.N2], "2", 0xfff7c139);
            DrawWaymark(WorldState.Waymarks[Waymark.N3], "3", 0xff6cb4e0);
            DrawWaymark(WorldState.Waymarks[Waymark.N4], "4", 0xffa352fa);
        }

        private void DrawWaymark(Vector3? pos, string text, uint color)
        {
            if (pos != null)
            {
                Arena.TextWorld(new(pos.Value.XZ()), text, color, 22);
            }
        }

        private void DrawPartyMembers(int pcSlot, Actor pc)
        {
            foreach (var (slot, player) in Raid.WithSlot().Exclude(pcSlot))
            {
                var (prio, color) = _rootComp.CalcPriorityRec(this, pcSlot, pc, slot, player);
                if (prio == BossComponent.PlayerPriority.Irrelevant && !Manager.WindowConfig.ShowIrrelevantPlayers)
                    continue;

                if (color == 0)
                {
                    color = prio switch
                    {
                        BossComponent.PlayerPriority.Interesting => ArenaColor.PlayerInteresting,
                        BossComponent.PlayerPriority.Danger => ArenaColor.Danger,
                        BossComponent.PlayerPriority.Critical => ArenaColor.Vulnerable, // TODO: select some better color...
                        _ => ArenaColor.PlayerGeneric
                    };
                }
                Arena.Actor(player, color);
            }
        }

        private void OnActorCreated(object? sender, Actor actor)
        {
            var relevant = _relevantEnemies.GetValueOrDefault(actor.OID);
            if (relevant != null)
                relevant.Add(actor);
        }

        private void OnActorDestroyed(object? sender, Actor actor)
        {
            var relevant = _relevantEnemies.GetValueOrDefault(actor.OID);
            if (relevant != null)
                relevant.Remove(actor);
        }

        private void OnActorCastStarted(object? sender, Actor actor)
        {
            _rootComp.OnCastStarted(this, actor);
        }

        private void OnActorCastFinished(object? sender, Actor actor)
        {
            _rootComp.OnCastFinished(this, actor);
        }

        private void OnActorTethered(object? sender, Actor actor)
        {
            var target = WorldState.Actors.Find(actor.Tether.Target);
            if (target != null)
                _rootComp.HandleTethered(this, actor, target);
        }

        private void OnActorUntethered(object? sender, Actor actor)
        {
            var target = WorldState.Actors.Find(actor.Tether.Target);
            if (target != null)
                _rootComp.HandleUntethered(this, actor, target);
        }

        private void OnActorStatusGain(object? sender, (Actor actor, int index) arg)
        {
            if (arg.actor.Type == ActorType.Player)
            {
                int slot = Raid.FindSlot(arg.actor.InstanceID);
                if (slot >= 0)
                    _rootComp.HandlePartyStatusUpdate(this, slot, arg.actor, arg.index);
            }
            else if (arg.actor.Type is not ActorType.Pet and not ActorType.Chocobo)
            {
                _rootComp.HandleEnemyStatusUpdate(this, arg.actor, arg.index);
            }
        }

        private void OnActorStatusLose(object? sender, (Actor actor, int index) arg)
        {
            if (arg.actor.Type == ActorType.Player)
            {
                int slot = Raid.FindSlot(arg.actor.InstanceID);
                if (slot >= 0)
                    _rootComp.HandlePartyStatusLose(this, slot, arg.actor, arg.index);
            }
            else if (arg.actor.Type is not ActorType.Pet and not ActorType.Chocobo)
            {
                _rootComp.HandleEnemyStatusLose(this, arg.actor, arg.index);
            }
        }

        private void OnActorStatusChange(object? sender, (Actor actor, int index, ushort prevExtra, DateTime prevExpire) arg)
        {
            if (arg.actor.Type == ActorType.Player)
            {
                int slot = Raid.FindSlot(arg.actor.InstanceID);
                if (slot >= 0)
                    _rootComp.HandlePartyStatusUpdate(this, slot, arg.actor, arg.index);
            }
            else if (arg.actor.Type is not ActorType.Pet and not ActorType.Chocobo)
            {
                _rootComp.HandleEnemyStatusUpdate(this, arg.actor, arg.index);
            }
        }

        private void OnEventIcon(object? sender, (ulong actorID, uint iconID) arg)
        {
            _rootComp.OnEventIcon(this, arg.actorID, arg.iconID);
        }

        private void OnEventCast(object? sender, CastEvent info)
        {
            _rootComp.OnEventCast(this, info);
        }

        private void OnEventEnvControl(object? sender, (uint featureID, byte index, uint state) arg)
        {
            _rootComp.OnEventEnvControl(this, arg.featureID, arg.index, arg.state);
        }
    }
}
