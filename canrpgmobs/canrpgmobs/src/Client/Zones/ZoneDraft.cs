using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;

namespace canrpgmobs.Client.Zones
{
    // The zone being marked up. Map, panel, wand and outlines all edit this one draft.
    public sealed class ZoneDraft
    {
        public const string Tag = "zones";
        private const int MaxUndo = 200;

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        private readonly ICoreClientAPI capi;
        private readonly canrpgmobsModSystem mod;
        private readonly List<string> undo = new();
        private readonly List<string> redo = new();
        private string savedJson = "";
        private bool deleting;
        private int dirtyAt = -1;
        private bool dirty;
        private int shapeAt = -1;
        private ZoneShape? shape;
        private int badAt = -1;
        private HashSet<int> bad = new();
        private bool panelOpen;
        private bool wandHeld;
        private string? typedKey;
        private long typedAtMs;

        public ZoneDraft(ICoreClientAPI capi, canrpgmobsModSystem mod)
        {
            this.capi = capi;
            this.mod = mod;
            mod.EditResultReceived += OnEditResult;
            mod.CatalogChanged += OnCatalogChanged;
        }

        // markup mode: map edits, outlines, panel; on while the panel is open or the wand is in hand
        public bool Active => panelOpen || wandHeld;
        public JObject? Draft { get; private set; }
        public string? Id => Draft == null ? null : Q(ReadString(Draft, "id"));
        // saved id, null for a new zone
        public string? OpenedId { get; private set; }
        public int Version { get; private set; }
        public bool Awaiting { get; private set; }
        public string Status { get; private set; } = "";
        public bool StatusIsError { get; private set; }
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;

        public event Action? Changed;

        public bool IsDirty
        {
            get
            {
                if (Draft == null) return false;
                if (dirtyAt != Version)
                {
                    dirty = Draft.ToString(Formatting.None) != savedJson;
                    dirtyAt = Version;
                }
                return dirty;
            }
        }

        public List<(int X, int Z)> Corners
        {
            get
            {
                var list = new List<(int, int)>();
                if (Draft?["polygon"] is not JArray polygon) return list;
                foreach (var token in polygon)
                    if (token is JArray { Count: 2 } p) list.Add(((int)p[0], (int)p[1]));
                return list;
            }
        }

        // null below 3 corners
        public ZoneShape? Shape
        {
            get
            {
                if (shapeAt == Version) return shape;
                shapeAt = Version;
                shape = null;
                if (Draft == null) return null;
                try
                {
                    var model = Draft.ToObject<ZoneModel>();
                    if (model != null)
                    {
                        model.id = Id!;
                        shape = ZoneShape.Of(model);
                    }
                }
                catch (JsonException) { }
                return shape;
            }
        }

        // drawn red
        public HashSet<int> BadEdges()
        {
            if (badAt == Version) return bad;
            badAt = Version;
            return bad = ZoneGeometry.BadEdges(Corners.ToArray());
        }

        public void SetActive(bool on)
        {
            if (panelOpen == on) return;
            panelOpen = on;
            Touch(false);
        }

        public void SetWandHeld(bool on)
        {
            if (wandHeld == on) return;
            wandHeld = on;
            Touch(false);
        }

        public void Open(string id)
        {
            string qualified = Q(id);
            var json = mod.Store.Get("zones", qualified)?.DeepClone() as JObject
                       ?? (mod.Catalog.Zones.TryGetValue(qualified, out var model) ? JObject.FromObject(model, Serializer) : null);
            if (json == null)
            {
                SetStatus("That zone no longer exists.", true);
                return;
            }
            json["id"] = qualified;
            Draft = json;
            OpenedId = qualified;
            savedJson = json.ToString(Formatting.None);
            undo.Clear();
            redo.Clear();
            SetStatus("", false);
            Touch(false);
        }

