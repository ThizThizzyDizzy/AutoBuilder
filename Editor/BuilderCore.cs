using System;
using System.Threading.Tasks;
using UnityEngine;
using VRC.Core;

public class BuilderCore : MonoBehaviour
{
    public static async Task<APIUser> TryLogin()
    {
        Log("Logging in...");
        
        API.SetOnlineMode(true);
        await InitRemoteConfig();

        if (!APIUser.IsLoggedIn && !ApiCredentials.Load()) throw new Exception("Unable to load API Credentials!");

        const int attempts = 10;
        string lastError = "";
        for (int i = 0; i < attempts; i++)
        {
            if (APIUser.IsLoggedIn) return APIUser.CurrentUser;
            int status = 0;
            APIUser.InitialFetchCurrentUser(user => status = 1, user => lastError = user.Error);
            await Task.Delay(250);

            if (status == 1) return APIUser.CurrentUser;
        }

        throw new Exception($"Unable to log in after {attempts} attempts! ({lastError})");
    }

    public static async Task InitRemoteConfig()
    {
        if (ConfigManager.RemoteConfig.IsInitialized()) return;
        int status = 0;
        ConfigManager.RemoteConfig.Init(() => status = 1, () => status = -1);
        while (status == 0) await Task.Delay(100);
        if (status < 0) throw new Exception("Failed to initialize remote config!");
    }

    public static void Log(string log) => Debug.Log($"[AutoBuilder] {log}");
    public static void LogWarning(string log) => Debug.LogWarning($"[AutoBuilder] {log}");
    public static void LogError(string log) => Debug.LogError($"[AutoBuilder] {log}");
}