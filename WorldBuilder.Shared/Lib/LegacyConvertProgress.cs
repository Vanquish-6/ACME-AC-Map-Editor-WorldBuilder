namespace WorldBuilder.Shared.Lib;

public static class LegacyConvertProgress {
    public static string Timestamped(string message) => $"{DateTime.Now:HH:mm:ss} {message}";

    public static Action<string>? Wrap(Action<string>? onProgress) {
        if (onProgress == null) {
            return null;
        }

        return message => onProgress(Timestamped(message));
    }

    public static bool ShouldReport(int count, long elapsedMs, ref long lastReportMs, int interval = 50, long heartbeatMs = 2000) {
        if (count == 1 || count % interval == 0 || elapsedMs - lastReportMs >= heartbeatMs) {
            lastReportMs = elapsedMs;
            return true;
        }

        return false;
    }
}
