using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BiliArchive.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string ArchivePath { get; set; } = string.Empty;

    public string FfmpegPath { get; set; } = string.Empty;

    public int Quality { get; set; } = 80;

    public int MinScanMinutes { get; set; } = 30;

    public int MaxScanMinutes { get; set; } = 240;

    public List<long> FolderIds { get; set; } = [];
}
