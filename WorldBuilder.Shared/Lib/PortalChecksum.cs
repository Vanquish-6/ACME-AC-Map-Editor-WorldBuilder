namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Port of PortalChecksum__CalcChecksum32 from the AC client (portal/cell DAT validation).
    /// </summary>
    public static class PortalChecksum {
        public static int CalcChecksum32(ReadOnlySpan<byte> data) {
            if (data.IsEmpty)
                return 0;

            int size = data.Length;
            int checksum = size << 16;
            int aligned = size & ~3;

            for (int i = 0; i < aligned; i += 4) {
                checksum += data[i]
                    | (data[i + 1] << 8)
                    | (data[i + 2] << 16)
                    | (data[i + 3] << 24);
            }

            int partialSum = 0;
            int shift = 3;
            for (int i = aligned; i < size; i++) {
                partialSum += data[i] << (8 * shift--);
            }

            return partialSum + checksum;
        }

        public static int CalcChecksum32(byte[]? data) {
            if (data == null || data.Length == 0)
                return 0;
            return CalcChecksum32(data.AsSpan());
        }
    }
}
