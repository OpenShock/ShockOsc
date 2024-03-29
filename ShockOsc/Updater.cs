﻿using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OpenShock.ShockOsc.Models;
using Serilog;

namespace OpenShock.ShockOsc;

public static class Updater
{
    private static readonly ILogger Logger = Log.ForContext(typeof(Updater));
    private static readonly HttpClient HttpClient = new();
    private const string GithubLatest = "https://api.github.com/repos/OpenShock/ShockOsc/releases/latest";
    private const string CurrentFileName = "OpenShock.ShockOsc.exe";
    private const string OldFileName = "OpenShock.ShockOsc.old.exe";
    private static readonly string OldFilePath = Path.Combine(Environment.CurrentDirectory, OldFileName);
    private static readonly string CurrentFilePath = Path.Combine(Environment.CurrentDirectory, CurrentFileName);
    private static readonly Version CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? throw new Exception("Could not determine ShockOsc version");

    static Updater()
    {
        HttpClient.DefaultRequestHeaders.Add("User-Agent", $"ShockOsc/{CurrentVersion}");
    }

    private static bool TryDeleteFile(string fileName)
    {
        if (!File.Exists(fileName)) return false;
        try
        {
            File.Delete(fileName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    private static async Task<(Version, GithubReleaseResponse.Asset)?> GetLatestRelease()
    {
        //TryDeleteFile(Path.Combine(Environment.CurrentDirectory, "ShockLink.ShockOsc.exe"));
        //TryDeleteFile(Path.Combine(Environment.CurrentDirectory, "ShockLink.ShockOsc.old.exe"));
        TryDeleteFile(OldFilePath);
        
        Logger.Information("Checking GitHub for updates...");

        var res = await HttpClient.GetAsync(GithubLatest);
        if (!res.IsSuccessStatusCode)
        {
            Logger.Warning("Failed to get latest version information from GitHub. {StatusCode}", res.StatusCode);
            return null;
        }
        
        var json = await JsonSerializer.DeserializeAsync<GithubReleaseResponse>(await res.Content.ReadAsStreamAsync());
        if (json == null)
        {
            Logger.Warning("Could not deserialize json");
            return null;
        }

        if (!Version.TryParse(json.TagName[1..], out var version))
        {
            Logger.Warning("Failed to parse version. Value: {Version}", json.TagName);
            return null;
        }

        var asset = json.Assets.FirstOrDefault(x => x.Name == "OpenShock.ShockOsc.exe");
        if (asset == null)
        {
            Logger.Warning("Could not find asset with OpenShock.ShockOsc.exe. Assets found: {@Assets}", json.Assets);
            return null;
        }

        return (version, asset);
    }

    public static async Task<bool> CheckUpdate()
    {
        var latestVersion = await GetLatestRelease();
        if (latestVersion is null) return false;
        if (latestVersion.Value.Item1 <= CurrentVersion)
        {
            Logger.Information("ShockOsc is up to date ([{Version}] >= [{LatestVersion}])", CurrentVersion, latestVersion.Value.Item1);
            return false;
        }

        if (Config.ConfigInstance.LastIgnoredVersion != null &&
            Config.ConfigInstance.LastIgnoredVersion >= latestVersion.Value.Item1)
        {
            Logger.Information("ShockOsc is not up to date. Skipping update due to previous postpone. You can reenable the updater by setting 'LastIgnoredVersion' to null");
            return false;
        }
        
        Logger.Warning(
            "ShockOsc is not up to date. Newest version is [{NewVersion}] but you are on [{CurrentVersion}]!\nDo you wish to update it?\n[Y]es, [N]o, [D]ont ask (Yes)",
            latestVersion.Value.Item1, CurrentVersion);

        var input = Console.ReadLine()?.ToLowerInvariant();
        var inputChar = 'y';
        if (input?.Length > 0) inputChar = input[0];

        switch (inputChar)
        {
            case 'y':
                return await DoUpdate(latestVersion.Value.Item2.BrowserDownloadUrl);
            case 'd':
                Config.ConfigInstance.LastIgnoredVersion = latestVersion.Value.Item1;
                Config.Save();
                Logger.Information("Postponed update and turned off asking until next version");
                break;
            case 'n':
                Logger.Information("Postponed update");
                break;
        }

        return false;
    }

    private static async Task<bool> DoUpdate(Uri downloadUri)
    {
        Logger.Information("Starting update...");


        try
        {
            Logger.Debug("Moving current file to old");
            File.Move(CurrentFilePath, OldFilePath, true);
        }
        catch (Exception e)
        {
            Logger.Warning(e, "Failed to move file, probably doesnt exist, proceeding like normal...");
        }

        Logger.Debug("Downloading new release...");
        var sp = Stopwatch.StartNew();
        await using (var stream = await HttpClient.GetStreamAsync(downloadUri))
        {
            await using var fStream = new FileStream(CurrentFilePath, FileMode.OpenOrCreate);
            await stream.CopyToAsync(fStream);
        }

        Logger.Debug("Downloaded file within {TimeTook}ms", sp.ElapsedMilliseconds);
        Logger.Information("Download complete, now restarting to newer application in one second");
        await Task.Delay(1000);
        var startInfo = new ProcessStartInfo
        {
            FileName = CurrentFilePath,
            UseShellExecute = true
        };
        Process.Start(startInfo);
        Environment.Exit(0);
        return true;
    }
}