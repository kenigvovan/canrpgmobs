using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Behaviors;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Brain
{
    // Params feed the validator and the editor
    public interface IMobExtension
    {
        ParamInfo[] Params { get; }
    }

    // runs for every rule on every evaluation, keep it allocation-free
    public interface IMobCondition : IMobExtension
    {
        bool Test(in BrainContext ctx, SkillConditionModel condition);

        // false = tested before target selection, with no target
        bool NeedsTarget => true;
    }

    public interface ITargetSelector : IMobExtension
    {
        // friendly targets skip the line of sight check
        bool TargetsAllies { get; }
        Entity? Select(in BrainContext ctx, SkillRuleModel rule);
    }

    public readonly struct EnemyPickContext
    {
        public EnemyPickContext(EBRpgMob mob, float range, double keepRange)
        {
            Mob = mob;
            Range = range;
            KeepRange = keepRange;
        }

        public EBRpgMob Mob { get; }
        public Entity Self => Mob.entity;
        public float Range { get; }
        // current enemy is dropped beyond this
        public double KeepRange { get; }
    }

    // aiProfile.targeting.selector, candidates already pass EBRpgMob.IsEnemy
    public interface IEnemySelector : IMobExtension
    {
        Entity? Pick(in EnemyPickContext ctx);

        // keep the current enemy unless a new one has clearly more threat
        bool StickyByThreat => false;
    }

    // server only
    public interface IMobAction : IMobExtension
    {
        // null = can run, else a constant reason for the brain trace. Runs every evaluation
        string? Refusal(in MobActionContext ctx, SkillActionModel action);
        void Run(in MobActionContext ctx, SkillActionModel action);
    }

    // for phases Target is the current enemy, or the mob itself when calm
    public readonly struct MobActionContext
    {
        public MobActionContext(EntityAgent self, EBRpgMob mob, MobCatalog catalog, Entity target)
        {
            Self = self;
            Mob = mob;
            Catalog = catalog;
            Target = target;
        }

        public EntityAgent Self { get; }
        public EBRpgMob Mob { get; }
        public MobCatalog Catalog { get; }
        public Entity Target { get; }
    }

    public interface IAiArchetype : IMobExtension
    {
        void Build(ResolvedMob mob, AiBuildContext ctx, List<JObject> tasks);
    }

    public sealed class AiBuildContext
    {
        public AiBuildContext(int brainEvalMs, MobStats stats, EntityAgent entity, MobCatalog? catalog)
        {
            BrainEvalMs = brainEvalMs;
            Stats = stats;
            Entity = entity;
            Catalog = catalog;
        }

        public int BrainEvalMs { get; }
        // scaled, task melee damage comes from here
        public MobStats Stats { get; }
        public EntityAgent Entity { get; }
        public ICoreAPI Api => Entity.Api;
        // null only if the mod system is gone
        public MobCatalog? Catalog { get; }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class MobExtensionAttribute : Attribute
    {
        protected MobExtensionAttribute(string id, string help)
        {
            Id = id;
            Help = help;
        }

        public string Id { get; }
        public string Help { get; }
    }

    // scanned in every mod that references canrpgmobs, needs a parameterless constructor
    public sealed class MobConditionTypeAttribute : MobExtensionAttribute
    {
        public MobConditionTypeAttribute(string id, string help = "") : base(id, help) { }
    }

    public sealed class MobTargetSelectorAttribute : MobExtensionAttribute
    {
        public MobTargetSelectorAttribute(string id, string help = "") : base(id, help) { }
    }

    public sealed class AiArchetypeAttribute : MobExtensionAttribute
    {
        public AiArchetypeAttribute(string id, string help = "") : base(id, help) { }
    }

    public sealed class MobActionTypeAttribute : MobExtensionAttribute
    {
        public MobActionTypeAttribute(string id, string help = "") : base(id, help) { }
    }

    public sealed class MobEnemySelectorAttribute : MobExtensionAttribute
    {
        public MobEnemySelectorAttribute(string id, string help = "") : base(id, help) { }
    }

    public sealed class ExtensionRegistry<T> where T : class, IMobExtension
    {
        private readonly string kind;
        private readonly Dictionary<string, (T implementation, VocabularyEntry entry)> items = new(StringComparer.Ordinal);
        private volatile Dictionary<string, T>? frozen;

        internal ExtensionRegistry(string kind) => this.kind = kind;

        // ignored after assets are loaded
        public bool Register(string id, T implementation, string help = "", ILogger? logger = null)
        {
            lock (items)
            {
                if (frozen != null)
                {
                    logger?.Warning("[canrpgmobs] {0} '{1}' was registered after assets loaded and is ignored - register in ModSystem.Start", kind, id);
                    return false;
                }
                if (items.TryGetValue(id, out var existing) && existing.implementation.GetType() != implementation.GetType())
                    logger?.Notification("[canrpgmobs] {0} '{1}': {2} replaces {3}", kind, id, implementation.GetType().FullName, existing.implementation.GetType().FullName);
                items[id] = (implementation, new VocabularyEntry(id, help, implementation.Params));
                return true;
            }
        }

        public T? Get(string? id)
        {
            if (id == null) return null;
            var snapshot = frozen;
            if (snapshot != null) return snapshot.TryGetValue(id, out var hit) ? hit : null;
            lock (items) return items.TryGetValue(id, out var entry) ? entry.implementation : null;
        }

        public IReadOnlyList<VocabularyEntry> Entries()
        {
            lock (items) return items.Values.Select(v => v.entry).ToList();
        }

        internal void Freeze()
        {
            lock (items) frozen ??= items.ToDictionary(p => p.Key, p => p.Value.implementation, StringComparer.Ordinal);
        }
    }

    // register in ModSystem.Start, frozen once assets load. Process-wide, keep entries stateless
    public static partial class MobsApi
    {
        public static ExtensionRegistry<IMobCondition> Conditions { get; } = new("condition");
        public static ExtensionRegistry<ITargetSelector> TargetSelectors { get; } = new("target selector");
        public static ExtensionRegistry<IAiArchetype> AiArchetypes { get; } = new("AI archetype");
        public static ExtensionRegistry<IMobAction> Actions { get; } = new("mob action");
        public static ExtensionRegistry<IEnemySelector> EnemySelectors { get; } = new("enemy selector");

        private static readonly object gate = new();
        private static bool scanned;
        private static volatile RuleVocabulary? frozenVocabulary;

        public static RuleVocabulary Vocabulary => frozenVocabulary ?? Snapshot();

        // once per process, singleplayer runs both sides
        internal static void Scan(ICoreAPI api, ILogger logger)
        {
            lock (gate)
            {
                if (scanned) return;
                scanned = true;
            }

            foreach (var assembly in ScanTargets(api, logger))
            {
                foreach (var type in Types(assembly, logger))
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    TryRegister(type, Conditions, type.GetCustomAttribute<MobConditionTypeAttribute>(false), logger);
                    TryRegister(type, TargetSelectors, type.GetCustomAttribute<MobTargetSelectorAttribute>(false), logger);
                    TryRegister(type, AiArchetypes, type.GetCustomAttribute<AiArchetypeAttribute>(false), logger);
                    TryRegister(type, Actions, type.GetCustomAttribute<MobActionTypeAttribute>(false), logger);
                    TryRegister(type, EnemySelectors, type.GetCustomAttribute<MobEnemySelectorAttribute>(false), logger);
                }
            }

            logger.Notification("[canrpgmobs] brain extensions: {0} condition(s), {1} target selector(s), {2} AI archetype(s), {3} action(s), {4} enemy selector(s)",
                Conditions.Entries().Count, TargetSelectors.Entries().Count, AiArchetypes.Entries().Count, Actions.Entries().Count, EnemySelectors.Entries().Count);
        }

        internal static void Freeze()
        {
            lock (gate)
            {
                if (frozenVocabulary != null) return;
                Conditions.Freeze();
                TargetSelectors.Freeze();
                AiArchetypes.Freeze();
                Actions.Freeze();
                EnemySelectors.Freeze();
                frozenVocabulary = Snapshot();
            }
        }

        private static RuleVocabulary Snapshot()
            => new(Conditions.Entries(), TargetSelectors.Entries(), AiArchetypes.Entries(), Actions.Entries(), EnemySelectors.Entries());

        private static void TryRegister<T>(Type type, ExtensionRegistry<T> registry, MobExtensionAttribute? attribute, ILogger logger)
            where T : class, IMobExtension
        {
            if (attribute == null) return;
            if (!typeof(T).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
            {
                logger.Warning("[canrpgmobs] {0} has [{1}] but is not a {2} with a parameterless constructor, skipped",
                    type.FullName, attribute.GetType().Name, typeof(T).Name);
                return;
            }
            try { registry.Register(attribute.Id, (T)Activator.CreateInstance(type)!, attribute.Help, logger); }
            catch (Exception e) { logger.Warning("[canrpgmobs] could not create {0}: {1}", type.FullName, e.InnerException?.Message ?? e.Message); }
        }

        // our assembly + mod assemblies referencing it (same as canrpgclasses ModExtensions)
        private static List<Assembly> ScanTargets(ICoreAPI api, ILogger logger)
        {
            var self = typeof(MobsApi).Assembly;
            var list = new List<Assembly> { self };
            if (api.ModLoader == null) return list;

            string selfName = self.GetName().Name ?? "";
            var seen = new HashSet<Assembly> { self };
            foreach (var mod in api.ModLoader.Mods)
            {
                foreach (var system in mod.Systems)
                {
                    var assembly = system.GetType().Assembly;
                    if (!seen.Add(assembly) || !References(assembly, selfName)) continue;
                    list.Add(assembly);
                    logger.Notification("[canrpgmobs] scanning extension assembly {0} (mod {1})", assembly.GetName().Name, mod.Info?.ModID ?? "?");
                }
            }
            return list;
        }

        private static IEnumerable<Type> Types(Assembly assembly, ILogger logger)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                logger.Warning("[canrpgmobs] {0}: some types could not be loaded, scanning the rest", assembly.GetName().Name);
                return e.Types.Where(t => t != null).Cast<Type>().ToList();
            }
            catch (Exception e)
            {
                logger.Warning("[canrpgmobs] could not scan {0}: {1}", assembly.GetName().Name, e.Message);
                return Array.Empty<Type>();
            }
        }

        private static bool References(Assembly assembly, string selfName)
        {
            if (selfName.Length == 0) return false;
            try
            {
                foreach (var reference in assembly.GetReferencedAssemblies())
                    if (string.Equals(reference.Name, selfName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* dynamic or unreadable assembly */ }
            return false;
        }
    }
}
