using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TAPythonInstaller;

/// <summary>
/// Collects install, update, uninstall and launch events and sends them to the Hub in idempotent batches.
/// Only tool slug, version, UE version and installer version are recorded; no paths, files or script content.
/// Reporting is best effort and must never get in the way of the installer.
/// </summary>
internal sealed class HubTelemetry
{
    private const int MaxQueued = 500;
    private const int BatchSize = 50;

    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly HubAccount account;
    private readonly string clientVersion;
    private readonly Func<string?> ueVersion;
    private readonly object gate = new();
    private readonly SemaphoreSlim flushGate = new(1, 1);
    private readonly string queuePath = Path.Combine(HubStorage.Directory, "hub-events.json");

    public HubTelemetry(HttpClient http, string baseUrl, HubAccount account, string clientVersion, Func<string?> ueVersion)
    {
        this.http = http;
        this.baseUrl = baseUrl.TrimEnd('/');
        this.account = account;
        this.clientVersion = clientVersion;
        this.ueVersion = ueVersion;
    }

    public void Track(string type, string? slug = null, string? version = null)
    {
        try
        {
            var item = new JsonObject
            {
                ["eventId"] = Guid.NewGuid().ToString("D"),
                ["type"] = type,
                ["at"] = DateTimeOffset.UtcNow.ToString("o")
            };
            if (!string.IsNullOrWhiteSpace(slug)) item["slug"] = slug;
            if (!string.IsNullOrWhiteSpace(version)) item["version"] = version;
            if (!string.IsNullOrWhiteSpace(ueVersion())) item["ue"] = ueVersion();

            lock (gate)
            {
                var queue = ReadQueue();
                queue.Add(item);
                while (queue.Count > MaxQueued) queue.RemoveAt(0);
                WriteQueue(queue);
            }
        }
        catch { }
    }

    public async Task FlushAsync()
    {
        if (!await flushGate.WaitAsync(0)) return;
        try
        {
            while (true)
            {
                List<JsonNode?> batch;
                lock (gate)
                {
                    batch = ReadQueue().Take(BatchSize).ToList();
                }

                if (batch.Count == 0) return;
                if (!await SendAsync(batch)) return;
                lock (gate)
                {
                    var queue = ReadQueue();
                    var sent = batch.Select(item => item?["eventId"]?.GetValue<string>()).ToHashSet();
                    var remaining = new JsonArray(queue.Where(item => !sent.Contains(item?["eventId"]?.GetValue<string>())).Select(item => item?.DeepClone()).ToArray());
                    WriteQueue(remaining);
                }
            }
        }
        catch { }
        finally
        {
            flushGate.Release();
        }
    }

    // True means the batch is finished with (delivered or permanently rejected) and may leave the queue.
    private async Task<bool> SendAsync(List<JsonNode?> batch)
    {
        var events = new JsonArray();
        string? latestUe = null;
        foreach (var item in batch)
        {
            if (item == null) continue;
            var copy = new JsonObject();
            foreach (var pair in item.AsObject())
            {
                if (pair.Key == "ue") latestUe = pair.Value?.GetValue<string>();
                else copy[pair.Key] = pair.Value?.DeepClone();
            }

            events.Add(copy);
        }

        var body = new JsonObject
        {
            ["deviceId"] = HubStorage.DeviceId,
            ["client"] = "installer",
            ["clientVersion"] = clientVersion,
            ["events"] = events
        };
        if (!string.IsNullOrWhiteSpace(latestUe)) body["ueVersion"] = latestUe;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/events")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            var token = await account.GetAccessTokenAsync();
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await http.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode) return true;
            // A 400 will never succeed on retry; anything else (404 while the feature is off, 429, 5xx) is kept for later.
            return response.StatusCode == HttpStatusCode.BadRequest;
        }
        catch
        {
            return false;
        }
    }

    private JsonArray ReadQueue()
    {
        try
        {
            if (File.Exists(queuePath)) return JsonNode.Parse(File.ReadAllText(queuePath))?.AsArray() ?? new JsonArray();
        }
        catch { }

        return new JsonArray();
    }

    private void WriteQueue(JsonArray queue)
    {
        Directory.CreateDirectory(HubStorage.Directory);
        File.WriteAllText(queuePath, queue.ToJsonString());
    }
}
