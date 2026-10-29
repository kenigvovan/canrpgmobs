using System;
using ProtoBuf;

namespace canrpgmobs.Net
{
    // server -> client, the in-game store as JSON; sent on join and after every change
    [ProtoContract]
    public class CatalogSyncPacket
    {
        [ProtoMember(1)] public string StoreJson = "";
        // server setting the client map needs
        [ProtoMember(2)] public bool ZonesForPlayers;
    }

    // client -> server, edit/delete one store entry; non-empty OldId = rename
    [ProtoContract]
    public class CatalogEditPacket
    {
        [ProtoMember(1)] public string Section = "";
        [ProtoMember(2)] public string Id = "";
        [ProtoMember(3)] public string OldId = "";
        [ProtoMember(4)] public string Json = "";
        [ProtoMember(5)] public bool Delete;
        // echoed in the result, empty = the editor's
        [ProtoMember(6)] public string Tag = "";
    }

    [ProtoContract]
    public class CatalogEditResultPacket
    {
        [ProtoMember(1)] public bool Ok;
        [ProtoMember(2)] public string Message = "";
        [ProtoMember(3)] public string[] Problems = Array.Empty<string>();
        [ProtoMember(4)] public string Tag = "";
    }

    [ProtoContract]
    public class MobPreviewPacket
    {
        [ProtoMember(1)] public string MobKey = "";
        [ProtoMember(2)] public int Level;
        [ProtoMember(3)] public string Tier = "";
    }

    // wizard output as a mobs file, all-or-nothing, new ids only
    [ProtoContract]
    public class CatalogBundlePacket
    {
        [ProtoMember(1)] public string Json = "";
    }

    // Action: spawn, group, kill, inspect, refresh, stop
    [ProtoContract]
    public class MobTestPacket
    {
        [ProtoMember(1)] public string Action = "";
        // group id for "group"
        [ProtoMember(2)] public string MobKey = "";
        [ProtoMember(3)] public int Level;
        [ProtoMember(4)] public string Tier = "";
        [ProtoMember(5)] public int Count = 1;
        [ProtoMember(6)] public int Radius = 24;
    }

    [ProtoContract]
    public class MobTestResultPacket
    {
        [ProtoMember(1)] public string Action = "";
        [ProtoMember(2)] public bool Ok;
        [ProtoMember(3)] public string Message = "";
        [ProtoMember(4)] public string Inspect = "";
        [ProtoMember(5)] public string[] Trace = Array.Empty<string>();
    }

    // zone panel, polled while its check page is open
    [ProtoContract]
    public class ZoneStatsRequestPacket
    {
        [ProtoMember(1)] public string ZoneId = "";
    }

    // recent spawn attempts as x, y, z triples
    [ProtoContract]
    public class ZoneStatsPacket
    {
        [ProtoMember(1)] public string ZoneId = "";
        [ProtoMember(2)] public string Text = "";
        [ProtoMember(3)] public double[] Points = Array.Empty<double>();
        [ProtoMember(4)] public bool[] Ok = Array.Empty<bool>();
        [ProtoMember(5)] public string[] Reasons = Array.Empty<string>();
    }

    // apply a draft look to the admin's last preview mob
    [ProtoContract]
    public class MobLookPacket
    {
        [ProtoMember(1)] public string AppearanceJson = "";
        [ProtoMember(2)] public string PresetJson = "";
    }
}
