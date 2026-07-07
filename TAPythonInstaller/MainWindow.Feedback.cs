using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace TAPythonInstaller;

/// <summary>
/// 反馈建议页：GitHub Device Flow / PAT 登录、提交 Issue 到 BOOHHP/TAPython_installer、我的反馈历史。
/// </summary>
public partial class MainWindow
{
    private const string FeedbackOAuthClientId = "Ov23lilHDl3ElW23Q7BM";
    private const string FeedbackDeviceCodeUrl = "https://github.com/login/device/code";
    private const string FeedbackAccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string FeedbackUserApiUrl = "https://api.github.com/user";
    private const string FeedbackIssuesApiUrl = "https://api.github.com/repos/BOOHHP/TAPython_installer/issues";

    private static string FeedbackAuthFilePath => Path.Combine(Path.GetDirectoryName(SettingsFilePath)!, "github-feedback.json");

    private readonly ObservableCollection<FeedbackHistoryItem> feedbackHistoryItems = new();
    private string? feedbackToken;
    private string? feedbackUserLogin;
    private CancellationTokenSource? feedbackLoginCts;

    private void InitializeFeedbackPage()
    {
        feedbackHistoryList.ItemsSource = feedbackHistoryItems;
        TryLoadStoredFeedbackAuth();
        UpdateFeedbackAuthUi();
        if (!string.IsNullOrWhiteSpace(feedbackToken))
            _ = RefreshFeedbackHistoryAsync(showErrors: false);
    }

    // ─── 登录状态 UI ───────────────────────────────────────────

