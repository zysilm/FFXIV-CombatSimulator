// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using AdvancedRagdoll.Hosting;
using CombatSimulator;
using CombatSimulator.Animation.Hosting;
using Newtonsoft.Json;

namespace RagdollReplay;

/// <summary>
/// RagdollReplay --config-migration [saved config.json]
/// The "Death guidance" setting (docs/RAGDOLL_DEATH_GUIDANCE.md) from a saved configuration: the plugin's real
/// Configuration, read the way Dalamud reads it, run through Initialize (no plugin interface: nothing is saved),
/// and handed to the library by the plugin's settings source. Lives here because this is the test project that
/// loads the built plugin. With a file (the user's saved configuration; only read), the same on that file.
/// </summary>
internal static class ConfigMigrationCheck
{
    // Dalamud's PluginConfigurations reads and writes a plugin's configuration with these.
    private static readonly JsonSerializerSettings DalamudSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    // An existing install: its one-time ragdoll page reset (MigrateRagdollLegModelRebuild) has already run.
    private const string Head = "{ \"$type\": \"CombatSimulator.Configuration, CombatSimulator\", \"Version\": 1, \"RagdollLegModelRebuildMigrated20260816\": true";
    private const string OldObject = "\"GuidedCollapse\": { \"$type\": \"CombatSimulator.GuidedCollapseSettings, CombatSimulator\", \"Enabled\": ";

    public static int Run(string[] args)
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
        }

        var on = Load(Head + ", " + OldObject + "true } }");
        Check("saved Knee buckle on death = on -> Knee buckle", on.RagdollDeathGuidance == RagdollDeathGuidance.KneeBuckle, on.RagdollDeathGuidance.ToString());
        Check("the old key is gone after the migration", on.PrivateConfigFields?.ContainsKey("GuidedCollapse") != true);
        var resaved = JsonConvert.SerializeObject(on, Formatting.Indented, DalamudSettings);
        Check("saved again: no GuidedCollapse, the choice as a plain number",
            !resaved.Contains("GuidedCollapse", StringComparison.Ordinal) && resaved.Contains("\"RagdollDeathGuidance\": 1", StringComparison.Ordinal));
        Check("read again: still Knee buckle", Load(resaved).RagdollDeathGuidance == RagdollDeathGuidance.KneeBuckle);

        var off = Load(Head + ", " + OldObject + "false } }");
        Check("saved Knee buckle on death = off -> Limp", off.RagdollDeathGuidance == RagdollDeathGuidance.Limp, off.RagdollDeathGuidance.ToString());
        Check("an old object without the switch -> Limp",
            Load(Head + ", \"GuidedCollapse\": { } }").RagdollDeathGuidance == RagdollDeathGuidance.Limp);
        Check("a new install -> Limp", Load("{ \"$type\": \"CombatSimulator.Configuration, CombatSimulator\" }").RagdollDeathGuidance == RagdollDeathGuidance.Limp);
        Check("a saved choice is kept (Auto)", Load(Head + ", \"RagdollDeathGuidance\": 5 }").RagdollDeathGuidance == RagdollDeathGuidance.Auto);
        Check("a saved choice is kept (Hold the legs)", Load(Head + ", \"RagdollDeathGuidance\": 2 }").RagdollDeathGuidance == RagdollDeathGuidance.HoldLegs);
        Check("a number that is no choice -> Limp", Load(Head + ", \"RagdollDeathGuidance\": 99 }").RagdollDeathGuidance == RagdollDeathGuidance.Limp);

        var reset = Load(Head + ", \"RagdollDeathGuidance\": 5 }");
        reset.ResetRagdollPageDefaults();
        Check("Reset All to Defaults (Ragdoll tab) -> Limp", reset.RagdollDeathGuidance == RagdollDeathGuidance.Limp);

        // What the library is handed: the profile, and its own knee-buckle switch always off.
        var expected = new (RagdollDeathGuidance Choice, RagdollTensionProfile Profile)[]
        {
            (RagdollDeathGuidance.Limp, RagdollTensionProfile.None), (RagdollDeathGuidance.KneeBuckle, RagdollTensionProfile.KneeBuckle),
            (RagdollDeathGuidance.HoldLegs, RagdollTensionProfile.HoldLegs), (RagdollDeathGuidance.HoldPose, RagdollTensionProfile.HoldPose),
            (RagdollDeathGuidance.SlowSlump, RagdollTensionProfile.SlowSlump), (RagdollDeathGuidance.Auto, RagdollTensionProfile.Auto),
        };
        var config = Load(Head + " }");
        var source = new ConfigRagdollSettings(config);
        var host = new HostSettings();
        source.Refresh(host);
        foreach (var (choice, profile) in expected)
        {
            config.RagdollDeathGuidance = choice;
            var changed = source.Refresh(host);
            var again = source.Refresh(host);
            Check($"{choice} reaches the library as {profile}",
                host.GuidedCollapse.TensionProfile == profile && !host.GuidedCollapse.Enabled
                && changed == (choice != RagdollDeathGuidance.Limp) && !again,
                $"profile {host.GuidedCollapse.TensionProfile}, Enabled {host.GuidedCollapse.Enabled}, changed {changed}, again {again}");
        }
        Check("the dropdown lists every choice once",
            RagdollDeathGuidances.DropdownOrder.Length == expected.Length && RagdollDeathGuidances.DropdownLabels.Length == expected.Length
            && RagdollDeathGuidances.DropdownHelp.Length == expected.Length
            && expected.All(e => RagdollDeathGuidances.DropdownOrder.Count(c => c == e.Choice) == 1));

        var at = Array.IndexOf(args, "--config-migration");
        var file = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (file != null)
        {
            var text = File.ReadAllText(file);
            var had = Newtonsoft.Json.Linq.JObject.Parse(text)["GuidedCollapse"]?.ToString(Formatting.None) ?? "(absent)";
            var saved = Load(text);
            host = new HostSettings();
            new ConfigRagdollSettings(saved).Refresh(host);
            Console.WriteLine($"{Path.GetFileName(file)}: GuidedCollapse {had} -> RagdollDeathGuidance {saved.RagdollDeathGuidance}, " +
                              $"library profile {host.GuidedCollapse.TensionProfile}, Enabled {host.GuidedCollapse.Enabled}; " +
                              $"old key left: {saved.PrivateConfigFields?.ContainsKey("GuidedCollapse") == true}");
        }

        Console.WriteLine(failures == 0 ? "== config migration: all passed" : $"== config migration: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static Configuration Load(string json)
    {
        var config = (Configuration)JsonConvert.DeserializeObject<Dalamud.Configuration.IPluginConfiguration>(json, DalamudSettings)!;
        config.Initialize(null!);
        return config;
    }
}
