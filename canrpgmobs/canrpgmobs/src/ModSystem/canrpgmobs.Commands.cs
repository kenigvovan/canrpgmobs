using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canrpgclasses.Core.EB;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Ai;
using canrpgmobs.Runtime.Factions;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs
{
    public partial class canrpgmobsModSystem
    {
        private ICoreServerAPI? sapi;

        private void RegisterCommands(ICoreServerAPI api)
        {
            sapi = api;
            var parsers = api.ChatCommands.Parsers;
            var root = api.ChatCommands.Create("canrpgmobs")
                .WithDescription("canrpgmobs admin tools")
                .RequiresPrivilege(Privilege.controlserver);
            AddMobCommands(root, parsers);
            AddSpawnerCommands(root, parsers);
            AddZoneCommands(root, parsers);
            AddToolCommands(root, parsers);
        }

        private void AddMobCommands(IChatCommand root, CommandArgumentParsers parsers)
        {
            root.BeginSubCommand("spawn")
                    .WithDescription("Spawn mobs 3 blocks ahead: spawn &lt;template|race|role&gt; [level] [tier] [count], e.g. goblin|shaman 3 elite 2. Level 0 = template range, tier - = default")
                    .RequiresPlayer()
                    .WithArgs(parsers.Word("mobKey"), parsers.OptionalInt("level", 0), parsers.OptionalWord("tier"), parsers.OptionalInt("count", 1))
                    .HandleWith(OnSpawn)
                .EndSubCommand()
                .BeginSubCommand("inspect")
                    .WithDescription("Show the looked-at (or nearest) mob's archetype, AI and cast state")
                    .RequiresPlayer()
                    .HandleWith(OnInspect)
                .EndSubCommand()
                .BeginSubCommand("brain")
                    .WithDescription("Print the skill decisions of the looked-at (or nearest) mob to your chat, or stop with off")
                    .RequiresPlayer()
                    .WithArgs(parsers.OptionalWordRange("mode", "on", "off"))
                    .HandleWith(OnBrain)
                .EndSubCommand()
                .BeginSubCommand("kill")
                    .WithDescription("Remove mobs around you")
                    .RequiresPlayer()
                    .WithArgs(parsers.OptionalInt("radius", 32))
                    .HandleWith(OnKill)
                .EndSubCommand()
                .BeginSubCommand("list")
                    .WithDescription("List races, roles, mob templates and aliases")
                    .HandleWith(OnList)
                .EndSubCommand()
                .BeginSubCommand("loot")
                    .WithDescription("Roll a loot table many times and show how often each item drops: loot &lt;table&gt; [level] [tier] [rolls]")
                    .WithArgs(parsers.Word("table"), parsers.OptionalInt("level", 1), parsers.OptionalWord("tier"), parsers.OptionalInt("rolls", 1000))
                    .HandleWith(OnLoot)
                .EndSubCommand()
                .BeginSubCommand("faction")
                    .WithDescription("Show how two factions treat each other, e.g. faction greenskins players")
                    .WithArgs(parsers.Word("factionA"), parsers.Word("factionB"))
                    .HandleWith(OnFaction)
                .EndSubCommand()
                .BeginSubCommand("validate")
                    .WithDescription("Show problems found in the mob content files")
                    .HandleWith(OnValidate)
                .EndSubCommand()
                .BeginSubCommand("reload")
                    .WithDescription("Re-read config/mobs files from disk and rebuild the catalog")
                    .HandleWith(OnReload)
                .EndSubCommand()
                .BeginSubCommand("import")
                    .WithDescription("Merge a mobs JSON file from ModConfig/canrpgmobs-import into the in-game content: import &lt;file.json&gt;")
                    .WithArgs(parsers.Word("file"))
                    .HandleWith(OnImport)
                .EndSubCommand()
                .BeginSubCommand("spawngroup")
                    .WithDescription("Spawn a mob group 4 blocks ahead: spawngroup &lt;group&gt; [level], level 0 = each member's own")
                    .RequiresPlayer()
                    .WithArgs(parsers.Word("group"), parsers.OptionalInt("level", 0))
                    .HandleWith(OnSpawnGroup)
                .EndSubCommand();
        }

        private void AddSpawnerCommands(IChatCommand root, CommandArgumentParsers parsers)
        {
            root.BeginSubCommand("spawner")
                    .WithDescription("Configure the mob spawner block you look at")
                    .BeginSubCommand("set")
                        .WithDescription("spawner set &lt;race|role|template|group:id|zone&gt; [maxAlive] [respawnSec] [radius] [activationRange] [level] [tier]; level 0 = template range")
                        .RequiresPlayer()
                        .WithArgs(parsers.Word("what"), parsers.OptionalInt("maxAlive", 3), parsers.OptionalInt("respawnSec", 60), parsers.OptionalInt("radius", 4),
                            parsers.OptionalInt("activationRange", 32), parsers.OptionalInt("level", 0), parsers.OptionalWord("tier"))
                        .HandleWith(OnSpawnerSet)
                    .EndSubCommand()
                    .BeginSubCommand("info")
                        .WithDescription("Show the spawner's settings and how many of its mobs are alive")
                        .RequiresPlayer()
                        .HandleWith(OnSpawnerInfo)
                    .EndSubCommand()
                    .BeginSubCommand("clear")
                        .WithDescription("Remove the spawner's mobs and turn it off")
                        .RequiresPlayer()
                        .HandleWith(OnSpawnerClear)
                    .EndSubCommand()
                .EndSubCommand();
        }

        private void AddZoneCommands(IChatCommand root, CommandArgumentParsers parsers)
        {
            root.BeginSubCommand("zone")
                    .WithDescription("Mob zones")
                    .BeginSubCommand("here")
                        .WithDescription("Which zone you stand in and what spawns there")
                        .RequiresPlayer()
                        .HandleWith(OnZoneHere)
                    .EndSubCommand()
                    .BeginSubCommand("list")
                        .WithDescription("All zones")
                        .HandleWith(OnZoneList)
                    .EndSubCommand()
                    .BeginSubCommand("info")
                        .WithDescription("Settings and problems of a zone: info &lt;zone&gt;")
                        .WithArgs(parsers.Word("zone"))
                        .HandleWith(OnZoneInfo)
                    .EndSubCommand()
                    .BeginSubCommand("check")
                        .WithDescription("Problems found in zones")
                        .HandleWith(OnZoneCheck)
                    .EndSubCommand()
                    .BeginSubCommand("tp")
                        .WithDescription("Teleport into a zone: tp &lt;zone&gt; [corner], 0 = the middle")
                        .RequiresPlayer()
                        .WithArgs(parsers.Word("zone"), parsers.OptionalInt("corner", 0))
                        .HandleWith(OnZoneTp)
                    .EndSubCommand()
                    .BeginSubCommand("stats")
                        .WithDescription("Alive, spawned, killed and refused spawns of a zone: stats &lt;zone&gt;")
                        .WithArgs(parsers.Word("zone"))
                        .HandleWith(OnZoneStats)
                    .EndSubCommand()
                    .BeginSubCommand("fill")
                        .WithDescription("Spawn a zone's mobs around you up to its cap now: fill &lt;zone&gt;")
                        .RequiresPlayer()
                        .WithArgs(parsers.Word("zone"))
                        .HandleWith(OnZoneFill)
                    .EndSubCommand()
                    .BeginSubCommand("clear")
                        .WithDescription("Remove a zone's loaded mobs: clear &lt;zone&gt;")
                        .WithArgs(parsers.Word("zone"))
                        .HandleWith(OnZoneClear)
                    .EndSubCommand()
                    .BeginSubCommand("simulate")
                        .WithDescription("What a zone would spawn and how many spots around you work, nothing spawned: simulate &lt;zone&gt; [picks]")
                        .RequiresPlayer()
                        .WithArgs(parsers.Word("zone"), parsers.OptionalInt("picks", 1000))
                        .HandleWith(OnZoneSimulate)
                    .EndSubCommand()
                    .BeginSubCommand("export")
                        .WithDescription("Write a zone to ModConfig/canrpgmobs-export as a mobs file: export &lt;zone&gt;")
                        .WithArgs(parsers.Word("zone"))
                        .HandleWith(OnZoneExport)
                    .EndSubCommand()
                    .BeginSubCommand("goto")
                        .WithDescription("Teleport to the ground at absolute coordinates: goto &lt;x&gt; &lt;z&gt;")
                        .RequiresPlayer()
                        .WithArgs(parsers.Int("x"), parsers.Int("z"))
                        .HandleWith(OnZoneGoto)
                    .EndSubCommand()
                    .BeginSubCommand("wand")
                        .WithDescription("Give yourself the zone wand")
                        .RequiresPlayer()
                        .HandleWith(OnZoneWand)
                    .EndSubCommand()
                .EndSubCommand();
        }

        // lang export and root-only dev tools
        private void AddToolCommands(IChatCommand root, CommandArgumentParsers parsers)
        {
            root.BeginSubCommand("lang")
                    .WithDescription("Translations of mob, race, role and zone names")
                    .BeginSubCommand("export")
                        .WithDescription("Write a lang file of all names to ModConfig/canrpgmobs-export/lang: export &lt;language&gt;")
                        .WithArgs(parsers.Word("language"))
                        .HandleWith(OnLangExport)
                    .EndSubCommand()
                .EndSubCommand()
                .BeginSubCommand("debug")
                    .WithDescription("Developer tools")
                    .RequiresPrivilege(Privilege.root)
                    .BeginSubCommand("audit")
                        .WithDescription("Look for mobs still tracked after they left the world")
                        .HandleWith(OnAudit)
                    .EndSubCommand()
                    .BeginSubCommand("perf")
                        .WithDescription("Measure the server tick time spent in mobs: perf [seconds]")
                        .WithArgs(parsers.OptionalInt("seconds", 10))
                        .HandleWith(OnPerf)
                    .EndSubCommand()
                    .BeginSubCommand("stress")
                        .WithDescription("Spawn two groups ahead and measure them fighting: stress [count] [keyA] [keyB] [seconds], default 60 goblin|brute human|brute 15")
                        .RequiresPlayer()
                        .WithArgs(parsers.OptionalInt("count", 60), parsers.OptionalWord("keyA"), parsers.OptionalWord("keyB"), parsers.OptionalInt("seconds", 15))
                        .HandleWith(OnStress)
                    .EndSubCommand()
                    .BeginSubCommand("selftest")
                        .WithDescription("Run the built-in data layer checks")
                        .HandleWith(OnSelfTest)
                    .EndSubCommand()
                .EndSubCommand();
        }

        private static TextCommandResult NoPlayer() => TextCommandResult.Error("No player entity.");

        // blank or "-" = default tier
        private static string? TierArg(object? arg) => arg is string text && !string.IsNullOrWhiteSpace(text) && text != "-" ? text : null;

        private static string JoinOrDash(IEnumerable<string> ids)
        {
            var sorted = ids.OrderBy(k => k).ToList();
            return sorted.Count == 0 ? "-" : string.Join(", ", sorted);
        }

        private TextCommandResult OnSpawn(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player?.Entity is not EntityPlayer player) return NoPlayer();

            string key = (string)args[0];
            int levelArg = (int)args[1];
            int? level = levelArg > 0 ? levelArg : null;
            string? tier = TierArg(args[2]);
            int count = GameMath.Clamp((int)args[3], 1, 20);

            int spawned = SpawnAhead(player, key, level, tier, count, out string? error);
            return spawned > 0
                ? TextCommandResult.Success($"Spawned {spawned} x {MobSpawner.NormalizeKey(key)}.")
                : TextCommandResult.Error($"Could not spawn '{key}': {error}");
        }

        // stops at the first failure
        private int SpawnAhead(EntityPlayer player, string key, int? level, string? tier, int count, out string? error)
        {
            float yaw = player.Pos.Yaw;
            Vec3d center = player.Pos.XYZ.AheadCopy(3.0, 0f, yaw);
            int spawned = 0;
            error = null;
            for (int i = 0; i < count; i++)
            {
                double side = (i - (count - 1) / 2.0) * 1.2;
                var pos = center.AddCopy(Math.Cos(yaw) * side, 0, -Math.Sin(yaw) * side);
                var request = new MobSpawnRequest(key, pos, level, yaw + GameMath.PI, Tier: tier);
                if (MobSpawner.TrySpawn(sapi!, request, out error) != null) spawned++;
                else break;
            }
            return spawned;
        }

        private TextCommandResult OnInspect(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player is not IPlayer caller || caller.Entity is not EntityPlayer player) return NoPlayer();

            var mob = LookedAtMob(caller, player);
            if (mob == null) return TextCommandResult.Error("No mob under the crosshair or within 8 blocks.");
            return TextCommandResult.Success(DescribeMob(mob));
        }

        // also used by the editor's test panel
        internal string DescribeMob(EntityRpgMob mob)
        {
            var bh = mob.GetBehavior<EBRpgMob>();
            var resolved = bh?.Mob;
            var health = mob.GetBehavior<EntityBehaviorHealth>();
            var caster = mob.GetBehavior<EBSpellCaster>();
            var tasks = mob.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
            var brain = tasks?.GetTask<AiTaskRpgCast>();

            var sb = new StringBuilder();
            sb.AppendLine($"{mob.GetName()} (entity {mob.EntityId}, dimension {mob.Pos.Dimension})");
            sb.AppendLine($"key: {bh?.MobKey ?? "-"}, level {bh?.Level ?? 0}, resolved: {(resolved != null ? "yes" : "NO")}");
            if (resolved != null)
            {
                sb.AppendLine($"race {resolved.Race.id}, role {resolved.Role.id}, template {resolved.Template?.id ?? "-"}, ai {resolved.Ai.id} ({resolved.Ai.archetype})");
                sb.AppendLine($"skills: {string.Join(", ", resolved.Skills.Select(s => s.id))}" + (resolved.SkippedSkills > 0 ? $" ({resolved.SkippedSkills} skipped, see validate)" : ""));
            }
            sb.AppendLine($"faction: {FactionService.FactionOf(mob, Catalog)}");
            if (bh != null)
            {
                var top = bh.TopThreat();
                sb.AppendLine($"fight: enemy {bh.CurrentEnemy?.GetName() ?? "-"}, {bh.CombatSeconds:0}s in combat"
                              + (bh.HoldPosition ? ", standing still to cast" : "") + (bh.IsFleeing ? ", fleeing" : ""));
                string home = bh.Anchor is { } anchor ? $"{mob.Pos.DistanceTo(anchor):0.#} blocks from home" : "no home";
                sb.AppendLine($"threat: {bh.ThreatCount} source(s), top {(top != null ? $"{top.GetName()} ({bh.ThreatOf(top):0.#})" : "-")}; {home}{(bh.IsLeashing ? ", walking home" : "")}");
            }
            if (bh?.ScaledStats is { } stats)
            {
                string resists = stats.Resists.Count == 0 ? "" : ", resist " + string.Join(" ", stats.Resists.Select(r => $"{r.Key} {r.Value:P0}"));
                string loot = resolved != null && resolved.LootTables.Count > 0 ? string.Join(", ", resolved.LootTables) : "-";
                sb.AppendLine($"scaled: level {stats.Level}, tier {stats.TierId}, melee {stats.MeleeDamage:0.#} tier {stats.MeleeTier}"
                              + (stats.Weapon != null ? $" from {stats.Weapon}" : "") + $", spell power x{stats.SpellPower:0.##}, "
                              + $"healing x{stats.HealingPower:0.##}, DR {stats.DamageReduction:P0}, takes x{stats.DamageTaken:0.##}{resists}; loot {loot}");
            }
            if (health != null) sb.AppendLine($"hp: {health.Health:0.#} / {health.MaxHealth:0.#}");
            if (tasks != null) sb.AppendLine($"active tasks: {DescribeActive(tasks)}");
            if (caster != null) sb.AppendLine($"casting: {caster.CurrentCastSpellId ?? "-"}, last core refusal: {caster.LastFailReason}");
            if (brain != null) sb.AppendLine($"brain: phase {brain.PhaseName}, last skill {brain.LastSkillId ?? "-"}, last refusal {brain.LastFailReason}");
            if (bh != null && resolved != null && (resolved.Phases.Count > 0 || bh.IsEnraged || bh.StolenCount > 0))
                sb.AppendLine($"boss: phase {bh.PhaseIndex}/{resolved.Phases.Count}{(bh.IsEnraged ? ", enraged" : "")}, summons alive {bh.AliveSummons()}, stolen stacks {bh.StolenCount}");
            return sb.ToString().TrimEnd();
        }

        private TextCommandResult OnBrain(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player is not IPlayer caller || caller.Entity is not EntityPlayer player) return NoPlayer();

            if ((args[0] as string) == "off")
                return TextCommandResult.Success($"Stopped tracing {BrainTrace.StopWatching(caller.PlayerUID)} mob(s).");

            var mob = LookedAtMob(caller, player);
            if (mob == null) return TextCommandResult.Error("No mob under the crosshair or within 8 blocks.");
            BrainTrace.Watch(mob.EntityId, caller.PlayerUID);
            return TextCommandResult.Success($"Tracing {mob.GetName()} ({mob.EntityId}): decisions and refusals go to your chat. /canrpgmobs brain off stops it.");
        }

        private EntityRpgMob? LookedAtMob(IPlayer caller, EntityPlayer player)
            => caller.CurrentEntitySelection?.Entity as EntityRpgMob
               ?? sapi!.World.GetNearestEntity(player.Pos.XYZ, 8f, 8f, e => e is EntityRpgMob) as EntityRpgMob;

        private TextCommandResult OnKill(TextCommandCallingArgs args)
        {
            if (sapi == null || args.Caller.Player?.Entity is not EntityPlayer player) return NoPlayer();

            float radius = GameMath.Clamp((int)args[0], 1, 128);
            return TextCommandResult.Success($"Removed {RemoveMobsAround(player, radius)} mob(s) within {radius} blocks.");
        }

        private int RemoveMobsAround(EntityPlayer player, float radius)
        {
            var mobs = sapi!.World.GetEntitiesAround(player.Pos.XYZ, radius, radius, e => e is EntityRpgMob);
            foreach (var mob in mobs) mob.Die(EnumDespawnReason.Removed);
            return mobs.Length;
        }

        private TextCommandResult OnList(TextCommandCallingArgs args)
        {
            string locale = Lang.CurrentLocale;
            var sb = new StringBuilder();
            sb.AppendLine("Races: " + string.Join(", ", Catalog.Races.Values.OrderBy(r => r.id).Select(r => $"{r.id} ({r.name?.Resolve(locale, r.id) ?? r.id})")));
            sb.AppendLine("Roles: " + string.Join(", ", Catalog.Roles.Keys.OrderBy(k => k)));
            sb.AppendLine("Templates: " + JoinOrDash(Catalog.Templates.Keys));
            sb.AppendLine("Factions: " + JoinOrDash(Catalog.Factions.Factions.Keys));
            sb.AppendLine("Tiers: " + JoinOrDash(Catalog.Tiers.Keys));
            sb.AppendLine("Loot tables: " + JoinOrDash(Catalog.LootTables.Keys));
            // chat is VTML, a raw < or > breaks the message
            sb.AppendLine("Aliases: " + (Catalog.Aliases.Count == 0 ? "-" : string.Join(", ", Catalog.Aliases.Select(a => $"{a.Key} -&gt; {a.Value}"))));
            sb.Append("Spawn with: /canrpgmobs spawn &lt;template|race|role&gt; [level] [tier] [count]");
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnLoot(TextCommandCallingArgs args)
        {
            string id = IdRules.Qualify((string)args[0], ModId);
            if (!Catalog.LootTables.ContainsKey(id))
                return TextCommandResult.Error($"Unknown loot table '{id}'. Known: {JoinOrDash(Catalog.LootTables.Keys)}");

            int level = Math.Max(1, (int)args[1]);
            TierModel? tier = null;
            if (TierArg(args[2]) is { } tierArg)
            {
                string tierId = IdRules.Qualify(tierArg, ModId);
                tier = Catalog.Tier(tierId);
                if (tier == null && tierId != StatMath.DefaultTier) return TextCommandResult.Error($"Unknown tier '{tierId}'.");
            }
            int kills = GameMath.Clamp((int)args[3], 1, 10000);

            var rng = new Random();
            var tableIds = new[] { id };
            var totals = new Dictionary<string, (long items, int kills)>();
            int empty = 0;
            for (int i = 0; i < kills; i++)
            {
                var drops = LootRoller.Roll(tableIds, Catalog.LootTables, level, tier, rng);
                if (drops.Count == 0) empty++;
                foreach (var drop in drops)
                {
                    totals.TryGetValue(drop.Code, out var total);
                    totals[drop.Code] = (total.items + drop.Count, total.kills + 1);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{kills} roll(s) of {id} at level {level}, tier {tier?.id ?? StatMath.DefaultTier}; nothing in {empty * 100.0 / kills:0.#}%:");
            foreach (var (code, total) in totals.OrderByDescending(p => p.Value.kills).Take(20))
                sb.AppendLine($"{code}: in {total.kills * 100.0 / kills:0.#}% of kills, {total.items / (double)kills:0.##} per kill");
            if (totals.Count > 20) sb.Append($"... and {totals.Count - 20} more");
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        private TextCommandResult OnFaction(TextCommandCallingArgs args)
        {
            string a = IdRules.Qualify((string)args[0], ModId);
            string b = IdRules.Qualify((string)args[1], ModId);
            var factions = Catalog.Factions;

            var sb = new StringBuilder();
            foreach (var id in new[] { a, b }.Distinct())
                if (!factions.Exists(id)) sb.AppendLine($"note: '{id}' is not a known faction, it is neutral to everyone");
            sb.AppendLine($"{a} and {b}: {factions.Relation(a, b).ToString().ToLowerInvariant()}");
            sb.AppendLine($"{a} attacks {b} on sight: {(factions.AttacksOnSight(a, b) ? "yes" : "no")}");
            sb.Append($"{b} attacks {a} on sight: {(factions.AttacksOnSight(b, a) ? "yes" : "no")}");
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnValidate(TextCommandCallingArgs args)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Mob content: {LastReport.Summary}.");
            AppendProblems(sb, 15);
            if (LastReport.ErrorCount + LastReport.WarningCount > 15) sb.AppendLine("The full list is in server-main.log.");
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        private TextCommandResult OnReload(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server only.");

            int assets = 0;
            try
            {
                foreach (var domain in CatalogLoader.Domains(sapi).ToList())
                    assets += sapi.Assets.Reload(new AssetLocation(domain, CatalogLoader.AssetPath));
            }
            catch (Exception e)
            {
                return TextCommandResult.Error("Asset reload failed, the old catalog stays: " + e.Message);
            }

            ReloadCatalog(sapi);
            BroadcastCatalog();
            var sb = new StringBuilder();
            sb.AppendLine($"Re-read {assets} file(s): {Catalog.Races.Count} race(s), {Catalog.Roles.Count} role(s), {Catalog.Templates.Count} template(s), "
                          + $"{Store.EntryCount} in-game entr{(Store.EntryCount == 1 ? "y" : "ies")}; {LastReport.Summary}.");
            AppendProblems(sb, 5);
            sb.Append($"Live mobs update over the next seconds ({Tracker.Pending} queued); clients got the new catalog.");
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnImport(TextCommandCallingArgs args)
        {
            string message = ImportContent((string)args[0], out bool ok);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        private TextCommandResult OnSelfTest(TextCommandCallingArgs args)
        {
            var failures = Debug.SelfTest.Run(out int total);
            return failures.Count == 0
                ? TextCommandResult.Success($"All {total} checks passed.")
                : TextCommandResult.Error($"{failures.Count} of {total} checks failed:\n" + string.Join("\n", failures));
        }

        private void AppendProblems(StringBuilder sb, int max)
        {
            foreach (var problem in LastReport.Worst(max)) sb.AppendLine(problem.ToString());
        }

        private static string DescribeActive(AiTaskManager tasks)
        {
            var names = new List<string>();
            foreach (var task in tasks.ActiveTasksBySlot)
            {
                if (task == null) continue;
                names.Add(AiTaskRegistry.TaskCodes.TryGetValue(task.GetType(), out var code) ? code : task.GetType().Name);
            }
            return names.Count == 0 ? "none" : string.Join(", ", names);
        }
    }
}
