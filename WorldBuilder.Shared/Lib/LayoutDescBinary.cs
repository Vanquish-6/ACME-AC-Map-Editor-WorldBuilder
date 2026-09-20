using Acme.Dat;

namespace WorldBuilder.Shared.Lib {
    /// <summary>Deep-copy a layout via native pack/unpack. Element trees stay in the native library.</summary>
    public static class LayoutDescBinary {
        public static LayoutDesc Clone(LayoutDesc source, uint id) {
            byte[] bytes = AethDatNative.EncodeStatic((uint)DatKind.LayoutDesc, source);
            var copy = AethDatNative.DecodeStatic<LayoutDesc>((uint)DatKind.LayoutDesc, bytes);
            copy.Id = id;
            return copy;
        }

        public static LayoutDesc Clone(LayoutDesc source, uint id, object? _) => Clone(source, id);
    }
}
