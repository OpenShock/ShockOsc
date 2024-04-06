using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using LucHeart.CoreOSC;
using Microsoft.Extensions.Logging;
using OpenShock.SDK.CSharp.Live;
using OpenShock.SDK.CSharp.Live.Models;
using OpenShock.SDK.CSharp.Models;
using OpenShock.ShockOsc.Backend;
using OpenShock.ShockOsc.Models;
using OpenShock.ShockOsc.OscChangeTracker;
using OpenShock.ShockOsc.OscQueryLibrary;
using OpenShock.ShockOsc.Ui.Utils;
using OpenShock.ShockOsc.Utils;
using SmartFormat;

#pragma warning disable CS4014

namespace OpenShock.ShockOsc;

public sealed class ShockOsc
{
    private readonly ILogger<ShockOsc> _logger;
    private readonly OscClient _oscClient;
    private readonly OpenShockApiLiveClient _liveClient;
    private readonly UnderscoreConfig _underscoreConfig;

    private static bool _oscServerActive;
    private static bool _isAfk;
    private static bool _isMuted;
    public static string AvatarId = string.Empty;
    private static readonly Random Random = new();
    public static readonly ConcurrentDictionary<string, ProgramGroup> ProgramGroups = new();
    
    public event Func<Task>? OnGroupsChanged; 

    public static readonly string[] ShockerParams =
    {
        string.Empty,
        "Stretch",
        "IsGrabbed",
        "Cooldown",
        "Active",
        "Intensity",
        "CooldownPercentage",
        "IShock"
    };
    
    public static Dictionary<string, object?> ParamsInUse = new();
    public static Dictionary<string, object?> AllAvatarParams = new();

    public static Action<bool>? OnParamsChange;
    public static Action? OnConfigUpdate;
    
    private readonly ChangeTrackedOscParam<bool> _paramAnyActive;
    private readonly ChangeTrackedOscParam<bool> _paramAnyCooldown;
    private readonly ChangeTrackedOscParam<float> _paramAnyCooldownPercentage;
    private readonly ChangeTrackedOscParam<float> _paramAnyIntensity;

    public ShockOsc(ILogger<ShockOsc> logger, OscClient oscClient, OpenShockApi openShockApi, OpenShockApiLiveClient liveClient, UnderscoreConfig underscoreConfig)
    {
        _logger = logger;
        _oscClient = oscClient;
        _liveClient = liveClient;
        _underscoreConfig = underscoreConfig;

        _paramAnyActive = new ChangeTrackedOscParam<bool>("_Any", "_Active", false, _oscClient);
        _paramAnyCooldown = new ChangeTrackedOscParam<bool>("_Any", "_Cooldown", false, _oscClient);
        _paramAnyCooldownPercentage = new ChangeTrackedOscParam<float>("_Any", "_CooldownPercentage", 0f, _oscClient);
        _paramAnyIntensity = new ChangeTrackedOscParam<float>("_Any", "_Intensity", 0f, _oscClient);
    }
    
    public void RaiseOnGroupsChanged() => OnGroupsChanged.Raise();
    
    private static void OnParamChange(bool shockOscParam)
    {
        OnParamsChange?.Invoke(shockOscParam);
    }

    public void FoundVrcClient()
    {
        _logger.LogInformation("Found VRC client");
        // stop tasks
        _oscServerActive = false;
        Task.Delay(1000).Wait(); // wait for tasks to stop

        _oscClient.CreateGameConnection(IPAddress.Parse(OscQueryServer.OscIpAddress), OscQueryServer.OscReceivePort, OscQueryServer.OscSendPort);
        _logger.LogInformation("Connecting UDP Clients...");

        // Start tasks
        _oscServerActive = true;
        OsTask.Run(ReceiverLoopAsync);
        OsTask.Run(SenderLoopAsync);
        OsTask.Run(CheckLoop);

        _logger.LogInformation("Ready");
        OsTask.Run(_underscoreConfig.SendUpdateForAll);
    }
    
