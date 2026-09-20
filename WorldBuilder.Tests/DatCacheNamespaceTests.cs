using WorldBuilder.Shared.Lib;
using Xunit;

namespace WorldBuilder.Tests {
    public class DatCacheNamespaceTests {
        [Fact]
        public void FromDatDirectory_IsStableForSameInputs() {
            string a = DatCacheNamespace.FromDatDirectory(@"C:\Games\AC\Retail", DatProjectMode.Retail);
            string b = DatCacheNamespace.FromDatDirectory(@"C:\Games\AC\Retail\", DatProjectMode.Retail);

            Assert.Equal(a, b);
        }

        [Fact]
        public void FromDatDirectory_SeparatesRetailAndLegacyScopes() {
            string retail = DatCacheNamespace.FromDatDirectory(@"C:\Games\AC\Shared", DatProjectMode.Retail);
            string legacy = DatCacheNamespace.FromDatDirectory(@"C:\Games\AC\Shared", DatProjectMode.LegacyPreTod);

            Assert.NotEqual(retail, legacy);
        }

        [Fact]
        public void FromProjectFile_SeparatesProjectsWithSameName() {
            string a = DatCacheNamespace.FromProjectFile(@"C:\Projects\Legacy\MyWorld\MyWorld.wbproj");
            string b = DatCacheNamespace.FromProjectFile(@"D:\Archives\Retail\MyWorld\MyWorld.wbproj");

            Assert.NotEqual(a, b);
        }
    }
}
