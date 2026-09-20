namespace WorldBuilder.Lib;

/// <summary>
/// Public update feed. Change <see cref="GitHubOwner"/> and <see cref="GitHubRepo"/>
/// to match the public GitHub repository, then enable GitHub Pages on that repo.
/// </summary>
public static class AppReleaseInfo {
    public const string GitHubOwner = "Vanquish-6";
    public const string GitHubRepo = "ACME-AC-Map-Editor-WorldBuilder";

    public static string AppcastUrl =>
        $"https://{GitHubOwner.ToLowerInvariant()}.github.io/{GitHubRepo}/appcast.xml";

    public const string SparklePublicKey = "V8bysZdBUEvhVt36o/FTTixfKEpNnwroz41Ihz9HrAs=";
}