    public void OnAvatarChange(Dictionary<string, object?>? parameters, string avatarId)
    {
        AvatarId = avatarId;
        try
        {
            foreach (var obj in ProgramGroups)
            {
                obj.Value.Reset();
            }

            var parameterCount = 0;

            if (parameters == null)
            {
                _logger.LogError("Failed to receive avatar parameters");
                return;
            }

            ParamsInUse.Clear();
            AllAvatarParams.Clear();

            foreach (var param in parameters.Keys)
            {
                if (param.StartsWith("/avatar/parameters/"))
                    AllAvatarParams.TryAdd(param[19..], parameters[param]);

                if (!param.StartsWith("/avatar/parameters/ShockOsc/"))
                    continue;

                var paramName = param[28..];
                var lastUnderscoreIndex = paramName.LastIndexOf('_') + 1;
                var action = string.Empty;
                var shockerName = paramName;
                if (lastUnderscoreIndex > 1)
                {
                    shockerName = paramName[..(lastUnderscoreIndex - 1)];
                    action = paramName.Substring(lastUnderscoreIndex, paramName.Length - lastUnderscoreIndex);
                }

                if (ShockerParams.Contains(action))
                {
                    parameterCount++;
                    ParamsInUse.TryAdd(paramName, parameters[param]);
                }

                if (!ProgramGroups.ContainsKey(shockerName) && !shockerName.StartsWith("_"))
                {
                    _logger.LogWarning("Unknown shocker on avatar {Shocker}", shockerName);
                    _logger.LogDebug("Param: {Param}", param);
                }
            }

            _logger.LogInformation("Loaded avatar config with {ParamCount} parameters", parameterCount);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on avatar change logic");
        }
        OnParamChange(true);
    }

    private async Task ReceiverLoopAsync()
    {
        while (_oscServerActive)
        {
            try
            {
                await ReceiveLogic();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error in receiver loop");
            }
        }
        // ReSharper disable once FunctionNeverReturns
    }

