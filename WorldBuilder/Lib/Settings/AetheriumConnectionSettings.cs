using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WorldBuilder.Lib.Settings {
    /// <summary>
    /// World Patch API v1 connection. The token is kept in memory only; settings.json
    /// holds it encrypted for the current Windows user (DPAPI).
    /// </summary>
    [SettingCategory("Aetherium", Order = 2)]
    public partial class AetheriumConnectionSettings : ObservableObject {
        private const string DpapiPrefix = "dpapi:";
        private const string PlainPrefix = "plain:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ACME WorldBuilder world patch token");

        [SettingDescription("Server web address, for example https://your-server.example. Use https unless the server runs on this machine.")]
        [SettingDisplayName("Web URL")]
        [SettingOrder(0)]
        private string _baseUrl = "";
        public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value); }

        [SettingHidden]
        private string _token = "";
        /// <summary>Raw bearer token. Edited in the Connect window, which masks it.</summary>
        [JsonIgnore]
        public string Token { get => _token; set => SetProperty(ref _token, value); }

        /// <summary>
        /// The form of <see cref="Token"/> written to settings.json. On Windows it can only be
        /// decrypted by the same user on the same machine; a copied file yields an empty token.
        /// </summary>
        [SettingHidden]
        public string ProtectedToken {
            get => Protect(_token);
            set => Token = Unprotect(value);
        }

        [SettingDescription("Allow plain http to localhost or 127.0.0.1. The server must allow it too.")]
        [SettingDisplayName("Allow insecure loopback")]
        [SettingOrder(2)]
        private bool _allowInsecureLoopback;
        public bool AllowInsecureLoopback { get => _allowInsecureLoopback; set => SetProperty(ref _allowInsecureLoopback, value); }

        [SettingDescription("white, red, or both. Both worlds use the same URL and token.")]
        [SettingDisplayName("World")]
        [SettingOrder(3)]
        private string _world = "white";
        public string World { get => _world; set => SetProperty(ref _world, value); }

        /// <summary>Landblocks ACME has sent. Restore uses this list to put the project back.</summary>
        [SettingHidden]
        private List<string> _pushedLandblocks = new();
        public List<string> PushedLandblocks {
            get => _pushedLandblocks ??= new();
            set => SetProperty(ref _pushedLandblocks, value ?? new());
        }

        public void RememberLandblock(ushort landblockKey) {
            string hex = $"{landblockKey:X4}";
            if (PushedLandblocks.Contains(hex)) return;
            PushedLandblocks.Add(hex);
            OnPropertyChanged(nameof(PushedLandblocks));
        }

        private static string Protect(string token) {
            if (string.IsNullOrEmpty(token)) return "";
            if (!OperatingSystem.IsWindows()) return PlainPrefix + token;
            byte[] sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(sealedBytes);
        }

        private static string Unprotect(string? stored) {
            if (string.IsNullOrEmpty(stored)) return "";
            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal)) return stored[PlainPrefix.Length..];
            if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return "";
            try {
                byte[] sealedBytes = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException) {
                return "";
            }
        }
    }
}
