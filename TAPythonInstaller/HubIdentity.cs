using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace TAPythonInstaller;

internal sealed class HubLoginException : Exception
{
    public HubLoginException(string message) : base(message) { }
}

internal sealed class HubUser
{
    public string OpenId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Email { get; init; }
    public string? Department { get; init; }
    public string[] Roles { get; init; } = Array.Empty<string>();

    public static HubUser? From(JsonNode? node)
    {
        var openId = node?["openId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(openId)) return null;
        return new HubUser
        {
            OpenId = openId,
            Name = node?["name"]?.GetValue<string>() ?? openId,
            Email = node?["email"]?.GetValue<string>(),
            Department = node?["department"]?.GetValue<string>(),
            Roles = (node?["roles"] as JsonArray)?.Select(role => role?.GetValue<string>() ?? "").Where(role => role.Length > 0).ToArray() ?? Array.Empty<string>()
        };
    }

    public JsonObject ToJson() => new()
    {
        ["openId"] = OpenId,
        ["name"] = Name,
        ["email"] = Email,
        ["department"] = Department,
        ["roles"] = new JsonArray(Roles.Select(role => (JsonNode?)JsonValue.Create(role)).ToArray())
    };
}

internal sealed class HubTokens
{
    public string AccessToken { get; init; } = "";
    public DateTimeOffset AccessExpiresAt { get; init; }
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset RefreshExpiresAt { get; init; }
}

/// <summary>Everything the installer keeps locally about its Hub identity: device id, preferences and the DPAPI-protected session.</summary>
internal static class HubStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TAPythonInstaller/hub-session/v1");
    private static readonly object Gate = new();

    // The environment override exists so automated tests never touch the real per-user installer state.
    public static string Directory { get; } = Environment.GetEnvironmentVariable("TAPYTHON_INSTALLER_DATA_DIR") is { Length: > 0 } overridden
        ? overridden
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TAPythonInstaller");

    private static string DevicePath => Path.Combine(Directory, "hub-device.id");
    private static string PrefsPath => Path.Combine(Directory, "hub-prefs.json");
    private static string SessionPath => Path.Combine(Directory, "hub-session.bin");

