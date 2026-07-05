using Dalamud.Plugin.Services;

namespace CombatSimulator.UpdateLog;

public sealed class UpdateLogPopupController
{
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly bool hadExistingConfig;
    private readonly UpdateLogCatalog catalog;
    private readonly UpdateLogWindow window;
    private readonly string currentVersion;
    private bool checkedForUpdate;

    public UpdateLogPopupController(Configuration config, IPluginLog log, bool hadExistingConfig)
    {
        this.config = config;
        this.log = log;
        this.hadExistingConfig = hadExistingConfig;
        catalog = UpdateLogCatalog.Load();
        window = new UpdateLogWindow(MarkCurrentVersionSeen);
        currentVersion = UpdateLogCatalog.CurrentPluginVersion;
    }

    public void Draw()
    {
        if (!checkedForUpdate)
        {
            checkedForUpdate = true;
            TryOpenForCurrentVersion();
        }

        window.Draw();
    }

    private void TryOpenForCurrentVersion()
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
            return;

        var lastSeen = UpdateLogCatalog.NormalizeVersion(config.LastSeenUpdateLogVersion);
        if (lastSeen == currentVersion)
            return;

        if (string.IsNullOrWhiteSpace(config.LastSeenUpdateLogVersion) && !hadExistingConfig)
        {
            MarkCurrentVersionSeen();
            return;
        }

        if (!config.ShowUpdateLogOnUpdate)
            return;

        if (catalog.Find(currentVersion) == null)
        {
            log.Warning($"UpdateLog: no entry found for plugin version {currentVersion}.");
            return;
        }

        // Show the current version plus its recent history (newest first) so a user who
        // skipped several updates can catch up in one popup, not just the latest entry.
        window.Open(catalog.RecentEntries());
    }

    private void MarkCurrentVersionSeen()
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
            return;

        config.LastSeenUpdateLogVersion = currentVersion;
        config.Save();
    }
}
