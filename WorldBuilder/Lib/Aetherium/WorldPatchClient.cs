using Acme.Dat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Lib.Aetherium {
    public sealed class WorldPatchState {
        public bool Available { get; init; }
        public string ReleaseId { get; init; } = "baseline";
        public ulong Generation { get; init; }
        public string SportalSha256 { get; init; } = "";
        public string ScellSha256 { get; init; } = "";
        public string UnavailableReason { get; init; } = "";
        public string Summary { get; init; } = "";
        public string WorldId { get; init; } = "";
    }

    public readonly record struct WorldTarget(string Id, string Label);

    public static class WorldTargets {
        public static WorldTarget White { get; } = new("white", "White");
        public static WorldTarget Red { get; } = new("red", "Red");

        public static IReadOnlyList<WorldTarget> Selected(AetheriumConnectionSettings settings) {
            var world = (settings.World ?? "white").Trim().ToLowerInvariant();
            if (world == "red") return new[] { Red };
            if (world == "both") return new[] { White, Red };
            return new[] { White };
        }
    }

    public static class WorldPatchClient {
        private const string Family = "/api/admin/world-patches/v1";
        private static readonly System.Threading.AsyncLocal<string?> WorldHeader = new();

        public static string CurrentWorldId() => WorldHeader.Value ?? "white";

        public static async Task<T> InWorld<T>(WorldTarget target, Func<Task<T>> action) {
            var previous = WorldHeader.Value;
            WorldHeader.Value = target.Id == "white" ? null : target.Id;
            try {
                return await action();
            }
            finally {
                WorldHeader.Value = previous;
            }
        }

        public static async Task<string> ProbeAsync(AetheriumConnectionSettings settings, CancellationToken ct = default) {
            var parts = new List<string>();
            foreach (var target in WorldTargets.Selected(settings)) {
                try {
                    string summary = await InWorld(target, async () => (await LoadStateAsync(settings, ct)).Summary);
                    parts.Add(summary);
                }
                catch (Exception ex) {
                    parts.Add($"{target.Label}: {ex.GetBaseException().Message}");
                }
            }
            return string.Join("\n", parts);
        }

        public static async Task<WorldPatchState> LoadStateAsync(AetheriumConnectionSettings settings, CancellationToken ct = default) {
            var root = await GetAsync(settings, Family + "/capabilities", ct);
            bool available = Bool(root, "available");
            string release = "baseline";
            ulong generation = 0;
            if (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.Object) {
                release = Text(active, "releaseId") ?? "baseline";
                generation = U64(active, "generation");
            }
            string sportal = "";
            string scell = "";
            if (root.TryGetProperty("baseline", out var baseline) && baseline.ValueKind == JsonValueKind.Object) {
                sportal = Text(baseline, "sportalSha256") ?? "";
                scell = Text(baseline, "scellSha256") ?? "";
            }
            string reason = Text(root, "unavailableReason") ?? "";
            string version = root.TryGetProperty("version", out var versionEl) ? versionEl.ToString() : "1";
            string reported = Text(root, "worldId") ?? "";
            string requested = WorldHeader.Value ?? "white";
            if (!string.Equals(requested, "white", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(reported, requested, StringComparison.OrdinalIgnoreCase)) {
                throw new WorldPatchException(0,
                    "Red did not answer on this URL. Ask the server admin to enable world patches for Red.");
            }
            string name = string.Equals(reported, "red", StringComparison.OrdinalIgnoreCase) ? "Red"
                : string.Equals(reported, "white", StringComparison.OrdinalIgnoreCase) ? "White"
                : "White";
            string summary = !available
                ? (string.IsNullOrEmpty(reason)
                    ? $"Connected to {name}. World patch is not available on this server."
                    : $"Connected to {name}. World patch is not available: {reason}")
                : $"Connected to {name}. World Patch v{version}, release {release}, generation {generation}.";
            return new WorldPatchState {
                Available = available,
                ReleaseId = release,
                Generation = generation,
                SportalSha256 = sportal,
                ScellSha256 = scell,
                UnavailableReason = reason,
                Summary = summary,
                WorldId = reported
            };
        }

        public static async Task<Dictionary<string, (string kind, string sha256, byte[] bytes)>> ReadLandblockAsync(
            AetheriumConnectionSettings settings, ushort landblockKey, CancellationToken ct = default) {
            var root = await GetAsync(settings, $"{Family}/landblocks/0x{landblockKey:X4}", ct);
            var map = new Dictionary<string, (string kind, string sha256, byte[] bytes)>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                return map;
            foreach (var record in records.EnumerateArray()) {
                string? id = Text(record, "recordId");
                string? kind = Text(record, "kind");
                string? sha = Text(record, "sha256");
                if (id == null || kind == null || sha == null) continue;
                if (!record.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("data", out var dataEl))
                    continue;
                string? data = dataEl.GetString();
                if (string.IsNullOrEmpty(data)) continue;
                map[id] = (kind, sha, Convert.FromBase64String(data));
            }
            return map;
        }

        public static async Task<string> CreatePreviewAsync(
            AetheriumConnectionSettings settings,
            WorldPatchState state,
            string name,
            IReadOnlyList<(string recordId, string kind, string beforeSha256, byte[] bytes)> records,
            string idempotencyKey,
            CancellationToken ct = default) {
            var body = new Dictionary<string, object?> {
                ["schema"] = "aetherium.world-patch",
                ["version"] = 1,
                ["name"] = name,
                ["description"] = "Pushed from ACME",
                ["expectedActive"] = Active(state),
                ["baseline"] = new Dictionary<string, string> {
                    ["sportalSha256"] = state.SportalSha256,
                    ["scellSha256"] = state.ScellSha256
                },
                ["records"] = records.Select(r => new Dictionary<string, object?> {
                    ["recordId"] = r.recordId,
                    ["kind"] = r.kind,
                    ["beforeSha256"] = r.beforeSha256,
                    ["payload"] = new Dictionary<string, string> {
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String(r.bytes)
                    }
                }).ToList(),
                ["idempotencyKey"] = idempotencyKey
            };
            var root = await SendAsync(settings, HttpMethod.Post, Family + "/previews", body, ct);
            return Text(root, "previewId") ?? throw new InvalidOperationException("The server did not return a preview id.");
        }

        public static Task ApplyLiveAsync(AetheriumConnectionSettings settings, WorldPatchState state, string previewId, string idempotencyKey, CancellationToken ct = default) =>
            SendAsync(settings, HttpMethod.Post, $"{Family}/previews/{previewId}/test-overlay", Activation(state, idempotencyKey), ct);

        public static Task<JsonElement> CommitPreviewAsync(AetheriumConnectionSettings settings, WorldPatchState state, string previewId, string idempotencyKey, CancellationToken ct = default) =>
            SendAsync(settings, HttpMethod.Post, $"{Family}/previews/{previewId}/activate", Activation(state, idempotencyKey), ct);

        public static async Task ClearLiveAsync(AetheriumConnectionSettings settings, WorldPatchState state, string idempotencyKey, CancellationToken ct = default) {
            try {
                await SendAsync(settings, HttpMethod.Post, Family + "/test-overlay/clear", Activation(state, idempotencyKey), ct);
            }
            catch (WorldPatchException ex) when (ex.StatusCode == 404) {
            }
        }

        public static Task<JsonElement> RestoreBaselineAsync(AetheriumConnectionSettings settings, WorldPatchState state, string idempotencyKey, CancellationToken ct = default) =>
            SendAsync(settings, HttpMethod.Post, Family + "/releases/baseline/activate", Activation(state, idempotencyKey), ct);

        public static async Task<List<ushort>> ReadOverlayLandblocksAsync(AetheriumConnectionSettings settings, CancellationToken ct = default) {
            try {
                var root = await GetAsync(settings, Family + "/test-overlay", ct);
                return Landblocks(root, "affectedLandblocks");
            }
            catch (WorldPatchException ex) when (ex.StatusCode == 404) {
                return new List<ushort>();
            }
        }

        public static List<ushort> Landblocks(JsonElement root, string name) {
            var list = new List<ushort>();
            if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var value in values.EnumerateArray()) {
                string? text = value.GetString();
                if (text == null) continue;
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    text = text[2..];
                if (ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var key))
                    list.Add(key);
            }
            return list;
        }

        public static async Task<int> WaitUntilDeliveredAsync(AetheriumConnectionSettings settings, CancellationToken ct = default) {
            int pending = 0;
            for (int i = 0; i < 20; i++) {
                var root = await GetAsync(settings, Family + "/delivery", ct);
                pending = root.TryGetProperty("worldPatchUpdatingCount", out var count) && count.TryGetInt32(out var n) ? n : 0;
                if (pending == 0) return 0;
                await Task.Delay(400, ct);
            }
            return pending;
        }

        private static Dictionary<string, object> Active(WorldPatchState state) => new() {
            ["releaseId"] = state.ReleaseId,
            ["generation"] = state.Generation
        };

        private static Dictionary<string, object> Activation(WorldPatchState state, string idempotencyKey) => new() {
            ["expectedActive"] = Active(state),
            ["idempotencyKey"] = idempotencyKey
        };

        private static async Task<JsonElement> GetAsync(AetheriumConnectionSettings settings, string path, CancellationToken ct) =>
            await SendAsync(settings, HttpMethod.Get, path, null, ct);

        private static async Task<JsonElement> SendAsync(
            AetheriumConnectionSettings settings, HttpMethod method, string path, object? body, CancellationToken ct) {
            var (baseUrl, token) = Require(settings);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(method, baseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(WorldHeader.Value))
                request.Headers.TryAddWithoutValidation("X-Aetherium-World", WorldHeader.Value);
            if (body != null) {
                string json = JsonSerializer.Serialize(body);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            HttpResponseMessage response;
            try {
                response = await http.SendAsync(request, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                throw new WorldPatchException(0, Unreachable(baseUrl));
            }
            using (response) {
                string text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new WorldPatchException((int)response.StatusCode, Explain((int)response.StatusCode, text, response.ReasonPhrase));
                if (string.IsNullOrWhiteSpace(text))
                    return default;
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
        }

        private static string Unreachable(string baseUrl) =>
            $"The server did not respond at {baseUrl}. Check the web URL and that the server is running.";

        private static (string baseUrl, string token) Require(AetheriumConnectionSettings settings) {
            if (settings == null) throw new WorldPatchException(0, "Connection settings are missing.");
            var baseUrl = (settings.BaseUrl ?? "").Trim().TrimEnd('/');
            var token = settings.Token ?? "";
            if (baseUrl.Length == 0) throw new WorldPatchException(0, "Enter the server web URL.");
            if (token.Length == 0 || token.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
                throw new WorldPatchException(0, "Enter the bearer token. It cannot contain spaces.");
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                throw new WorldPatchException(0, "That web URL is not valid.");
            bool loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                && !(settings.AllowInsecureLoopback && loopback && string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
                throw new WorldPatchException(0, "Use https, or enable insecure loopback and use http://127.0.0.1 or http://localhost.");
            return (baseUrl, token);
        }

        private const string NotAllowedMessage =
            "The server does not accept world patches from this network. Ask the server admin to allow your IP address.";
        private const string RateLimitedMessage =
            "Too many requests or rejected tokens from this network. Wait, then check the bearer token and try again.";

        private static string Explain(int status, string body, string? reason) {
            try {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error)) {
                    string code = Text(error, "code") ?? "";
                    string message = Text(error, "message") ?? reason ?? "Request failed.";
                    if (code == "affected_area_occupied") {
                        var ids = error.TryGetProperty("details", out var details)
                            ? Landblocks(details, "occupiedLandblocks")
                            : new List<ushort>();
                        string where = ids.Count == 0
                            ? "the 3x3 area around this landblock"
                            : string.Join(", ", ids.Select(id => $"0x{id:X4}"));
                        return $"Someone is still in {where}. Portal at least two landblocks away, then try again. Nothing was changed.";
                    }
                    if (code == "test_overlay_active")
                        return "A live test is already mounted. Clear the live test before committing or reverting.";
                    if (code == "rate_limited")
                        return RateLimitedMessage;
                    if (code == "forbidden" && status == 403)
                        return NotAllowedMessage;
                    return string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
                }
            }
            catch {
            }
            if (status == 403) return NotAllowedMessage;
            if (status == 429) return RateLimitedMessage;
            return string.IsNullOrWhiteSpace(body) ? (reason ?? "Request failed.") : body;
        }

        private static bool Bool(JsonElement el, string name) =>
            el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private static string? Text(JsonElement el, string name) =>
            el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static ulong U64(JsonElement el, string name) {
            if (!el.TryGetProperty(name, out var value)) return 0;
            return value.TryGetUInt64(out var n) ? n : 0;
        }
    }

    public sealed class WorldPatchException : Exception {
        public int StatusCode { get; }
        public WorldPatchException(int statusCode, string message) : base(message) {
            StatusCode = statusCode;
        }
    }

    public static class WorldPatchPublisher {
        public static async Task<string> PushAsync(
            AetheriumConnectionSettings settings,
            IDatReaderWriter dats,
            TerrainDocument terrain,
            DocumentManager documents,
            ushort landblockKey,
            bool commit,
            CancellationToken ct = default) {
            settings.RememberLandblock(landblockKey);
            return await EachWorld(settings, () => PushOneAsync(settings, dats, terrain, documents, landblockKey, commit, ct));
        }

        private static async Task<string> PushOneAsync(
            AetheriumConnectionSettings settings,
            IDatReaderWriter dats,
            TerrainDocument terrain,
            DocumentManager documents,
            ushort landblockKey,
            bool commit,
            CancellationToken ct) {
            var state = await WorldPatchClient.LoadStateAsync(settings, ct);
            if (!state.Available)
                return state.Summary;

            var current = await WorldPatchClient.ReadLandblockAsync(settings, landblockKey, ct);
            var records = BuildRecords(dats, terrain, documents, landblockKey, current);
            if (records.Count == 0)
                return $"Landblock 0x{landblockKey:X4} has no outdoor records the server can patch.";

            string key = NewKey(commit ? "commit" : "live");
            string previewId = await WorldPatchClient.CreatePreviewAsync(
                settings, state, $"ACME 0x{landblockKey:X4}", records, key, ct);
            if (commit)
                await WorldPatchClient.CommitPreviewAsync(settings, state, previewId, NewKey("activate"), ct);
            else
                await WorldPatchClient.ApplyLiveAsync(settings, state, previewId, NewKey("overlay"), ct);
            int pending = await WorldPatchClient.WaitUntilDeliveredAsync(settings, ct);
            string world = WorldName();
            string mode = commit
                ? $"Saved landblock 0x{landblockKey:X4} on {world}."
                : $"Live test of landblock 0x{landblockKey:X4} is on {world}. It is not saved until you use Save This Landblock.";
            return mode + "\n\n" + ClientSteps(pending);
        }

        public static Task<string> ClearLiveAsync(AetheriumConnectionSettings settings, CancellationToken ct = default) =>
            EachWorld(settings, async () => {
                var state = await WorldPatchClient.LoadStateAsync(settings, ct);
                await WorldPatchClient.ClearLiveAsync(settings, state, NewKey("clear"), ct);
                int pending = await WorldPatchClient.WaitUntilDeliveredAsync(settings, ct);
                return $"Live test removed from {WorldName()}.\n\n" + ClientSteps(pending);
            });

        public sealed class RevertResult {
            public string Message { get; init; } = "";
            public List<ushort> ProjectLandblocks { get; init; } = new();
        }

        public static async Task<RevertResult> RevertAsync(AetheriumConnectionSettings settings, IReadOnlyList<ushort> pushed, CancellationToken ct = default) {
            var messages = new List<string>();
            var project = new HashSet<ushort>();
            foreach (var target in WorldTargets.Selected(settings)) {
                var result = await WorldPatchClient.InWorld(target, () => RevertOneAsync(settings, pushed, ct));
                messages.Add(WorldTargets.Selected(settings).Count == 1 ? result.Message : target.Label + "\n" + result.Message);
                foreach (var key in result.ProjectLandblocks)
                    project.Add(key);
            }
            return new RevertResult { Message = string.Join("\n\n", messages), ProjectLandblocks = project.ToList() };
        }

        private static async Task<RevertResult> RevertOneAsync(AetheriumConnectionSettings settings, IReadOnlyList<ushort> pushed, CancellationToken ct) {
            var project = new HashSet<ushort>(pushed);
            var overlay = await WorldPatchClient.ReadOverlayLandblocksAsync(settings, ct);
            foreach (var key in overlay)
                project.Add(key);

            var state = await WorldPatchClient.LoadStateAsync(settings, ct);
            await WorldPatchClient.ClearLiveAsync(settings, state, NewKey("clear-before-revert"), ct);
            state = await WorldPatchClient.LoadStateAsync(settings, ct);
            var restored = await WorldPatchClient.RestoreBaselineAsync(settings, state, NewKey("baseline"), ct);
            var sent = new HashSet<ushort>(WorldPatchClient.Landblocks(restored, "affectedLandblocks"));
            foreach (var key in sent)
                project.Add(key);

            var missing = pushed.Where(key => !sent.Contains(key)).ToList();
            string resendNote = "";
            if (missing.Count > 0) {
                try {
                    var resent = await ForceOriginalDeliveryAsync(settings, missing, ct);
                    foreach (var key in resent)
                        sent.Add(key);
                    resendNote = "The original landblock bytes were sent again, because an earlier revert had not reached the client.";
                }
                catch (WorldPatchException ex) {
                    resendNote = ex.Message;
                }
            }

            int pending = await WorldPatchClient.WaitUntilDeliveredAsync(settings, ct);
            string blocks = project.Count == 0
                ? "No landblock was recorded, so the project was left as it is."
                : "Project landblocks put back to the original DAT: " + Format(project) + ".";
            string server = sent.Count == 0
                ? $"{WorldName()} was already the original world."
                : $"{WorldName()} sent the original for " + Format(sent) + ".";
            string message = server + "\n" + blocks;
            if (resendNote.Length > 0)
                message += "\n" + resendNote;
            message += "\n\n" + ClientSteps(pending);
            return new RevertResult { Message = message, ProjectLandblocks = project.ToList() };
        }

        private static async Task<string> EachWorld(AetheriumConnectionSettings settings, Func<Task<string>> action) {
            var targets = WorldTargets.Selected(settings);
            var parts = new List<string>();
            foreach (var target in targets) {
                string text = await WorldPatchClient.InWorld(target, action);
                parts.Add(targets.Count == 1 ? text : target.Label + "\n" + text);
            }
            return string.Join("\n\n", parts);
        }

        private static string WorldName() =>
            string.Equals(WorldPatchClient.CurrentWorldId(), "red", StringComparison.OrdinalIgnoreCase) ? "Red" : "White";

        private static async Task<List<ushort>> ForceOriginalDeliveryAsync(
            AetheriumConnectionSettings settings, IReadOnlyList<ushort> landblocks, CancellationToken ct) {
            var state = await WorldPatchClient.LoadStateAsync(settings, ct);
            var records = new List<(string recordId, string kind, string beforeSha256, byte[] bytes)>();
            foreach (var key in landblocks) {
                var current = await WorldPatchClient.ReadLandblockAsync(settings, key, ct);
                string landId = $"0x{((uint)key << 16) | 0xFFFF:X8}";
                string infoId = $"0x{((uint)key << 16) | 0xFFFE:X8}";
                if (current.TryGetValue(landId, out var land))
                    records.Add((landId, land.kind, land.sha256, BumpLandblock(land.bytes)));
                if (current.TryGetValue(infoId, out var info) && BumpInfo(info.bytes) is byte[] bumped)
                    records.Add((infoId, info.kind, info.sha256, bumped));
            }
            if (records.Count == 0)
                return new List<ushort>();

            string previewId = await WorldPatchClient.CreatePreviewAsync(
                settings, state, "Resend original", records, NewKey("resend"), ct);
            await WorldPatchClient.CommitPreviewAsync(settings, state, previewId, NewKey("resend-activate"), ct);
            state = await WorldPatchClient.LoadStateAsync(settings, ct);
            JsonElement restored;
            try {
                restored = await WorldPatchClient.RestoreBaselineAsync(settings, state, NewKey("resend-baseline"), ct);
            }
            catch (WorldPatchException) {
                state = await WorldPatchClient.LoadStateAsync(settings, ct);
                restored = await WorldPatchClient.RestoreBaselineAsync(settings, state, NewKey("resend-baseline-retry"), ct);
            }
            return WorldPatchClient.Landblocks(restored, "affectedLandblocks");
        }

        private static byte[] BumpLandblock(byte[] raw) {
            var copy = (byte[])raw.Clone();
            int heightAt = 8 + 81 * 2;
            if (copy.Length > heightAt)
                copy[heightAt] = (byte)(copy[heightAt] + 1);
            return copy;
        }

        private static byte[]? BumpInfo(byte[] raw) {
            if (raw.Length < 20 || BitConverter.ToUInt32(raw, 8) == 0)
                return null;
            var copy = (byte[])raw.Clone();
            float x = BitConverter.ToSingle(copy, 16);
            BitConverter.GetBytes(x + 1f).CopyTo(copy, 16);
            return copy;
        }

        private static string Format(IEnumerable<ushort> keys) =>
            string.Join(", ", keys.Select(key => $"0x{key:X4}"));

        private static string ClientSteps(int pending) =>
            pending == 0
                ? "In game, run @freelist flush, then portal back in. The landblock you are standing on does not change until you do."
                : $"{pending} client(s) are still downloading it. When that finishes, run @freelist flush and portal back in.";

        private static string NewKey(string action) =>
            $"acme-{action}-{Guid.NewGuid():N}";

        private static List<(string recordId, string kind, string beforeSha256, byte[] bytes)> BuildRecords(
            IDatReaderWriter dats,
            TerrainDocument terrain,
            DocumentManager documents,
            ushort landblockKey,
            Dictionary<string, (string kind, string sha256, byte[] bytes)> current) {
            var built = new List<(string recordId, string kind, string beforeSha256, byte[] bytes)>();
            uint fileId = ((uint)landblockKey << 16) | 0xFFFF;
            uint infoId = ((uint)landblockKey << 16) | 0xFFFE;
            string landId = $"0x{fileId:X8}";
            string infoRecordId = $"0x{infoId:X8}";

            if (dats.TryGetLandblock(fileId, out var land) && current.TryGetValue(landId, out var serverLand)) {
                ApplyTerrain(land, terrain, landblockKey);
                if (DatNativeRecords.TryUnpack<LandBlock>(serverLand.bytes, out var serverParsed) && serverParsed != null)
                    land.HasObjects = serverParsed.HasObjects;
                built.Add((landId, "landBlock", serverLand.sha256, DatNativeRecords.Pack(land)));
            }

            if (dats.TryGet<LandBlockInfo>(infoId, out var info) && current.TryGetValue(infoRecordId, out var serverInfo)) {
                if (documents.ActiveDocs.TryGetValue($"landblock_{landblockKey:X4}", out var doc) && doc is LandblockDocument landblock)
                    ApplyObjects(info, landblock.GetStaticObjects().ToList(), landblockKey, dats);
                built.Add((infoRecordId, "landBlockInfo", serverInfo.sha256, DatNativeRecords.Pack(info)));
            }
            return built;
        }

        private static void ApplyTerrain(LandBlock land, TerrainDocument terrain, ushort landblockKey) {
            var entries = terrain.GetLandblockInternal(landblockKey);
            if (entries == null || land.Terrain == null || land.Height == null) return;
            int count = Math.Min(entries.Length, Math.Min(land.Terrain.Length, land.Height.Length));
            for (int i = 0; i < count; i++) {
                uint terrainData = entries[i].ToUInt();
                land.Terrain[i] = (ushort)((land.Terrain[i] & ~0xF87F)
                    | (terrainData & 3)
                    | (((terrainData >> 8) & 31) << 11)
                    | (((terrainData >> 16) & 31) << 2));
                land.Height[i] = (byte)(terrainData >> 24);
            }
        }

        private static void ApplyObjects(LandBlockInfo info, List<StaticObject> objects, ushort landblockKey, IDatReaderWriter dats) {
            var used = new HashSet<int>();
            foreach (var building in info.Buildings) {
                int best = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < objects.Count; i++) {
                    if (used.Contains(i) || objects[i].IsParticleEmitter || objects[i].Id != building.ModelId) continue;
                    float dist = Vector3Distance(ToLocal(objects[i].Origin, landblockKey), building.Frame.Origin);
                    if (dist < bestDist) {
                        bestDist = dist;
                        best = i;
                    }
                }
                if (best < 0) continue;
                used.Add(best);
                building.Frame = new Frame {
                    Origin = ToLocal(objects[best].Origin, landblockKey),
                    Orientation = objects[best].Orientation
                };
            }

            var stabs = new List<Stab>();
            for (int i = 0; i < objects.Count; i++) {
                if (used.Contains(i)) continue;
                var obj = objects[i];
                if (BuildingBlueprintCache.IsBuildingModelId(obj.Id, dats)) continue;
                stabs.Add(new Stab {
                    Id = obj.Id,
                    Frame = new Frame {
                        Origin = ToLocal(obj.Origin, landblockKey),
                        Orientation = obj.Orientation
                    }
                });
            }
            info.Objects.Clear();
            info.Objects.AddRange(stabs);
        }

        private static Vector3 ToLocal(Vector3 world, ushort landblockKey) {
            float blockX = (landblockKey >> 8) & 0xFF;
            float blockY = landblockKey & 0xFF;
            return new Vector3(world.X - blockX * 192f, world.Y - blockY * 192f, world.Z);
        }

        private static float Vector3Distance(Vector3 a, Vector3 b) {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
