namespace StartDock.Models
{
    /// <summary>One entry from the Start menu's own app list (Get-StartApps) — covers
    /// both Store/UWP apps and ordinary desktop apps that registered themselves there.</summary>
    public class InstalledAppInfo
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>The AppUserModelId Explorer's virtual "shell:AppsFolder" uses as a key.
        /// Combined into "shell:AppsFolder\{AppId}" to launch and to extract an icon for.</summary>
        public string AppId { get; set; } = string.Empty;
    }
}
