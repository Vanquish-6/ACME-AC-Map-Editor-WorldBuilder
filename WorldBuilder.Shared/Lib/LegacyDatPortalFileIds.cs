namespace WorldBuilder.Shared.Lib;

internal static class LegacyDatPortalFileIds {
    internal const uint Iteration = 0xFFFF0001u;

    internal static uint Environment(uint environmentId) =>
        (environmentId & 0xFF000000u) == 0x0D000000u ? environmentId : 0x0D000000u | (environmentId & 0xFFFFu);

    internal static uint Surface(uint surfaceId) =>
        (surfaceId & 0xFF000000u) == 0x08000000u ? surfaceId : 0x08000000u | (surfaceId & 0xFFFFu);
}