    private async Task ReceiveLogic()
    {
        OscMessage received;
        try
        {
            received = await _oscClient.ReceiveGameMessage()!;
        }
        catch (Exception e)
        {
            _logger.LogTrace(e, "Error receiving message");
            return;
        }

        var addr = received.Address;
        _logger.LogTrace("Received message: {Addr}", addr);

        if (addr.StartsWith("/avatar/parameters/"))
        {
            var fullName = addr[19..];
            if (AllAvatarParams.ContainsKey(fullName))
            {
                AllAvatarParams[fullName] = received.Arguments[0];
                OnParamChange(false);
            }
            else
                AllAvatarParams.TryAdd(fullName, received.Arguments[0]);
        }

        switch (addr)
        {
            case "/avatar/change":
                var avatarId = received.Arguments.ElementAtOrDefault(0);
                _logger.LogDebug("Avatar changed: {AvatarId}", avatarId);
                OsTask.Run(OscQueryServer.GetParameters);
                OsTask.Run(_underscoreConfig.SendUpdateForAll);
                return;
            case "/avatar/parameters/AFK":
                _isAfk = received.Arguments.ElementAtOrDefault(0) is true;
                _logger.LogDebug("Afk: {State}", _isAfk);
                return;
            case "/avatar/parameters/MuteSelf":
                _isMuted = received.Arguments.ElementAtOrDefault(0) is true;
                _logger.LogDebug("Muted: {State}", _isMuted);
                return;
        }

        if (!addr.StartsWith("/avatar/parameters/ShockOsc/"))
            return;

        var pos = addr.Substring(28, addr.Length - 28);

        // Check if _Config
        if (pos.StartsWith("_Config/"))
        {
            _underscoreConfig.HandleCommand(pos, received.Arguments);
            return;
        }

        var lastUnderscoreIndex = pos.LastIndexOf('_') + 1;
        var action = string.Empty;
        var shockerName = pos;
        if (lastUnderscoreIndex > 1)
        {
            shockerName = pos[..(lastUnderscoreIndex - 1)];
            action = pos.Substring(lastUnderscoreIndex, pos.Length - lastUnderscoreIndex);
        }

        if (ParamsInUse.ContainsKey(pos))
        {
            ParamsInUse[pos] = received.Arguments[0];
            OnParamChange(true);
        }
        else
            ParamsInUse.TryAdd(pos, received.Arguments[0]);

        if (!ShockerParams.Contains(action)) return;

        if (!ProgramGroups.ContainsKey(shockerName))
        {
            if (shockerName == "_Any") return;
            _logger.LogWarning("Unknown shocker {Shocker}", shockerName);
            _logger.LogDebug("Param: {Param}", pos);
            return;
        }

        var shocker = ProgramGroups[shockerName];

        var value = received.Arguments.ElementAtOrDefault(0);
        switch (action)
        {
            case "IShock":
                // TODO: check Cooldowns
                if (value is not true) return;
                if (_underscoreConfig.KillSwitch)
                {
                    shocker.TriggerMethod = TriggerMethod.None;
                    await LogIgnoredKillSwitchActive();
                    return;
                }

                if (_isAfk && ShockOscConfigManager.ConfigInstance.Behaviour.DisableWhileAfk)
                {
                    shocker.TriggerMethod = TriggerMethod.None;
                    await LogIgnoredAfk();
                    return;
                }

                OsTask.Run(() => InstantShock(shocker, GetDuration(), GetIntensity()));

                return;
            case "Stretch":
                if (value is float stretch)
                    shocker.LastStretchValue = stretch;
                return;
            case "IsGrabbed":
                var isGrabbed = value is true;
                if (shocker.IsGrabbed && !isGrabbed)
                {
                    // on physbone release
                    if (shocker.LastStretchValue != 0)
                    {
                        shocker.TriggerMethod = TriggerMethod.PhysBoneRelease;
                        shocker.LastActive = DateTime.UtcNow;
                    }
                    else if (ShockOscConfigManager.ConfigInstance.Behaviour.WhileBoneHeld !=
                                ShockOscConfigManager.ShockOscConfig.BehaviourConf.BoneHeldAction.None)
                    {
                        await CancelAction(shocker);
                    }
                }

                shocker.IsGrabbed = isGrabbed;
                return;
            // Normal shocker actions
            case "":
                break;
            // Ignore all other actions
            default:
                return;
        }

        if (value is true)
        {
            shocker.TriggerMethod = TriggerMethod.Manual;
            shocker.LastActive = DateTime.UtcNow;
        }
        else shocker.TriggerMethod = TriggerMethod.None;
    }

    private ValueTask LogIgnoredKillSwitchActive()
    {
        _logger.LogInformation("Ignoring shock, kill switch is active");
        if (string.IsNullOrEmpty(ShockOscConfigManager.ConfigInstance.Chatbox.IgnoredKillSwitchActive)) return ValueTask.CompletedTask;

        return _oscClient.SendChatboxMessage(
            $"{ShockOscConfigManager.ConfigInstance.Chatbox.Prefix}{ShockOscConfigManager.ConfigInstance.Chatbox.IgnoredKillSwitchActive}");
    }

    private ValueTask LogIgnoredAfk()
    {
        _logger.LogInformation("Ignoring shock, user is AFK");
        if (string.IsNullOrEmpty(ShockOscConfigManager.ConfigInstance.Chatbox.IgnoredAfk)) return ValueTask.CompletedTask;

        return _oscClient.SendChatboxMessage(
            $"{ShockOscConfigManager.ConfigInstance.Chatbox.Prefix}{ShockOscConfigManager.ConfigInstance.Chatbox.IgnoredAfk}");
    }

    private async Task SenderLoopAsync()
    {
        while (_oscServerActive)
        {
            await SendParams();
            await Task.Delay(300);
        }
    }
    