        public void New(int x, int z)
        {
            int n = 1;
            while (mod.Catalog.Zones.ContainsKey(Q("zone" + n)) || mod.Store.Contains("zones", Q("zone" + n))) n++;
            var json = EditorSchema.NewEntry("zones", Q("zone" + n), mod.Catalog.Data);
            json["name"] = new JObject { ["en"] = "Zone " + n };
            // not new JArray(JArray), that's the copy constructor
            json["polygon"] = new JArray { new JArray(x, z) };
            Draft = json;
            OpenedId = null;
            savedJson = "";
            undo.Clear();
            redo.Clear();
            SetStatus("New zone: add at least 3 corners, then save.", false);
            Touch(false);
        }

        public void Close()
        {
            Draft = null;
            OpenedId = null;
            undo.Clear();
            redo.Clear();
            Touch(false);
        }

        // one undo step
        public void Edit(Action<JObject> change)
        {
            if (Draft == null) return;
            PushUndo();
            change(Draft);
            Touch(true);
        }

        // typing into one field is one undo step
        public void Type(string key, Action<JObject> change)
        {
            if (Draft == null) return;
            long now = capi.World.ElapsedMilliseconds;
            if (key != typedKey || now - typedAtMs > 2000) PushUndo(true);
            typedKey = key;
            typedAtMs = now;
            change(Draft);
            Touch(true);
        }

        // drags: BeginDrag once, then MoveCorner without undo steps
        public void BeginDrag() => PushUndo();

        public void MoveCorner(int index, int x, int z)
        {
            if (Polygon() is not { } polygon || index < 0 || index >= polygon.Count) return;
            polygon[index] = new JArray(x, z);
            Touch(true);
        }

        public void InsertCorner(int index, int x, int z, bool record = true)
        {
            if (Draft == null) return;
            if (record) PushUndo();
            var polygon = EnsureArray(Draft, "polygon");
            polygon.Insert(Math.Clamp(index, 0, polygon.Count), new JArray(x, z));
            Touch(true);
        }

        public void RemoveCorner(int index)
        {
            if (Polygon() is not { } polygon || index < 0 || index >= polygon.Count) return;
            PushUndo();
            polygon.RemoveAt(index);
            Touch(true);
        }

        // a saved zone keeps at least 3 corners
        public void RemoveCornerIfAllowed(int index)
        {
            if (Corners.Count <= 3 && OpenedId != null) Notify(Lang.Get("canrpgmobs:zones-min-corners"), true);
            else RemoveCorner(index);
        }

        // true = unsaved edits, the player was told
        public bool WarnUnsaved()
        {
            if (!IsDirty) return false;
            Notify(Lang.Get("canrpgmobs:zones-unsaved"), true);
            return true;
        }

        // into the edge it is closest to, or at the end while there are fewer than 3; returns the new index
        public int AddCorner(int x, int z)
        {
            var corners = Corners;
            int at = corners.Count < 3 ? corners.Count : NearestEdge(corners, x, z) + 1;
            InsertCorner(at, x, z);
            return at;
        }

        public int NearestCorner(double x, double z, double maxDistance)
        {
            var corners = Corners;
            int best = -1;
            double bestSq = maxDistance * maxDistance;
            for (int i = 0; i < corners.Count; i++)
            {
                double dx = corners[i].X + 0.5 - x, dz = corners[i].Z + 0.5 - z;
                double sq = dx * dx + dz * dz;
                if (sq > bestSq) continue;
                bestSq = sq;
                best = i;
            }
            return best;
        }

        public static int NearestEdge(List<(int X, int Z)> corners, double x, double z)
        {
            int best = 0;
            double bestSq = double.MaxValue;
            for (int i = 0; i < corners.Count; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % corners.Count];
                double sq = SegmentDistanceSq(a.X, a.Z, b.X, b.Z, x - 0.5, z - 0.5);
                if (sq >= bestSq) continue;
                bestSq = sq;
                best = i;
            }
            return best;
        }

        public void Undo() => Step(undo, redo);
        public void Redo() => Step(redo, undo);

        public void Revert()
        {
            if (OpenedId == null) Close();
            else Open(OpenedId);
        }

