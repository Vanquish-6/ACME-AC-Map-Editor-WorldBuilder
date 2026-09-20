using System.Text.Json.Serialization;

namespace WorldBuilder.Shared.Lib.AceDb {
    /// <summary>
    /// MySQL connection settings for the world database.
    /// Persisted per-project in project.json.
    /// </summary>
    public class AceDbSettings {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "";
        public string User { get; set; } = "root";
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// When true, the export pipeline will generate a reposition SQL file.
        /// </summary>
        public bool EnableReposition { get; set; } = false;

        /// <summary>
        /// When true (and EnableReposition is true), execute the generated SQL
        /// directly against the database after export.
        /// </summary>
        public bool ApplyDirectly { get; set; } = false;

        /// <summary>
        /// Minimum absolute height delta (in world units) before an instance
        /// is included in the SQL output.
        /// </summary>
        public float Threshold { get; set; } = 0.05f;

        [JsonIgnore]
        public string ConnectionString =>
            $"Server={Host};Port={Port};Database={Database};User={User};Password={Password};";
    }
}
