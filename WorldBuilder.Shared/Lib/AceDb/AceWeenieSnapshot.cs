using System;
using System.Collections.Generic;

namespace WorldBuilder.Shared.Lib.AceDb {

    public sealed class AceWeenieRowInt {
        public ushort Type { get; set; }
        public int Value { get; set; }
    }

    public sealed class AceWeenieRowInt64 {
        public ushort Type { get; set; }
        public long Value { get; set; }
    }

    public sealed class AceWeenieRowBool {
        public ushort Type { get; set; }
        public bool Value { get; set; }
    }

    public sealed class AceWeenieRowFloat {
        public ushort Type { get; set; }
        public double Value { get; set; }
    }

    public sealed class AceWeenieRowString {
        public ushort Type { get; set; }
        public string Value { get; set; } = "";
    }

    public sealed class AceWeenieRowDid {
        public ushort Type { get; set; }
        public uint Value { get; set; }
    }

    public sealed class AceWeenieRowIid {
        public ushort Type { get; set; }
        public ulong Value { get; set; }
    }

    /// <summary>Scalar weenie properties loaded from the world database (complex tables are counted only).</summary>
    public sealed class AceWeenieSnapshot {
        public uint ClassId { get; set; }
        public uint WeenieType { get; set; }
        public DateTime? LastModified { get; set; }

        public List<AceWeenieRowInt> Ints { get; } = new();
        public List<AceWeenieRowInt64> Int64s { get; } = new();
        public List<AceWeenieRowBool> Bools { get; } = new();
        public List<AceWeenieRowFloat> Floats { get; } = new();
        public List<AceWeenieRowString> Strings { get; } = new();
        public List<AceWeenieRowDid> DataIds { get; } = new();
        public List<AceWeenieRowIid> InstanceIds { get; } = new();

        public int SpellBookCount { get; set; }
        public int CreateListCount { get; set; }
        public int EmoteCount { get; set; }
        public int BookCount { get; set; }
        public int PositionCount { get; set; }
        public int AttributeCount { get; set; }
        public int Attribute2ndCount { get; set; }
        public int SkillCount { get; set; }

        /// <summary>PropertyDataId.Setup (1), or 0.</summary>
        public uint SetupDid =>
            DataIds.Find(d => d.Type == 1)?.Value ?? 0;

        /// <summary>PropertyDataId.Icon (8), or 0.</summary>
        public uint IconDid =>
            DataIds.Find(d => d.Type == 8)?.Value ?? 0;
    }
}