    private void UpdateFeedbackAuthUi()
    {
        var loggingIn = feedbackLoginCts != null;
        if (loggingIn)
        {
            feedbackLoginButton.Content = "取消登录";
            feedbackLoginButton.IsEnabled = true;
            return;
        }

        if (!string.IsNullOrWhiteSpace(feedbackToken))
        {
            feedbackAuthStatusText.Text = $"已登录：{feedbackUserLogin}";
            feedbackLoginButton.Content = "退出登录";
            if (string.IsNullOrWhiteSpace(feedbackNameBox.Text))
                feedbackNameBox.Text = feedbackUserLogin;
            feedbackHistoryEmptyText.Text = "还没有提交过反馈。填写左侧内容并提交后会显示在这里。";
        }
        else
        {
            feedbackAuthStatusText.Text = "未登录 GitHub";
            feedbackLoginButton.Content = "登录 GitHub";
            feedbackHistoryEmptyText.Text = "登录 GitHub 后可查看我提交过的反馈记录。";
        }
        feedbackLoginButton.IsEnabled = true;
        feedbackHistoryEmptyText.Visibility = feedbackHistoryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void FeedbackLogin_Click(object sender, RoutedEventArgs e)
    {
        if (feedbackLoginCts != null)
        {
            feedbackLoginCts.Cancel();
            return;
        }

        if (!string.IsNullOrWhiteSpace(feedbackToken))
        {
            feedbackToken = null;
            feedbackUserLogin = null;
            ClearStoredFeedbackAuth();
            feedbackHistoryItems.Clear();
            UpdateFeedbackAuthUi();
            Log("已退出 GitHub 登录。");
            return;
        }

        var success = await StartDeviceFlowLoginAsync();
        if (success)
            await RefreshFeedbackHistoryAsync(showErrors: false);
    }

    private void FeedbackPatToggle_Click(object sender, RoutedEventArgs e)
    {
        feedbackPatPanel.Visibility = feedbackPatPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void FeedbackPatApply_Click(object sender, RoutedEventArgs e)
    {
        var token = feedbackPatBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show(this, "请先粘贴 GitHub Personal Access Token（需 public_repo 权限）。", "缺少令牌", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            feedbackAuthStatusText.Text = "正在验证令牌...";
            var login = await FetchGitHubLoginAsync(token, CancellationToken.None);
            feedbackToken = token;
            feedbackUserLogin = login;
            SaveFeedbackAuth(token, login);
            feedbackPatBox.Text = string.Empty;
            feedbackPatPanel.Visibility = Visibility.Collapsed;
            Log($"GitHub 令牌登录成功：{login}");
            UpdateFeedbackAuthUi();
            await RefreshFeedbackHistoryAsync(showErrors: false);
        }
        catch (Exception ex)
        {
            feedbackAuthStatusText.Text = "未登录 GitHub";
            MessageBox.Show(this, $"令牌验证失败：{ex.Message}", "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ─── Device Flow ──────────────────────────────────────────

    private async Task<bool> StartDeviceFlowLoginAsync()
    {
        feedbackLoginCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = feedbackLoginCts.Token;
        UpdateFeedbackAuthUi();

        try
        {
            feedbackAuthStatusText.Text = "正在获取登录码...";
            using var codeRequest = new HttpRequestMessage(HttpMethod.Post, FeedbackDeviceCodeUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = FeedbackOAuthClientId,
                    ["scope"] = "public_repo",
                })
            };
            codeRequest.Headers.Accept.ParseAdd("application/json");
            using var codeResponse = await httpClient.SendAsync(codeRequest, ct);
            codeResponse.EnsureSuccessStatusCode();
            var codeJson = JsonNode.Parse(await codeResponse.Content.ReadAsStringAsync(ct))!.AsObject();
            var deviceCode = codeJson["device_code"]!.GetValue<string>();
            var userCode = codeJson["user_code"]!.GetValue<string>();
            var verificationUri = codeJson["verification_uri"]?.GetValue<string>() ?? "https://github.com/login/device";
            var interval = Math.Max(codeJson["interval"]?.GetValue<int>() ?? 5, 5);

            TryCopyTextToClipboard(userCode);
            feedbackAuthStatusText.Text = $"浏览器中输入代码 {userCode}（已复制）";
            Log($"GitHub 登录：请在浏览器输入代码 {userCode}（已复制到剪贴板）。");
            try
            {
                Process.Start(new ProcessStartInfo(verificationUri) { UseShellExecute = true });
            }
            catch
            {
                Log($"无法自动打开浏览器，请手动访问：{verificationUri}");
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);

                using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, FeedbackAccessTokenUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = FeedbackOAuthClientId,
                        ["device_code"] = deviceCode,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    })
                };
                tokenRequest.Headers.Accept.ParseAdd("application/json");
                using var tokenResponse = await httpClient.SendAsync(tokenRequest, ct);
                var tokenJson = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(ct))!.AsObject();

                var error = tokenJson["error"]?.GetValue<string>();
                if (string.IsNullOrEmpty(error))
                {
                    var token = tokenJson["access_token"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("GitHub 未返回访问令牌");

                    var login = await FetchGitHubLoginAsync(token, ct);
                    feedbackToken = token;
                    feedbackUserLogin = login;
                    SaveFeedbackAuth(token, login);
                    Log($"GitHub 登录成功：{login}");
                    return true;
                }

                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "expired_token":
                        throw new InvalidOperationException("登录码已过期，请重新点击登录");
                    case "access_denied":
                        throw new InvalidOperationException("已在浏览器中拒绝授权");
                    default:
                        throw new InvalidOperationException($"GitHub 登录失败：{error}");
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            Log("GitHub 登录已取消或超时。");
            return false;
        }
        catch (HttpRequestException ex)
        {
            var detail = DescribeException(ex);
            Log($"GitHub 登录失败：{detail}");
            feedbackPatPanel.Visibility = Visibility.Visible;
            MessageBox.Show(this,
                "无法连接 GitHub 登录服务（github.com），当前网络可能存在代理或防火墙拦截。\n\n" +
                "请改用「Token 登录」：\n" +
                "1. 点击 PAT 面板中的「创建令牌」，在浏览器中生成 public_repo 权限的令牌\n" +
                "2. 复制令牌粘贴到访问令牌框，点「使用令牌」\n\n" +
                $"错误详情：{detail}",
                "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Log($"GitHub 登录失败：{DescribeException(ex)}");
            MessageBox.Show(this, $"GitHub 登录失败：{ex.Message}", "登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            feedbackLoginCts?.Dispose();
            feedbackLoginCts = null;
            UpdateFeedbackAuthUi();
        }
    }

    private static string DescribeException(Exception ex)
    {
        var parts = new List<string>();
        for (var current = (Exception?)ex; current != null; current = current.InnerException)
            parts.Add(current.Message);
        return string.Join(" → ", parts.Distinct());
    }

    private void FeedbackPatCreate_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://github.com/settings/tokens/new?scopes=public_repo&description=TAPython+Installer";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            TryCopyTextToClipboard(url);
            MessageBox.Show(this, $"无法自动打开浏览器，令牌创建地址已复制到剪贴板：\n{url}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task<string> FetchGitHubLoginAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedbackUserApiUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
        return json["login"]?.GetValue<string>() ?? throw new InvalidOperationException("GitHub 用户信息缺少 login 字段");
    }

    // ─── 提交反馈 ─────────────────────────────────────────────

    private async void SubmitFeedback_Click(object sender, RoutedEventArgs e)
    {
        var content = feedbackContentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            MessageBox.Show(this, "请先填写反馈内容。", "内容为空", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        feedbackSubmitButton.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(feedbackToken))
            {
                feedbackStatusText.Text = "需要先登录 GitHub...";
                var success = await StartDeviceFlowLoginAsync();
                if (!success || string.IsNullOrWhiteSpace(feedbackToken))
                {
                    feedbackStatusText.Text = "未登录，反馈内容已保留，登录后可再次提交";
                    return;
                }
            }

            var isIssue = feedbackTypeIssueRadio.IsChecked == true;
            var prefix = isIssue ? "[问题反馈]" : "[功能建议]";
            var firstLine = content.Split('\n')[0].Trim();
            if (firstLine.Length > 60) firstLine = firstLine[..60];
            var title = $"{prefix} {firstLine}";

            var name = feedbackNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = feedbackUserLogin ?? "未填写";

            var body = content +
                       "\n\n---\n" +
                       $"- 反馈人：{name}\n" +
                       $"- 安装器版本：v{installerCurrentVersion}\n" +
                       $"- UE 版本：{detectedEngineVersion ?? "未检测"}\n" +
                       $"- 系统：{Environment.OSVersion.VersionString}";

            feedbackStatusText.Text = "正在提交到 GitHub Issues...";
            var payload = new JsonObject { ["title"] = title, ["body"] = body };

            using var request = new HttpRequestMessage(HttpMethod.Post, FeedbackIssuesApiUrl)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", feedbackToken);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                feedbackToken = null;
                feedbackUserLogin = null;
                ClearStoredFeedbackAuth();
                UpdateFeedbackAuthUi();
                feedbackStatusText.Text = "登录已失效，请重新登录后提交";
                MessageBox.Show(this, "GitHub 登录已失效，请重新登录后再次提交。反馈内容已保留。", "登录失效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub 返回 {(int)response.StatusCode}：{TryExtractGitHubErrorMessage(responseText)}");

            var issueJson = JsonNode.Parse(responseText)!.AsObject();
            var issueUrl = issueJson["html_url"]?.GetValue<string>() ?? string.Empty;
            var issueNumber = issueJson["number"]?.GetValue<int>() ?? 0;

            AppendLocalFeedbackCache(title, issueUrl, DateTimeOffset.Now);
            Log($"反馈已提交：#{issueNumber} {title}");
            feedbackStatusText.Text = $"已提交 #{issueNumber} · {DateTime.Now:HH:mm}";
            feedbackContentBox.Text = string.Empty;

            await RefreshFeedbackHistoryAsync(showErrors: false);

            var open = MessageBox.Show(this, $"反馈已提交到 GitHub Issues（#{issueNumber}）。\n\n是否在浏览器中查看？", "提交成功", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(issueUrl))
                Process.Start(new ProcessStartInfo(issueUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            feedbackStatusText.Text = "提交失败，内容已保留";
            MessageBox.Show(this, $"提交反馈失败：{ex.Message}\n\n反馈内容已保留，可稍后重试。", "提交失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            feedbackSubmitButton.IsEnabled = true;
        }
    }

    private static string TryExtractGitHubErrorMessage(string responseText)
    {
        try
        {
            return JsonNode.Parse(responseText)?["message"]?.GetValue<string>() ?? responseText;
        }
        catch
        {
            return responseText;
        }
    }

    // ─── 历史记录 ─────────────────────────────────────────────

    private async void RefreshFeedbackHistory_Click(object sender, RoutedEventArgs e)
        => await RefreshFeedbackHistoryAsync(showErrors: true);

    private async Task RefreshFeedbackHistoryAsync(bool showErrors)
    {
        if (string.IsNullOrWhiteSpace(feedbackToken) || string.IsNullOrWhiteSpace(feedbackUserLogin))
        {
            LoadLocalFeedbackCacheIntoList();
            return;
        }

        feedbackHistoryRefreshButton.IsEnabled = false;
        try
        {
            var url = $"{FeedbackIssuesApiUrl}?creator={Uri.EscapeDataString(feedbackUserLogin)}&state=all&per_page=50&sort=created&direction=desc";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", feedbackToken);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
            feedbackHistoryItems.Clear();
            foreach (var item in items)
            {
                if (item is not JsonObject issue) continue;
                if (issue.ContainsKey("pull_request")) continue;

                var title = issue["title"]?.GetValue<string>() ?? "(无标题)";
                var createdAt = issue["created_at"]?.GetValue<string>();
                var state = issue["state"]?.GetValue<string>() ?? "open";
                var htmlUrl = issue["html_url"]?.GetValue<string>() ?? string.Empty;
                var dateText = DateTimeOffset.TryParse(createdAt, out var created)
                    ? created.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : createdAt ?? "";

                feedbackHistoryItems.Add(new FeedbackHistoryItem(title, dateText,
                    string.Equals(state, "open", StringComparison.OrdinalIgnoreCase) ? "处理中" : "已关闭", htmlUrl));
            }

            feedbackHistoryCountText.Text = feedbackHistoryItems.Count > 0 ? $"{feedbackHistoryItems.Count} 条" : "";
            feedbackHistoryEmptyText.Visibility = feedbackHistoryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log($"刷新反馈历史失败：{ex.Message}");
            LoadLocalFeedbackCacheIntoList(offline: true);
            if (showErrors)
                MessageBox.Show(this, $"刷新反馈历史失败：{ex.Message}\n\n已显示本地缓存记录。", "刷新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            feedbackHistoryRefreshButton.IsEnabled = true;
        }
    }

    private void FeedbackHistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (feedbackHistoryList.SelectedItem is not FeedbackHistoryItem item || string.IsNullOrWhiteSpace(item.Url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开链接：{ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ─── 本地缓存与令牌存储 ────────────────────────────────────

    private void LoadLocalFeedbackCacheIntoList(bool offline = false)
    {
        feedbackHistoryItems.Clear();
        try
        {
            if (File.Exists(FeedbackAuthFilePath) &&
                JsonNode.Parse(File.ReadAllText(FeedbackAuthFilePath))?.AsObject()["localHistory"] is JsonArray history)
            {
                foreach (var entry in history.Reverse())
                {
                    if (entry is not JsonObject record) continue;
                    var title = record["title"]?.GetValue<string>() ?? "(无标题)";
                    var date = record["createdAt"]?.GetValue<string>() ?? "";
                    var url = record["url"]?.GetValue<string>() ?? "";
                    feedbackHistoryItems.Add(new FeedbackHistoryItem(title, date, offline ? "离线缓存" : "本地记录", url));
                }
            }
        }
        catch
        {
            // 缓存损坏时静默忽略，不影响主流程。
        }
        feedbackHistoryCountText.Text = feedbackHistoryItems.Count > 0 ? $"{feedbackHistoryItems.Count} 条" : "";
        feedbackHistoryEmptyText.Visibility = feedbackHistoryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLocalFeedbackCache(string title, string url, DateTimeOffset createdAt)
    {
        try
        {
            var root = ReadFeedbackAuthFile();
            if (root["localHistory"] is not JsonArray history)
            {
                history = new JsonArray();
                root["localHistory"] = history;
            }
            history.Add(new JsonObject
            {
                ["title"] = title,
                ["url"] = url,
                ["createdAt"] = createdAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            });
            while (history.Count > 100) history.RemoveAt(0);
            WriteFeedbackAuthFile(root);
        }
        catch (Exception ex)
        {
            Log($"写入反馈本地缓存失败：{ex.Message}");
        }
    }

    private void TryLoadStoredFeedbackAuth()
    {
        try
        {
            if (!File.Exists(FeedbackAuthFilePath)) return;
            var root = JsonNode.Parse(File.ReadAllText(FeedbackAuthFilePath))?.AsObject();
            var protectedToken = root?["token"]?.GetValue<string>();
            var login = root?["login"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(protectedToken) || string.IsNullOrWhiteSpace(login)) return;

            var tokenBytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), null, DataProtectionScope.CurrentUser);
            feedbackToken = Encoding.UTF8.GetString(tokenBytes);
            feedbackUserLogin = login;
        }
        catch (Exception ex)
        {
            Log($"读取 GitHub 登录状态失败：{ex.Message}");
            feedbackToken = null;
            feedbackUserLogin = null;
        }
    }

    private void SaveFeedbackAuth(string token, string login)
    {
        try
        {
            var root = ReadFeedbackAuthFile();
            root["token"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
            root["login"] = login;
            WriteFeedbackAuthFile(root);
        }
        catch (Exception ex)
        {
            Log($"保存 GitHub 登录状态失败：{ex.Message}");
        }
    }

    private void ClearStoredFeedbackAuth()
    {
        try
        {
            var root = ReadFeedbackAuthFile();
            root.Remove("token");
            root.Remove("login");
            WriteFeedbackAuthFile(root);
        }
        catch (Exception ex)
        {
            Log($"清除 GitHub 登录状态失败：{ex.Message}");
        }
    }

    private static JsonObject ReadFeedbackAuthFile()
    {
        try
        {
            if (File.Exists(FeedbackAuthFilePath))
                return JsonNode.Parse(File.ReadAllText(FeedbackAuthFilePath))?.AsObject() ?? new JsonObject();
        }
        catch
        {
            // 文件损坏时重建。
        }
        return new JsonObject();
    }

    private static void WriteFeedbackAuthFile(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FeedbackAuthFilePath)!);
        File.WriteAllText(FeedbackAuthFilePath, root.ToJsonString(IndentedJsonOptions));
    }

    private sealed record FeedbackHistoryItem(string Title, string DateText, string StateText, string Url);
}
