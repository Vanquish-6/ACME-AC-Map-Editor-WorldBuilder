using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Client UI string ID hash (<c>compute_str_hash</c> / <see cref="SpellBase.GetStringHash"/>).
/// </summary>
public static class AcClientStringHash {
    public static uint Compute(string value) => SpellBase.GetStringHash(value);
}
