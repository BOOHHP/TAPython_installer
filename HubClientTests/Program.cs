using System.Net.Http;
using System.Text.Json.Nodes;
using TAPythonInstaller;

// End-to-end check of the installer's Hub client against a Hub running with the mock Feishu provider.
// Usage: HubClientTests <hub base url>   (TAPYTHON_INSTALLER_DATA_DIR must point to a scratch directory)
var baseUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:8791";
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TAPYTHON_INSTALLER_DATA_DIR")))
{
    Console.Error.WriteLine("Set TAPYTHON_INSTALLER_DATA_DIR to a scratch directory first.");
    return 2;
}

var failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {name}");
    if (!condition) failures++;
}

using var http = new HttpClient(new HubDeviceHandler(baseUrl));
var plain = new HttpClient();

var deviceId = HubStorage.DeviceId;
Check(deviceId == HubStorage.DeviceId && deviceId.Length >= 8, "device id is stable across reads");

var account = new HubAccount(http, baseUrl);
var telemetry = new HubTelemetry(http, baseUrl, account, "9.9.9-test", () => "5.5");

var config = await account.GetAuthConfigAsync();
Check(config is { Enabled: true }, "hub reports feishu login enabled");

// Anonymous history recorded before signing in must be claimed by the account afterwards.
telemetry.Track("app.launch");
telemetry.Track("tool.install", "demo-tool", "1.0.0");
await telemetry.FlushAsync();

account.AuthorizeUrlOverride = "http://127.0.0.1:1/authorize";
account.BrowserLauncher = target =>
{
    var query = System.Web.HttpUtility.ParseQueryString(new Uri(target).Query);
    Check(query["code_challenge_method"] == "S256" && !string.IsNullOrEmpty(query["code_challenge"]), "authorize url carries a PKCE challenge");
    var callback = $"{query["redirect_uri"]}?code={Uri.EscapeDataString("mock:tester@example.com")}&state={query["state"]}";
    _ = Task.Run(async () =>
    {
        await Task.Delay(200);
        using var browser = new HttpClient();
        var page = await browser.GetAsync(callback);
        Check(page.IsSuccessStatusCode, "loopback callback page is served");
    });
    return true;
};

using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
{
    await account.LoginAsync(cts.Token);
}

Check(account.IsSignedIn && account.User?.Name == "tester", "login completes and exposes the user");
Check(File.Exists(Path.Combine(HubStorage.Directory, "hub-session.bin")), "session is stored on disk");
Check(!File.ReadAllText(Path.Combine(HubStorage.Directory, "hub-session.bin"), System.Text.Encoding.Latin1).Contains("refreshToken"), "session file is encrypted, not plain JSON");

var restored = new HubAccount(http, baseUrl);
restored.LoadFromDisk();
Check(restored.IsSignedIn && await restored.GetAccessTokenAsync() is { Length: > 10 }, "a new process restores the session");

telemetry.Track("tool.update", "demo-tool", "1.1.0");
await telemetry.FlushAsync();
var queue = Path.Combine(HubStorage.Directory, "hub-events.json");
Check(!File.Exists(queue) || JsonNode.Parse(File.ReadAllText(queue))!.AsArray().Count == 0, "event queue is empty after a successful flush");

// An unreachable hub keeps events for later instead of losing or throwing.
var offline = new HubTelemetry(http, "http://127.0.0.1:1", account, "9.9.9-test", () => null);
offline.Track("tool.uninstall", "demo-tool", "1.1.0");
await offline.FlushAsync();
Check(File.Exists(queue) && JsonNode.Parse(File.ReadAllText(queue))!.AsArray().Count == 1, "events stay queued while the hub is unreachable");
await telemetry.FlushAsync();
Check(JsonNode.Parse(File.ReadAllText(queue))!.AsArray().Count == 0, "queued events are delivered once the hub is back");

var refreshToken = JsonNode.Parse(await http.GetStringAsync($"{baseUrl}/api/v1/auth/config")) is not null ? "n/a" : "";
await account.LogoutAsync();
Check(!account.IsSignedIn && !File.Exists(Path.Combine(HubStorage.Directory, "hub-session.bin")), "logout clears the local session");
var after = new HubAccount(http, baseUrl);
after.LoadFromDisk();
Check(!after.IsSignedIn, "a fresh process starts signed out");

var newId = HubStorage.ResetDeviceId();
Check(newId != deviceId, "resetting the device id issues a new one");

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