    private async Task InstantShock(ProgramGroup programGroup, uint duration, byte intensity)
    {
        programGroup.LastExecuted = DateTime.UtcNow;
        programGroup.LastDuration = duration;
        var intensityPercentage = Math.Round(GetFloatScaled(intensity) * 100f);
        programGroup.LastIntensity = intensity;

        ForceUnmute();
        SendParams();

        programGroup.TriggerMethod = TriggerMethod.None;
        var inSeconds = MathF.Round(duration / 1000f, 1).ToString(CultureInfo.InvariantCulture);
        _logger.LogInformation(
            "Sending shock to {Shocker} Intensity: {Intensity} IntensityPercentage: {IntensityPercentage}% Length:{Length}s",
            programGroup.Name, intensity, intensityPercentage, inSeconds);

        await ControlGroup(programGroup.Id, duration, intensity, ControlType.Shock);

        if (!ShockOscConfigManager.ConfigInstance.Osc.Chatbox) return;
        // Chatbox message local
        var dat = new
        {
            ShockerName = programGroup.Name,
            Intensity = intensity,
            IntensityPercentage = intensityPercentage,
            Duration = duration,
            DurationSeconds = inSeconds
        };
        var template = ShockOscConfigManager.ConfigInstance.Chatbox.Types[ControlType.Shock];
        var msg = $"{ShockOscConfigManager.ConfigInstance.Chatbox.Prefix}{Smart.Format(template.Local, dat)}";
        await _oscClient.SendChatboxMessage(msg);
    }

    /// <summary>
    /// Coverts to a 0-1 float and scale it to the max intensity
    /// </summary>
    /// <param name="intensity"></param>
    /// <returns></returns>
    private static float GetFloatScaled(byte intensity) =>
        ClampFloat((float)intensity / ShockOscConfigManager.ConfigInstance.Behaviour.IntensityRange.Max);

    private async Task SendParams()
    {
        // TODO: maybe force resend on avatar change
        var anyActive = false;
        var anyCooldown = false;
        var anyCooldownPercentage = 0f;
        var anyIntensity = 0f;

        foreach (var shocker in ProgramGroups.Values)
        {
            var isActive = shocker.LastExecuted.AddMilliseconds(shocker.LastDuration) > DateTime.UtcNow;
            var isActiveOrOnCooldown =
                shocker.LastExecuted.AddMilliseconds(ShockOscConfigManager.ConfigInstance.Behaviour.CooldownTime)
                    .AddMilliseconds(shocker.LastDuration) > DateTime.UtcNow;
            if (!isActiveOrOnCooldown && shocker.LastIntensity > 0)
                shocker.LastIntensity = 0;

            var onCoolDown = !isActive && isActiveOrOnCooldown;

            var cooldownPercentage = 0f;
            if (onCoolDown)
                cooldownPercentage = ClampFloat(1 -
                                                (float)(DateTime.UtcNow -
                                                        shocker.LastExecuted.AddMilliseconds(shocker.LastDuration))
                                                .TotalMilliseconds / ShockOscConfigManager.ConfigInstance.Behaviour.CooldownTime);

            await shocker.ParamActive.SetValue(isActive);
            await shocker.ParamCooldown.SetValue(onCoolDown);
            await shocker.ParamCooldownPercentage.SetValue(cooldownPercentage);
            await shocker.ParamIntensity.SetValue(GetFloatScaled(shocker.LastIntensity));

            if (isActive) anyActive = true;
            if (onCoolDown) anyCooldown = true;
            anyCooldownPercentage = Math.Max(anyCooldownPercentage, cooldownPercentage);
            anyIntensity = Math.Max(anyIntensity, GetFloatScaled(shocker.LastIntensity));
        }

        await _paramAnyActive.SetValue(anyActive);
        await _paramAnyCooldown.SetValue(anyCooldown);
        await _paramAnyCooldownPercentage.SetValue(anyCooldownPercentage);
        await _paramAnyIntensity.SetValue(anyIntensity);
    }

    private async Task CheckLoop()
    {
        while (_oscServerActive)
        {
            try
            {
                await CheckLogic();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error in check loop");
            }

            await Task.Delay(20);
        }
    }

