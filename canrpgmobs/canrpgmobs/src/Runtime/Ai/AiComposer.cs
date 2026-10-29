using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Ai
{
    // Builds AI tasks at runtime from the archetype instead of a fixed aitasks list in JSON.
    public static class AiComposer
    {
        // returns the config hash
        public static string Compose(EntityAgent agent, ResolvedMob mob, MobStats stats)
        {
            var taskAi = agent.GetBehavior<EntityBehaviorTaskAI>();
            if (taskAi == null)
            {
                agent.Api.Logger.Warning("[canrpgmobs] {0} has no taskai behavior - no AI built", agent.Code);
                return "";
            }
            var specs = BuildSpecs(agent, mob, stats);
            if (specs == null) return "";

            var aiConfig = new JsonObject(new JObject());
            foreach (var spec in specs)
            {
                string? code = spec["code"]?.Type == JTokenType.String ? (string?)spec["code"] : null;
                if (code == null || !AiTaskRegistry.TaskTypes.TryGetValue(code, out var type))
                {
                    agent.Api.Logger.Warning("[canrpgmobs] unknown AI task '{0}' for {1}", code ?? "(no code)", mob.Key);
                    continue;
                }
                try
                {
                    var task = (IAiTask)Activator.CreateInstance(type, agent, new JsonObject(spec), aiConfig)!;
                    taskAi.TaskManager.AddTask(task);
                }
                catch (Exception e)
                {
                    agent.Api.Logger.Warning("[canrpgmobs] AI task '{0}' for {1} could not be built: {2}", code, mob.Key, e.InnerException?.Message ?? e.Message);
                }
            }
            return Hash(specs, mob);
        }

        // includes anims, tasks keep the anims they were built with
        public static string SpecHash(EntityAgent agent, ResolvedMob mob, MobStats stats)
        {
            var specs = BuildSpecs(agent, mob, stats);
            return specs == null ? "" : Hash(specs, mob);
        }

        // running casts get cancelled in FinishExecute
        public static string Rebuild(EntityAgent agent, ResolvedMob mob, MobStats stats)
        {
            var manager = agent.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
            if (manager == null) return "";
            manager.StopTasks();
            var oldBrain = manager.GetTask<AiTaskRpgCast>();
            foreach (var task in manager.AllTasks.ToList()) manager.RemoveTask(task);

            string hash = Compose(agent, mob, stats);
            foreach (var task in manager.AllTasks) task.AfterInitialize();
            // keep cooldowns across rebuilds
            if (oldBrain != null) manager.GetTask<AiTaskRpgCast>()?.CopyStateFrom(oldBrain);
            return hash;
        }

        private static List<JObject>? BuildSpecs(EntityAgent agent, ResolvedMob mob, MobStats stats)
        {
            var archetype = MobsApi.AiArchetypes.Get(mob.Ai.archetype) ?? MobsApi.AiArchetypes.Get(MobValidator.DefaultArchetype);
            if (archetype == null)
            {
                agent.Api.Logger.Warning("[canrpgmobs] no AI archetypes are registered - {0} gets no AI", mob.Key);
                return null;
            }
            int evalMs = canrpgmobsModSystem.For(agent.Api)?.Settings.brainEvalMs ?? 300;
            var specs = new List<JObject>();
            archetype.Build(mob, new AiBuildContext(evalMs, stats, agent, canrpgmobsModSystem.For(agent.Api)?.Catalog), specs);
            return specs;
        }

        private static string Hash(List<JObject> specs, ResolvedMob mob)
        {
            var text = new StringBuilder();
            foreach (var spec in specs) text.Append(spec.ToString(Formatting.None)).Append('\n');
            foreach (var pair in mob.Anims.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                text.Append(pair.Key).Append('=').Append(JsonConvert.SerializeObject(pair.Value)).Append('\n');

            return StableHash.Of(text.ToString()).ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
