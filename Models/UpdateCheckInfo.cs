using System.Text.Json.Serialization;

namespace GithubLauncher.Models
{
    public class UpdateCheckInfo
    {
        public DateTime LastCheckTime { get; set; }
        public string LastKnownVersion { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ETag { get; set; } = string.Empty;
        public bool UpdateAvailable { get; set; }
    }
}
