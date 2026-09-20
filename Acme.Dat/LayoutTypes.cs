namespace Acme.Dat;

public enum UIStateId : uint {
    Undef = 0,
    Normal = 1,
    Normal_rollover = 2,
    Normal_pressed = 3,
}

[Flags]
public enum IncorporationFlags : uint {
    None = 0,
    X = 0x1,
    Y = 0x2,
    Width = 0x4,
    Height = 0x8,
    ZLevel = 0x10,
}

public enum MediaType {
    Undef = 0,
    Image = 1,
    Animation = 2,
    Message = 3,
    Sound = 4,
}

public enum DrawModeType : uint {
    Default = 0,
}

public abstract class BaseProperty {
    public virtual uint PropertyKey { get; set; }
}

public sealed class StringInfo {
    public uint TableId { get; set; }
    public uint StringId { get; set; }
}

public sealed class StringInfoBaseProperty : BaseProperty {
    public StringInfo Value { get; set; } = new();
}

public abstract class MediaDesc {
    public MediaType Type { get; set; }
}

public sealed class MediaDescImage : MediaDesc {
    public uint File { get; set; }
    public DrawModeType DrawMode { get; set; }
}

public sealed class MediaDescMessage : MediaDesc {
    public uint MessageId { get; set; }
    public uint Id { get => MessageId; set => MessageId = value; }
}

public sealed class StateDesc {
    public uint StateId { get; set; }
    public bool PassToChildren { get; set; }
    public IncorporationFlags IncorporationFlags { get; set; }
    public Dictionary<uint, BaseProperty> Properties { get; set; } = new();
    public List<MediaDesc> Media { get; set; } = [];
}

public sealed class ElementDesc {
    public StateDesc StateDesc { get; set; } = new();
    public uint ReadOrder { get; set; }
    public uint ElementId { get; set; }
    public uint Type { get; set; }
    public uint BaseElement { get; set; }
    public uint BaseLayoutId { get; set; }
    public UIStateId DefaultState { get; set; }
    public uint X { get; set; }
    public uint Y { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint ZLevel { get; set; }
    public uint LeftEdge { get; set; }
    public uint TopEdge { get; set; }
    public uint RightEdge { get; set; }
    public uint BottomEdge { get; set; }
    public Dictionary<UIStateId, StateDesc> States { get; set; } = new();
    public Dictionary<uint, ElementDesc> Children { get; set; } = new();
}
