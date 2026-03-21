using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace CombatSimulator.Animation;

public unsafe class ChatCommandExecutor
{
    private readonly IPluginLog log;
    private float cooldownTimer;

    public ChatCommandExecutor(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Execute a chat command (e.g., "/playdead", "/gsit").
    /// Must be called on the framework thread.
    /// </summary>
    public void ExecuteCommand(string command, float cooldown = 0f)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        if (!command.StartsWith('/'))
        {
            log.Warning($"ChatCommandExecutor: Command must start with '/': {command}");
            return;
        }

        if (cooldownTimer > 0)
            return;

        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                log.Warning("ChatCommandExecutor: UIModule is null.");
                return;
            }

            var utf8Str = new Utf8String(command);
            try
            {
                uiModule->ProcessChatBoxEntry(&utf8Str);
                log.Verbose($"ChatCommandExecutor: Executed '{command}'");
            }
            finally
            {
                utf8Str.Dtor();
            }

            if (cooldown > 0)
                cooldownTimer = cooldown;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"ChatCommandExecutor: Failed to execute '{command}'.");
        }
    }

    public void Tick(float deltaTime)
    {
        if (cooldownTimer > 0)
            cooldownTimer = Math.Max(0, cooldownTimer - deltaTime);
    }
}
