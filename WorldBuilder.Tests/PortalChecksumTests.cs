using System.Text;
using WorldBuilder.Shared.Lib;
using Xunit;

namespace WorldBuilder.Tests {
    public class PortalChecksumTests {
        [Fact]
        public void Empty_ReturnsZero() {
            Assert.Equal(0, PortalChecksum.CalcChecksum32(ReadOnlySpan<byte>.Empty));
            Assert.Equal(0, PortalChecksum.CalcChecksum32((byte[]?)null));
        }

        [Fact]
        public void AlignedWords_MatchesClientFormula() {
            // "ABCD" => size<<16 + 0x44434241
            var data = Encoding.ASCII.GetBytes("ABCD");
            int checksum = PortalChecksum.CalcChecksum32(data);
            Assert.Equal((4 << 16) + 0x44434241, checksum);
        }

        [Fact]
        public void TailBytes_AddShiftedPartialSum() {
            var data = new byte[] { 0x01, 0x02, 0x03 };
            int checksum = PortalChecksum.CalcChecksum32(data);
            int expected = (3 << 16) + (0x01 << 24) + (0x02 << 16) + (0x03 << 8);
            Assert.Equal(expected, checksum);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(7)]
        [InlineData(16)]
        public void Length_IsDeterministic(int len) {
            var data = new byte[len];
            for (int i = 0; i < len; i++)
                data[i] = (byte)(i * 31 + 7);
            int a = PortalChecksum.CalcChecksum32(data);
            int b = PortalChecksum.CalcChecksum32(data);
            Assert.Equal(a, b);
        }
    }
}
