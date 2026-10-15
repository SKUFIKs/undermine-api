using System.Collections.Generic;

namespace UnderMineApi.DTOs
{
    /// <summary>
    /// A single selectable version (Minecraft release or loader build).
    /// </summary>
    public class VersionOptionDto
    {
        public string Version { get; set; } = string.Empty;

        /// <summary>release / snapshot for Minecraft, release / beta for loader builds.</summary>
        public string? Type { get; set; }

        /// <summary>False for snapshots, betas and alphas.</summary>
        public bool Stable { get; set; } = true;

        /// <summary>The version the admin panel selects by default (newest usable build).</summary>
        public bool Recommended { get; set; }
    }

    /// <summary>
    /// Response for the version auto-discovery endpoints.
    /// </summary>
    public class VersionListResponse
    {
        public string Loader { get; set; } = string.Empty;

        /// <summary>Minecraft version the loader versions belong to (null for the Minecraft list).</summary>
        public string? MinecraftVersion { get; set; }

        /// <summary>Upstream the list came from, handy when debugging.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Set when the upstream list could not be read; the panel then falls back to manual input.</summary>
        public string? Message { get; set; }

        public List<VersionOptionDto> Versions { get; set; } = new();
    }
}