        public void Save()
        {
            if (Draft == null || Awaiting) return;
            // typing "from 10" into a fresh 1-5 zone leaves min above max
            if (Draft["level"] is JArray { Count: 2 } level && (int)level[0] > (int)level[1])
                Draft["level"] = new JArray(level[1], level[0]);
            var corners = Corners.ToArray();
            string? problem = corners.Length < 3 ? "A zone needs at least 3 corners."
                : BadEdges().Count > 0 ? "Edges cross or fold back (drawn red), move a corner."
                : ZoneGeometry.Area(corners) < 1 ? "The corners lie on one line, the zone has no area."
                : Draft["level"] is JArray { Count: 2 } l && (int)l[0] < 1 ? "Level must start from 1."
                : null;
            if (problem != null)
            {
                Notify(problem, true);
                return;
            }
            string id = Id!;
            Awaiting = true;
            mod.SendEdit("zones", id, OpenedId != null && OpenedId != id ? OpenedId : null, Draft.ToString(Formatting.None), Tag);
            SetStatus("Saving...", false);
        }

        public void Delete()
        {
            if (Draft == null || OpenedId == null || Awaiting) return;
            Awaiting = true;
            deleting = true;
            mod.SendDelete("zones", OpenedId, Tag);
            SetStatus("Deleting...", false);
        }

        public bool InStore => OpenedId != null && mod.Store.Contains("zones", OpenedId);

        private void OnEditResult(CatalogEditResultPacket packet)
        {
            if (packet.Tag != Tag) return;
            Awaiting = false;
            bool wasDelete = deleting;
            deleting = false;
            if (packet.Ok && Draft != null)
            {
                // the catalog sync comes before the reply; a mod file zone comes back after deleting its in-game version
                if (wasDelete && mod.Catalog.Zones.ContainsKey(OpenedId!)) Open(OpenedId!);
                else if (wasDelete) Close();
                else
                {
                    OpenedId = Id;
                    savedJson = Draft.ToString(Formatting.None);
                }
            }
            var problems = packet.Problems ?? Array.Empty<string>();
            SetStatus(packet.Ok ? packet.Message : "Not saved: " + packet.Message + (problems.Length > 0 ? "\n" + problems[0] : ""), !packet.Ok);
            Touch(false);
        }

        private void OnCatalogChanged()
        {
            // the saved version may have changed under a clean draft
            if (Draft != null && OpenedId != null && !IsDirty && !Awaiting)
            {
                var current = mod.Store.Get("zones", OpenedId)?.ToString(Formatting.None);
                if (current != null && current != savedJson) Open(OpenedId);
            }
            Touch(false);
        }

        private void Step(List<string> from, List<string> to)
        {
            if (Draft == null || from.Count == 0) return;
            typedKey = null;
            to.Add(Draft.ToString(Formatting.None));
            Draft = JObject.Parse(from[^1]);
            from.RemoveAt(from.Count - 1);
            Touch(true);
        }

        private void PushUndo(bool typing = false)
        {
            if (Draft == null) return;
            if (!typing) typedKey = null;
            undo.Add(Draft.ToString(Formatting.None));
            if (undo.Count > MaxUndo) undo.RemoveAt(0);
            redo.Clear();
        }

        private JArray? Polygon() => Draft?["polygon"] as JArray;

        public void SetStatus(string text, bool error)
        {
            Status = text;
            StatusIsError = error;
        }

        // status change only, no edit
        public void Notify(string text, bool error)
        {
            SetStatus(text, error);
            Changed?.Invoke();
        }

        private void Touch(bool edited)
        {
            Version++;
            if (edited && !Awaiting && StatusIsError) SetStatus("", false);
            Changed?.Invoke();
        }

        private static double SegmentDistanceSq(double ax, double az, double bx, double bz, double px, double pz)
        {
            double dx = bx - ax, dz = bz - az;
            double len = dx * dx + dz * dz;
            double t = len == 0 ? 0 : Math.Clamp(((px - ax) * dx + (pz - az) * dz) / len, 0, 1);
            double cx = ax + t * dx - px, cz = az + t * dz - pz;
            return cx * cx + cz * cz;
        }

        public void Dispose()
        {
            mod.EditResultReceived -= OnEditResult;
            mod.CatalogChanged -= OnCatalogChanged;
        }
    }
}