    private byte GetIntensity()
    {
        var config = ShockOscConfigManager.ConfigInstance.Behaviour;

        if (!config.RandomIntensity) return config.FixedIntensity;
        var rir = config.IntensityRange;
        var intensityValue = Random.Next((int)rir.Min, (int)rir.Max);
        return (byte)intensityValue;
    }

    private async Task CheckLogic()
    {
        var config = ShockOscConfigManager.ConfigInstance.Behaviour;
        foreach (var (pos, programGroup) in ProgramGroups)
        {
            var isActiveOrOnCooldown =
                programGroup.LastExecuted.AddMilliseconds(ShockOscConfigManager.ConfigInstance.Behaviour.CooldownTime)
                    .AddMilliseconds(programGroup.LastDuration) > DateTime.UtcNow;

            if (programGroup.TriggerMethod == TriggerMethod.None &&
                ShockOscConfigManager.ConfigInstance.Behaviour.WhileBoneHeld != ShockOscConfigManager.ShockOscConfig.BehaviourConf.BoneHeldAction.None &&
                !isActiveOrOnCooldown &&
                programGroup.IsGrabbed &&
                programGroup.LastVibration < DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(300)))
            {
                var vibrationIntensity = programGroup.LastStretchValue * 100f;
                if (vibrationIntensity < 1)
                    vibrationIntensity = 1;
                programGroup.LastVibration = DateTime.UtcNow;

                _logger.LogDebug("Vibrating {Shocker} at {Intensity}", pos, vibrationIntensity);
                await ControlGroup(programGroup.Id, 1000, (byte)vibrationIntensity,
                    ShockOscConfigManager.ConfigInstance.Behaviour.WhileBoneHeld == ShockOscConfigManager.ShockOscConfig.BehaviourConf.BoneHeldAction.Shock
                        ? ControlType.Shock
                        : ControlType.Vibrate);
            }

            if (programGroup.TriggerMethod == TriggerMethod.None)
                continue;

            if (programGroup.TriggerMethod == TriggerMethod.Manual &&
                programGroup.LastActive.AddMilliseconds(config.HoldTime) > DateTime.UtcNow)
                continue;

            if (isActiveOrOnCooldown)
            {
                programGroup.TriggerMethod = TriggerMethod.None;
                _logger.LogInformation("Ignoring shock, group {Shocker} is on cooldown", pos);
                continue;
            }

            if (_underscoreConfig.KillSwitch)
            {
                programGroup.TriggerMethod = TriggerMethod.None;
                await LogIgnoredKillSwitchActive();
                continue;
            }

            if (_isAfk && config.DisableWhileAfk)
            {
                programGroup.TriggerMethod = TriggerMethod.None;
                await LogIgnoredAfk();
                continue;
            }

            byte intensity;

            if (programGroup.TriggerMethod == TriggerMethod.PhysBoneRelease)
            {
                intensity = (byte)LerpFloat(config.IntensityRange.Min, config.IntensityRange.Max,
                    programGroup.LastStretchValue);
                programGroup.LastStretchValue = 0;
            }
            else intensity = GetIntensity();

            InstantShock(programGroup, GetDuration(), intensity);
        }
    }

    private uint GetDuration()
    {
        var config = ShockOscConfigManager.ConfigInstance.Behaviour;

        if (!config.RandomDuration) return config.FixedDuration;
        var rdr = config.DurationRange;
        return (uint)(Random.Next((int)(rdr.Min / config.RandomDurationStep),
            (int)(rdr.Max / config.RandomDurationStep)) * config.RandomDurationStep);
    }

    private async Task<bool> ControlGroup(Guid groupId, uint duration, byte intensity, ControlType type)
    {
        if (!ShockOscConfigManager.ConfigInstance.Groups.TryGetValue(groupId, out var group)) return false;

        var controlCommands = group.Shockers.Select(x => new Control
        {
            Id = x,
            Duration = duration,
            Intensity = intensity,
            Type = type
        });
        
        await _liveClient.Control(controlCommands);
        return true;
    }

    public async Task RemoteActivateShocker(ControlLogSender sender, ControlLog log)
    {
        // if (sender.ConnectionId == BackendLiveApiManager.ConnectionId)
        // {
        //     _logger.LogDebug("Ignoring remote command log cause it was the local connection");
        //     return;
        // }

        var inSeconds = ((float)log.Duration / 1000).ToString(CultureInfo.InvariantCulture);

        if (sender.CustomName == null)
            _logger.LogInformation(
                "Received remote {Type} for \"{ShockerName}\" at {Intensity}%:{Duration}s by {Sender}",
                log.Type, log.Shocker.Name, log.Intensity, inSeconds, sender.Name);
        else
            _logger.LogInformation(
                "Received remote {Type} for \"{ShockerName}\" at {Intensity}%:{Duration}s by {SenderCustomName} [{Sender}]",
                log.Type, log.Shocker.Name, log.Intensity, inSeconds, sender.CustomName, sender.Name);

        var template = ShockOscConfigManager.ConfigInstance.Chatbox.Types[log.Type];
        if (ShockOscConfigManager.ConfigInstance.Osc.Chatbox && ShockOscConfigManager.ConfigInstance.Chatbox.DisplayRemoteControl && template.Enabled)
        {
            // Chatbox message remote
            var dat = new
            {
                ShockerName = log.Shocker.Name,
                Intensity = log.Intensity,
                Duration = log.Duration,
                DurationSeconds = inSeconds,
                Name = sender.Name,
                CustomName = sender.CustomName
            };

            var msg =
                $"{ShockOscConfigManager.ConfigInstance.Chatbox.Prefix}{Smart.Format(sender.CustomName == null ? template.Remote : template.RemoteWithCustomName, dat)}";
            await _oscClient.SendChatboxMessage(msg);
        }

        var shocker = ProgramGroups.Values.Where(s => s.Id == log.Shocker.Id).ToArray();
        if (shocker.Length <= 0)
            return;

        var oneShock = false;

        foreach (var pain in shocker)
        {
            switch (log.Type)
            {
                case ControlType.Shock:
                    {
                        pain.LastIntensity = log.Intensity;
                        pain.LastDuration = log.Duration;
                        pain.LastExecuted = log.ExecutedAt;

                        oneShock = true;
                        break;
                    }
                case ControlType.Vibrate:
                    pain.LastVibration = log.ExecutedAt;
                    break;
                case ControlType.Stop:
                    pain.LastDuration = 0;
                    SendParams();
                    break;
                case ControlType.Sound:
                    break;
                default:
                    _logger.LogError("ControlType was out of range. Value was: {Type}", log.Type);
                    break;
            }

            if (oneShock)
            {
                ForceUnmute();
                SendParams();
            }
        }
    }

    private async Task ForceUnmute()
    {
        if (!ShockOscConfigManager.ConfigInstance.Behaviour.ForceUnmute || !_isMuted) return;
        _logger.LogDebug("Force unmuting...");
        await _oscClient.SendGameMessage("/input/Voice", false);
        await Task.Delay(50);
        await _oscClient.SendGameMessage("/input/Voice", true);
        await Task.Delay(50);
        await _oscClient.SendGameMessage("/input/Voice", false);
    }

    private Task CancelAction(ProgramGroup programGroup)
    {
        _logger.LogDebug("Cancelling action");
        return ControlGroup(programGroup.Id, 0, 0, ControlType.Stop);
    }

    private static float LerpFloat(float min, float max, float t) => min + (max - min) * t;
    public static float ClampFloat(float value) => value < 0 ? 0 : value > 1 ? 1 : value;
    public static uint LerpUint(uint min, uint max, float t) => (uint)(min + (max - min) * t);
    public static uint ClampUint(uint value, uint min, uint max) => value < min ? min : value > max ? max : value;
}
