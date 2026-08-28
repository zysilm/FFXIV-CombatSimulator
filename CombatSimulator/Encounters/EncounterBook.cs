// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSimulator.Encounters.Definitions;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Encounters;

/// <summary>Loads and validates the strict, declarative encounter resources.</summary>
public sealed class EncounterBook
{
    private const string ResourcePrefix = "CombatSimulator.Resources.Encounters.";
    private readonly IPluginLog log;
    private List<EncounterDefinition>? encounters;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public EncounterBook(IPluginLog log)
    {
        this.log = log;
    }

    public IReadOnlyList<EncounterDefinition> Encounters => encounters ??= Load();

    public EncounterDefinition? Find(string id)
    {
        foreach (var encounter in Encounters)
            if (string.Equals(encounter.Id, id, StringComparison.OrdinalIgnoreCase))
                return encounter;
        return null;
    }

    private List<EncounterDefinition> Load()
    {
        var loaded = new List<EncounterDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                var definition = JsonSerializer.Deserialize<EncounterDefinition>(reader.ReadToEnd(), JsonOptions)
                                 ?? throw new InvalidDataException("Resource deserialized to null.");
                Validate(definition, resourceName);
                if (!ids.Add(definition.Id))
                    throw new InvalidDataException($"Duplicate encounter id '{definition.Id}'.");
                loaded.Add(definition);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to load encounter resource '{resourceName}'.");
            }
        }

        loaded.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        log.Info($"Loaded {loaded.Count} encounter definition(s).");
        return loaded;
    }

    private static void Validate(EncounterDefinition encounter, string resourceName)
    {
        if (encounter.SchemaVersion != 1)
            throw new InvalidDataException($"{resourceName}: unsupported schemaVersion {encounter.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(encounter.Id) || string.IsNullOrWhiteSpace(encounter.Name))
            throw new InvalidDataException($"{resourceName}: id and name are required.");
        if (string.IsNullOrWhiteSpace(encounter.Setup.Recipe))
            throw new InvalidDataException($"{resourceName}: setup.recipe is required.");
        if (encounter.Phases.Count == 0)
            throw new InvalidDataException($"{resourceName}: at least one phase is required.");

        var phases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in encounter.Phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Id) || !phases.Add(phase.Id))
                throw new InvalidDataException($"{resourceName}: phase ids must be non-empty and unique.");
        }
        if (string.IsNullOrWhiteSpace(encounter.InitialPhase) || !phases.Contains(encounter.InitialPhase))
            throw new InvalidDataException($"{resourceName}: initialPhase does not name a phase.");

        foreach (var phase in encounter.Phases)
        {
            ValidateCues(encounter, phase.OnEnter, resourceName, phase.Id);
            ValidateCues(encounter, phase.OnExit, resourceName, phase.Id);
            foreach (var transition in phase.Transitions)
            {
                if (transition.When.Count == 0)
                    throw new InvalidDataException($"{resourceName}: phase '{phase.Id}' has an empty transition.");
                if (transition.To is not "$complete" and not "$failed" && !phases.Contains(transition.To))
                    throw new InvalidDataException(
                        $"{resourceName}: phase '{phase.Id}' targets unknown phase '{transition.To}'.");
                foreach (var condition in transition.When)
                {
                    if (condition.Type is EncounterConditionType.ActorDead or EncounterConditionType.ActorHpAtOrBelow)
                        ValidateActor(encounter, condition.Actor, resourceName, phase.Id);
                    if (condition.Type == EncounterConditionType.ActorHpAtOrBelow &&
                        (condition.Ratio < 0f || condition.Ratio > 1f))
                        throw new InvalidDataException(
                            $"{resourceName}: phase '{phase.Id}' has an HP ratio outside 0..1.");
                }
            }
        }
    }

    private static void ValidateCues(
        EncounterDefinition encounter,
        IReadOnlyList<EncounterCueDefinition> cues,
        string resource,
        string phase)
    {
        foreach (var cue in cues)
        {
            if (cue.AtSeconds < 0f || cue.Duration < 0f)
                throw new InvalidDataException($"{resource}: phase '{phase}' has a cue with negative timing.");
            if (cue.Type is EncounterCueType.CameraFocus or EncounterCueType.EnemyPressure)
                ValidateActor(encounter, cue.Actor, resource, phase);
            if (cue.Type == EncounterCueType.SpawnEnemies && cue.Enemies.Count == 0)
                throw new InvalidDataException($"{resource}: phase '{phase}' has an empty spawnEnemies cue.");
            if (cue.Type == EncounterCueType.SpawnEnemies)
            {
                var total = 0;
                foreach (var group in cue.Enemies)
                {
                    if (group.Count is < 0 or > 50)
                        throw new InvalidDataException(
                            $"{resource}: phase '{phase}' has a spawn count outside 0..50.");
                    total += group.Count;
                }
                if (total > 150)
                    throw new InvalidDataException(
                        $"{resource}: phase '{phase}' requests more than 150 spawned enemies.");
            }
        }
    }

    private static void ValidateActor(
        EncounterDefinition encounter,
        string actor,
        string resource,
        string phase)
    {
        if (string.IsNullOrWhiteSpace(actor) || !encounter.Actors.ContainsKey(actor))
            throw new InvalidDataException(
                $"{resource}: phase '{phase}' references unknown actor alias '{actor}'.");
    }
}
