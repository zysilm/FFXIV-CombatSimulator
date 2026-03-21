using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Animation;

public class ActionEffectRequest
{
    public uint SourceEntityId { get; set; }
    public Vector3 SourcePosition { get; set; }
    public uint ActionId { get; set; }
    public float AnimationLock { get; set; } = 0.6f;
    public float SourceRotation { get; set; }
    public bool IsSourcePlayer { get; set; }
    public bool IsRanged { get; set; }
    public List<TargetEffect> Targets { get; set; } = new();
}

public class TargetEffect
{
    public ulong TargetId { get; set; }
    public int Damage { get; set; }
    public int Healing { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public SimDamageType DamageType { get; set; }
}

public unsafe class AnimationController : IDisposable
{
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly ChatCommandExecutor commandExecutor;
    private readonly Configuration config;

    // Monotonically increasing sequence number for fabricated ActionEffects
    private uint globalSequence = 0x10000000;

    public AnimationController(
        IPluginLog log,
        IClientState clientState,
        ChatCommandExecutor commandExecutor,
        Configuration config)
    {
        this.log = log;
        this.clientState = clientState;
        this.commandExecutor = commandExecutor;
        this.config = config;

        log.Info("AnimationController: Initialized with ActionEffectHandler + command system.");
    }

    public void Tick(float deltaTime)
    {
        commandExecutor.Tick(deltaTime);
    }

    /// <summary>
    /// Play attack animation + VFX + hit reaction via ActionEffectHandler.Receive().
    /// This triggers the game's full combat visual pipeline: caster animation, target hit
    /// reaction, VFX particles, damage flytext, and sound effects — all in one call.
    /// If custom commands are configured for the player, those are used instead.
    /// </summary>
    public void PlayActionEffect(ActionEffectRequest request)
    {
        if (request.Targets.Count == 0)
            return;

        try
        {
            // Check for custom command override (player only)
            if (request.IsSourcePlayer)
            {
                var customCommand = request.IsRanged
                    ? config.PlayerRangedAttackCommand
                    : config.PlayerMeleeAttackCommand;

                if (!string.IsNullOrWhiteSpace(customCommand))
                {
                    // Use custom chat command instead of ActionEffect pipeline
                    commandExecutor.ExecuteCommand(customCommand, cooldown: 0.8f);
                    log.Verbose($"Custom attack command: {customCommand}");
                    return;
                }
            }

            // Use ActionEffectHandler.Receive() for the real combat pipeline
            CallActionEffectReceive(request);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to play action effect.");
        }
    }

    /// <summary>
    /// Fabricate ActionEffect data and feed it to ActionEffectHandler.Receive().
    /// This is the same code path the game uses when receiving combat results from the server.
    /// </summary>
    private void CallActionEffectReceive(ActionEffectRequest request)
    {
        var targetCount = Math.Min(request.Targets.Count, 32);

        // Resolve caster Character*
        Character* casterPtr = FindCharacter(request.SourceEntityId, request.IsSourcePlayer);
        if (casterPtr == null)
        {
            log.Warning($"ActionEffect: Could not find caster 0x{request.SourceEntityId:X}");
            return;
        }

        // Allocate native memory for the structs
        var headerSize = sizeof(ActionEffectHandler.Header);
        var effectsSize = sizeof(ActionEffectHandler.TargetEffects) * targetCount;
        var idsSize = sizeof(GameObjectId) * targetCount;

        var headerPtr = (ActionEffectHandler.Header*)Marshal.AllocHGlobal(headerSize);
        var effectsPtr = (ActionEffectHandler.TargetEffects*)Marshal.AllocHGlobal(effectsSize);
        var targetIdsPtr = (GameObjectId*)Marshal.AllocHGlobal(idsSize);

        try
        {
            // Zero initialize
            NativeMemory.Clear(headerPtr, (nuint)headerSize);
            NativeMemory.Clear(effectsPtr, (nuint)effectsSize);
            NativeMemory.Clear(targetIdsPtr, (nuint)idsSize);

            // Fill header
            var seq = globalSequence++;
            headerPtr->AnimationTargetId = request.Targets[0].TargetId;
            headerPtr->ActionId = request.ActionId;
            headerPtr->GlobalSequence = seq;
            headerPtr->AnimationLock = request.AnimationLock;
            headerPtr->SourceSequence = 0; // Not client-initiated (prevents the game from expecting a server response)
            headerPtr->RotationInt = QuantizeRotation(request.SourceRotation);
            headerPtr->SpellId = (ushort)(request.ActionId & 0xFFFF);
            headerPtr->ActionType = 1; // Action
            headerPtr->NumTargets = (byte)targetCount;
            headerPtr->ShowInLog = false; // Don't pollute the real action log
            headerPtr->ForceAnimationLock = false; // Don't lock the player

            // Fill per-target effects
            for (int i = 0; i < targetCount; i++)
            {
                var target = request.Targets[i];
                targetIdsPtr[i] = (GameObjectId)target.TargetId;

                // Build damage effect entry
                ref var effect = ref effectsPtr[i].Effects[0];
                if (target.Damage > 0)
                {
                    effect.Type = 3; // Damage
                    effect.Value = (ushort)(target.Damage & 0xFFFF);
                    effect.Param0 = 0;
                    if (target.IsCritical) effect.Param0 |= 0x01;
                    if (target.IsDirectHit) effect.Param0 |= 0x02;
                    // Encode damage type: bits 2-3
                    effect.Param0 |= target.DamageType switch
                    {
                        SimDamageType.Physical => 0,
                        SimDamageType.Magical => (byte)(1 << 2),
                        SimDamageType.Unique => (byte)(2 << 2),
                        _ => 0,
                    };
                    // High bits for large damage values
                    if (target.Damage > 0xFFFF)
                    {
                        effect.Param3 = (byte)((target.Damage >> 16) & 0xFF);
                        effect.Param4 = (byte)((target.Damage >> 24) & 0xFF);
                    }
                }
                else if (target.Healing > 0)
                {
                    effect.Type = 4; // Heal
                    effect.Value = (ushort)(target.Healing & 0xFFFF);
                    if (target.IsCritical) effect.Param0 |= 0x01;
                }
            }

            // Call the game's ActionEffect handler
            var casterPos = request.SourcePosition;
            ActionEffectHandler.Receive(
                request.SourceEntityId,
                casterPtr,
                &casterPos,
                headerPtr,
                effectsPtr,
                targetIdsPtr);

            log.Verbose($"ActionEffectHandler.Receive: caster=0x{request.SourceEntityId:X}, " +
                        $"action={request.ActionId}, targets={targetCount}, seq={seq}");
        }
        finally
        {
            Marshal.FreeHGlobal((nint)headerPtr);
            Marshal.FreeHGlobal((nint)effectsPtr);
            Marshal.FreeHGlobal((nint)targetIdsPtr);
        }
    }