    public static string DeviceId
    {
        get
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(DevicePath))
                    {
                        var existing = File.ReadAllText(DevicePath).Trim();
                        if (existing.Length is >= 8 and <= 128) return existing;
                    }
                }
                catch { }

                return WriteNewDeviceId();
            }
        }
    }

    public static string ResetDeviceId()
    {
        lock (Gate) return WriteNewDeviceId();
    }

    private static string WriteNewDeviceId()
    {
        var id = Guid.NewGuid().ToString("D");
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(DevicePath, id);
        }
        catch { }

        return id;
    }

    public static JsonObject LoadPrefs()
    {
        try
        {
            if (File.Exists(PrefsPath)) return JsonNode.Parse(File.ReadAllText(PrefsPath))?.AsObject() ?? new JsonObject();
        }
        catch { }

        return new JsonObject();
    }

    public static void SavePrefs(JsonObject prefs)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(PrefsPath, prefs.ToJsonString());
        }
        catch { }
    }

    public static (HubTokens Tokens, HubUser User)? LoadSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(SessionPath), Entropy, DataProtectionScope.CurrentUser);
            var node = JsonNode.Parse(Encoding.UTF8.GetString(plain));
            var user = HubUser.From(node?["user"]);
            var access = node?["accessToken"]?.GetValue<string>();
            var refresh = node?["refreshToken"]?.GetValue<string>();
            if (user == null || string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return null;
            return (new HubTokens
            {
                AccessToken = access,
                AccessExpiresAt = DateTimeOffset.Parse(node!["accessExpiresAt"]!.GetValue<string>()),
                RefreshToken = refresh,
                RefreshExpiresAt = DateTimeOffset.Parse(node["refreshExpiresAt"]!.GetValue<string>())
            }, user);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveSession(HubTokens tokens, HubUser user)
    {
        try
        {
            var node = new JsonObject
            {
                ["accessToken"] = tokens.AccessToken,
                ["accessExpiresAt"] = tokens.AccessExpiresAt.ToString("o"),
                ["refreshToken"] = tokens.RefreshToken,
                ["refreshExpiresAt"] = tokens.RefreshExpiresAt.ToString("o"),
                ["user"] = user.ToJson()
            };
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(SessionPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(node.ToJsonString()), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { }
    }

    public static void ClearSession()
    {
        try { File.Delete(SessionPath); } catch { }
    }
}

/// <summary>Adds the device id only to requests aimed at the Hub, so it never reaches GitHub or mirrors.</summary>
internal sealed class HubDeviceHandler : DelegatingHandler
{
    private readonly string hubHost;

    public HubDeviceHandler(string hubBaseUrl) : base(new HttpClientHandler())
    {
        hubHost = new Uri(hubBaseUrl).Host;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri != null && string.Equals(request.RequestUri.Host, hubHost, StringComparison.OrdinalIgnoreCase)
            && !request.Headers.Contains("X-Tapython-Device"))
        {
            request.Headers.TryAddWithoutValidation("X-Tapython-Device", HubStorage.DeviceId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

internal sealed class HubAuthConfig
{
    public bool Enabled { get; init; }
    public string? AppId { get; init; }
    public string? AuthorizeUrl { get; init; }
    public string? Scope { get; init; }
}

internal sealed class HubAccount
{
    // The Feishu app must list each of these loopback callbacks as a redirect URL.
    private static readonly int[] CallbackPorts = { 53682, 53683, 53684, 53685, 53686 };

    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private HubTokens? tokens;

    public HubAccount(HttpClient http, string baseUrl)
    {
        this.http = http;
        this.baseUrl = baseUrl.TrimEnd('/');
    }

    internal Func<string, bool>? BrowserLauncher { get; set; }
    internal string? AuthorizeUrlOverride { get; set; }

    public HubUser? User { get; private set; }
    public bool IsSignedIn => User != null && tokens != null;
    public event Action? Changed;

    public void LoadFromDisk()
    {
        var session = HubStorage.LoadSession();
        if (session is { } saved && saved.Tokens.RefreshExpiresAt > DateTimeOffset.UtcNow)
        {
            tokens = saved.Tokens;
            User = saved.User;
        }
        else if (session != null)
        {
            HubStorage.ClearSession();
        }
    }

    public async Task<HubAuthConfig?> GetAuthConfigAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var response = await http.GetAsync($"{baseUrl}/api/v1/auth/config", cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            return new HubAuthConfig
            {
                Enabled = node?["enabled"]?.GetValue<bool>() == true,
                AppId = node?["appId"]?.GetValue<string>(),
                AuthorizeUrl = node?["authorizeUrl"]?.GetValue<string>(),
                Scope = node?["scope"]?.GetValue<string>()
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        var config = await GetAuthConfigAsync();
        var authorizeUrl = AuthorizeUrlOverride ?? config?.AuthorizeUrl;
        if (config is not { Enabled: true } || string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(authorizeUrl))
            throw new HubLoginException("无法连接 Tool Hub，或服务器尚未启用飞书登录。");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = BindCallbackListener(out var port);
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        var query = new List<string>
        {
            $"client_id={Uri.EscapeDataString(config.AppId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"state={state}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256"
        };
        if (!string.IsNullOrWhiteSpace(config.Scope)) query.Add($"scope={Uri.EscapeDataString(config.Scope)}");

        try
        {
            var target = $"{authorizeUrl}?{string.Join("&", query)}";
            if (BrowserLauncher != null) BrowserLauncher(target);
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new HubLoginException($"无法打开浏览器：{ex.Message}");
        }

        var code = await WaitForCallbackAsync(listener, state, cancellationToken);
        var exchange = new JsonObject
        {
            ["code"] = code,
            ["redirectUri"] = redirectUri,
            ["codeVerifier"] = verifier,
            ["client"] = "installer",
            ["deviceId"] = HubStorage.DeviceId
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync($"{baseUrl}/api/v1/auth/feishu/exchange", new StringContent(exchange.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HubLoginException($"无法连接 Tool Hub：{ex.Message}");
        }

        using (response)
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!response.IsSuccessStatusCode) throw new HubLoginException(DescribeLoginError(body?["error"]?.GetValue<string>()));
            Adopt(body);
        }
    }

    public async Task LogoutAsync()
    {
        var current = tokens;
        tokens = null;
        User = null;
        HubStorage.ClearSession();
        Changed?.Invoke();
        if (current == null) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/auth/logout")
            {
                Content = new StringContent(new JsonObject { ["refreshToken"] = current.RefreshToken }.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var _ = await http.SendAsync(request, cts.Token);
        }
        catch { }
    }

    /// <summary>Returns a usable access token, refreshing it when it is about to expire; null means signed out.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        if (tokens == null) return null;
        if (tokens.AccessExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return tokens.AccessToken;

        await refreshGate.WaitAsync();
        try
        {
            var current = tokens;
            if (current == null) return null;
            if (current.AccessExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return current.AccessToken;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var payload = new JsonObject { ["refreshToken"] = current.RefreshToken };
                using var response = await http.PostAsync($"{baseUrl}/api/v1/auth/refresh", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    Adopt(JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token)));
                    return tokens?.AccessToken;
                }

                // Rejected refresh token (expired, revoked, account disabled): fall back to signed-out.
                if ((int)response.StatusCode is 400 or 401 or 403)
                {
                    tokens = null;
                    User = null;
                    HubStorage.ClearSession();
                    Changed?.Invoke();
                }
            }
            catch { }

            return null;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>Keeps name and roles current after the administrator changes them.</summary>
    public async Task RefreshProfileAsync()
    {
        var token = await GetAccessTokenAsync();
        if (token == null) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return;
            var user = HubUser.From(JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token))?["user"]);
            if (user != null && tokens != null)
            {
                User = user;
                HubStorage.SaveSession(tokens, user);
                Changed?.Invoke();
            }
        }
        catch { }
    }

    private void Adopt(JsonNode? body)
    {
        var user = HubUser.From(body?["user"]);
        var access = body?["accessToken"]?.GetValue<string>();
        var refresh = body?["refreshToken"]?.GetValue<string>();
        if (user == null || string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            throw new HubLoginException("服务器返回的登录信息不完整。");

        tokens = new HubTokens
        {
            AccessToken = access,
            AccessExpiresAt = DateTimeOffset.Parse(body!["accessExpiresAt"]!.GetValue<string>()),
            RefreshToken = refresh,
            RefreshExpiresAt = DateTimeOffset.Parse(body["refreshExpiresAt"]!.GetValue<string>())
        };
        User = user;
        HubStorage.SaveSession(tokens, user);
        Changed?.Invoke();
    }

    private static TcpListener BindCallbackListener(out int port)
    {
        foreach (var candidate in CallbackPorts)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (SocketException) { }
        }

        throw new HubLoginException("本机的登录回调端口（53682-53686）都被占用，请关闭占用程序后重试。");
    }

    private static async Task<string> WaitForCallbackAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var target = await ReadRequestTargetAsync(client, timeout.Token);
                if (target == null || !target.StartsWith("/callback", StringComparison.Ordinal))
                {
                    await RespondAsync(client, 404, "Not found", timeout.Token);
                    continue;
                }

                var parameters = System.Web.HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + target).Query);
                if (parameters["state"] != expectedState)
                {
                    await RespondAsync(client, 400, "登录请求已失效，请回到安装器重新点击登录。", timeout.Token);
                    continue;
                }

                if (!string.IsNullOrEmpty(parameters["error"]))
                {
                    await RespondAsync(client, 200, "已取消授权，可以关闭此页面。", timeout.Token);
                    throw new HubLoginException("已取消飞书授权。");
                }

                var code = parameters["code"];
                if (string.IsNullOrEmpty(code))
                {
                    await RespondAsync(client, 400, "没有收到授权码，请重试。", timeout.Token);
                    continue;
                }

                await RespondAsync(client, 200, "登录成功，可以关闭此页面并回到 TAPython 安装器。", timeout.Token);
                return code;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HubLoginException("等待授权超时，请重新登录。");
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        var length = 0;
        var stream = client.GetStream();
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), limit.Token);
                if (read == 0) break;
                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n")) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var requestLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
        return requestLine.Length >= 2 && requestLine[0] == "GET" ? requestLine[1] : null;
    }

    private static async Task RespondAsync(TcpClient client, int status, string message, CancellationToken cancellationToken)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>TAPython</title></head><body style=\"font-family:sans-serif;padding:48px\"><h3>{WebUtility.HtmlEncode(message)}</h3></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string DescribeLoginError(string? code) => code switch
    {
        "invalid_code" => "授权码无效或已过期，请重新登录。",
        "account_disabled" => "该账号已停用，无法登录。",
        "email_conflict" => "该邮箱已绑定另一个账号，请联系管理员。",
        "feishu_unavailable" => "服务器暂时无法连接飞书，请稍后再试。",
        "rate_limited" => "登录尝试过于频繁，请稍后再试。",
        _ => $"登录失败（{code ?? "未知错误"}）。"
    };

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
