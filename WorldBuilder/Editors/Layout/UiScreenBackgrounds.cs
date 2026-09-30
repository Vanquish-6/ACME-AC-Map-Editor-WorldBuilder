using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Layout;

/// <summary>
/// Portal image ids drawn behind the intro and connection screens.
/// Retail intro layout <c>0x21000000</c> uses <c>0x06001343</c> (800×482). Retail connection layout
/// <c>0x21000001</c> uses JPEG stills <c>0x0600610F</c> and <c>0x06006EBC</c> (640×480).
/// Legacy portal.dat uses <c>0x06001343</c> for the connection background and
/// <c>0x06001B14</c>–<c>0x06001B16</c> for the intro stills. The movies themselves are loose files.
/// </summary>
public static class UiScreenBackgrounds {
    public const uint IntroSurfaceId = 0x06001343;
    public static readonly uint[] ConnectionSurfaceIds = [0x0600610F, 0x06006EBC];
    public const uint LegacyConnectionSurfaceId = 0x06001343;
    public static readonly uint[] LegacyIntroSurfaceIds = [0x06001B14, 0x06001B15, 0x06001B16];

    public static uint[] For(DatProjectMode mode, bool intro) {
        if (mode == DatProjectMode.LegacyPreTod) {
            return intro ? LegacyIntroSurfaceIds : [LegacyConnectionSurfaceId];
        }

        return intro ? [IntroSurfaceId] : ConnectionSurfaceIds;
    }
}