    /// <summary>
    /// Play death animation on an NPC (CharacterModes.Dead).
    /// </summary>
    public void PlayDeathAnimation(SimulatedNpc npc)
    {
        try
        {
            if (npc.BattleChara == null)
                return;

            var character = (Character*)npc.BattleChara;
            character->SetMode(CharacterModes.Dead, 0);

            log.Verbose($"Death animation triggered for NPC '{npc.Name}'.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to play death animation for {npc.Name}.");
        }
    }

    /// <summary>
    /// Execute the death command on the player character (e.g., /playdead).
    /// </summary>
    public void PlayPlayerDeath()
    {
        var command = config.PlayerDeathCommand;
        if (!string.IsNullOrWhiteSpace(command))
        {
            commandExecutor.ExecuteCommand(command);
            log.Info($"Player death command executed: {command}");
        }
    }

    /// <summary>
    /// Execute victory animation/command.
    /// </summary>
    public void PlayVictory(bool isPlayerVictory)
    {
        if (isPlayerVictory)
        {
            var command = config.PlayerVictoryCommand;
            if (!string.IsNullOrWhiteSpace(command))
            {
                commandExecutor.ExecuteCommand(command);
                log.Info($"Player victory command executed: {command}");
            }
        }
        else
        {
            var command = config.TargetVictoryCommand;
            if (!string.IsNullOrWhiteSpace(command))
            {
                commandExecutor.ExecuteCommand(command);
                log.Info($"Target victory command executed: {command}");
            }
        }
    }

    /// <summary>
    /// Play NPC auto-attack with full animation + VFX pipeline.
    /// </summary>
    public void PlayNpcAutoAttack(SimulatedNpc npc, ulong targetId, int damage)
    {
        PlayActionEffect(new ActionEffectRequest
        {
            SourceEntityId = npc.SimulatedEntityId,
            SourcePosition = GetNpcPosition(npc),
            ActionId = 7, // Auto-attack
            AnimationLock = 0.6f,
            SourceRotation = GetNpcRotation(npc),
            IsSourcePlayer = false,
            IsRanged = false,
            Targets =
            {
                new TargetEffect
                {
                    TargetId = targetId,
                    Damage = damage,
                    DamageType = SimDamageType.Physical,
                }
            }
        });
    }

    private Character* FindCharacter(uint entityId, bool isPlayer)
    {
        if (isPlayer)
        {
            var player = clientState.LocalPlayer;
            if (player != null)
                return (Character*)player.Address;
        }
        else
        {
            foreach (var obj in Core.Services.ObjectTable)
            {
                if (obj.EntityId == entityId)
                    return (Character*)obj.Address;
            }
        }
        return null;
    }

    private Vector3 GetNpcPosition(SimulatedNpc npc)
    {
        if (npc.BattleChara != null)
        {
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
            return go->Position;
        }
        return Vector3.Zero;
    }

    private float GetNpcRotation(SimulatedNpc npc)
    {
        if (npc.BattleChara != null)
        {
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
            return go->Rotation;
        }
        return 0;
    }

    private static ushort QuantizeRotation(float radians)
    {
        // Game maps: 0 -> -pi, 65535 -> pi
        float normalized = (radians + MathF.PI) / (2f * MathF.PI);
        normalized = ((normalized % 1f) + 1f) % 1f; // Wrap to [0, 1)
        return (ushort)(normalized * 65535f);
    }

    public void Dispose()
    {
    }
}
