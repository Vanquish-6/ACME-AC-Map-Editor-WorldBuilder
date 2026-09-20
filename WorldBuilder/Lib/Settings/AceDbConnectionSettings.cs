using CommunityToolkit.Mvvm.ComponentModel;
using WorldBuilder.Shared.Lib.AceDb;
using System;

namespace WorldBuilder.Lib.Settings {
    /// <summary>
    /// World database connection used for weenie lookup and instance placement.
    /// Stored in app settings; Export window can override per-project.
    /// </summary>
    [SettingCategory("World database", Order = 1)]
    public partial class AceDbConnectionSettings : ObservableObject {
        [SettingDescription("MySQL host for the world database")]
        [SettingOrder(0)]
        private string _host = "localhost";
        public string Host { get => _host; set => SetProperty(ref _host, value); }

        [SettingDescription("MySQL port")]
        [SettingOrder(1)]
        private int _port = 3306;
        public int Port { get => _port; set => SetProperty(ref _port, value); }

        [SettingDescription("World database name")]
        [SettingOrder(2)]
        private string _database = "";
        public string Database { get => _database; set => SetProperty(ref _database, value); }

        [SettingDescription("MySQL user name")]
        [SettingOrder(3)]
        private string _user = "root";
        public string User { get => _user; set => SetProperty(ref _user, value); }

        [SettingDescription("MySQL password")]
        [SettingOrder(4)]
        [SettingDisplayName("Password")]
        private string _password = "";
        public string Password { get => _password; set => SetProperty(ref _password, value); }

        public AceDbSettings ToAceDbSettings() => new AceDbSettings {
            Host = Host,
            Port = Port,
            Database = Database,
            User = User,
            Password = Password,
        };
    }
}
