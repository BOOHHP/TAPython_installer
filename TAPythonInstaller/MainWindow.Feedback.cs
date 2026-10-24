using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace TAPythonInstaller;

/// <summary>
/// 反馈建议页：使用 Tool Hub 飞书账号提交反馈，并查看我提交过的反馈及处理状态。
/// </summary>
public partial class MainWindow
{
    private const int FeedbackTitleMaxLength = 80;
    private const int FeedbackBodyMaxLength = 4000;

    private readonly ObservableCollection<FeedbackHistoryItem> feedbackHistoryItems = new();

    private static string FeedbackPageUrl => $"{ToolHubBaseUrl}/#feedback";

    private void InitializeFeedbackPage()
    {
        feedbackHistoryList.ItemsSource = feedbackHistoryItems;
        RemoveLegacyGitHubFeedbackAuth();
        hubAccount.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateFeedbackAuthUi();
            if (hubAccount.IsSignedIn) _ = RefreshFeedbackHistoryAsync(showErrors: false);
            else ClearFeedbackHistory();
        }));
        UpdateFeedbackAuthUi();
        if (hubAccount.IsSignedIn)
            _ = RefreshFeedbackHistoryAsync(showErrors: false);
    }

    // 旧版本保存过 GitHub 令牌，现已不再使用，顺手清掉。
    private static void RemoveLegacyGitHubFeedbackAuth()
    {
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(SettingsFilePath)!, "github-feedback.json");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch
        {
            // 清理失败不影响使用。
        }
    }

    private void UpdateFeedbackAuthUi()
    {
        var user = hubAccount.User;
        if (hubLoginCancellation != null)
        {
            feedbackAuthStatusText.Text = "正在等待浏览器完成飞书授权…";
            feedbackLoginButton.Content = "取消登录";
        }
        else if (user != null)
        {
            feedbackAuthStatusText.Text = $"已登录：{user.Name}";
            feedbackLoginButton.Content = "退出登录";
            feedbackHistoryEmptyText.Text = "还没有提交过反馈。填写左侧内容并提交后会显示在这里。";
        }
        else
        {
            feedbackAuthStatusText.Text = "未登录飞书";
            feedbackLoginButton.Content = "飞书登录";
            feedbackHistoryEmptyText.Text = "登录飞书后可查看我提交过的反馈记录。";
        }
        feedbackLoginButton.IsEnabled = true;
        feedbackHistoryEmptyText.Visibility = feedbackHistoryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void FeedbackLogin_Click(object sender, RoutedEventArgs e)
    {
        if (hubLoginCancellation != null)
        {
            hubLoginCancellation.Cancel();
            return;
        }

        if (hubAccount.IsSignedIn)
        {
            await hubAccount.LogoutAsync();
            Log("已退出 Tool Hub 登录。");
            return;
        }

        UpdateFeedbackAuthUi();
        await SignInAsync();
    }

    private async void SubmitFeedback_Click(object sender, RoutedEventArgs e)
    {
        var content = feedbackContentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            MessageBox.Show(this, "请先填写反馈内容。", "内容为空", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var body = $"{content}\n\n---\n系统：{Environment.OSVersion.VersionString}";
        if (body.Length > FeedbackBodyMaxLength)
        {
            MessageBox.Show(this, $"反馈内容过长（上限约 {FeedbackBodyMaxLength - 120} 字），请精简后再提交。", "内容过长", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        feedbackSubmitButton.IsEnabled = false;
        try
        {
            var token = await hubAccount.GetAccessTokenAsync();
            if (token == null)
            {
                feedbackStatusText.Text = "需要先登录飞书…";
                await SignInAsync();
                token = await hubAccount.GetAccessTokenAsync();
                if (token == null)
                {
                    feedbackStatusText.Text = "未登录，反馈内容已保留，登录后可再次提交";
                    return;
                }
            }

            var firstLine = content.Split('\n')[0].Trim();
            if (firstLine.Length > FeedbackTitleMaxLength) firstLine = firstLine[..FeedbackTitleMaxLength];
            var payload = new JsonObject
            {
                ["kind"] = feedbackTypeIssueRadio.IsChecked == true ? "bug" : "suggestion",
                ["title"] = firstLine,
                ["body"] = body,
                ["client"] = "installer",
                ["clientVersion"] = installerCurrentVersion,
            };
            if (!string.IsNullOrWhiteSpace(detectedEngineVersion)) payload["ueVersion"] = detectedEngineVersion;

            feedbackStatusText.Text = "正在提交到 Tool Hub…";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ToolHubBaseUrl}/api/v1/feedback")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                feedbackStatusText.Text = "登录已失效，请重新登录后提交";
                MessageBox.Show(this, "Tool Hub 登录已失效，请重新登录后再次提交。反馈内容已保留。", "登录失效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeFeedbackError((int)response.StatusCode, responseText));

            Log($"反馈已提交：{firstLine}");
            feedbackStatusText.Text = $"已提交 · {DateTime.Now:HH:mm}，处理进度会通过飞书通知你";
            feedbackContentBox.Text = string.Empty;
            await RefreshFeedbackHistoryAsync(showErrors: false);
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

    private static string DescribeFeedbackError(int status, string responseText)
    {
        string? code = null;
        try
        {
            code = JsonNode.Parse(responseText)?["error"]?.GetValue<string>();
        }
        catch
        {
            // 非 JSON 响应按状态码处理。
        }

        return code switch
        {
            "too_many_feedback" => "提交过于频繁，请稍后再试。",
            "invalid_title" => "标题不能为空。",
            "invalid_body" => "反馈内容不能为空或过长。",
            _ => $"Tool Hub 返回 {status}{(code != null ? $"（{code}）" : "")}",
        };
    }

    private async void RefreshFeedbackHistory_Click(object sender, RoutedEventArgs e)
        => await RefreshFeedbackHistoryAsync(showErrors: true);

    private void ClearFeedbackHistory()
    {
        feedbackHistoryItems.Clear();
        feedbackHistoryCountText.Text = string.Empty;
        UpdateFeedbackAuthUi();
    }

    private async Task RefreshFeedbackHistoryAsync(bool showErrors)
    {
        var token = await hubAccount.GetAccessTokenAsync();
        if (token == null)
        {
            ClearFeedbackHistory();
            return;
        }

        feedbackHistoryRefreshButton.IsEnabled = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ToolHubBaseUrl}/api/v1/me/feedback");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["feedback"]?.AsArray();
            feedbackHistoryItems.Clear();
            foreach (var item in items ?? new JsonArray())
            {
                if (item is not JsonObject feedback) continue;
                var created = feedback["createdAt"]?.GetValue<string>();
                var dateText = DateTimeOffset.TryParse(created, out var at) ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : created ?? "";
                var replies = feedback["replyCount"]?.GetValue<int>() ?? 0;
                var state = FeedbackStatusLabel(feedback["status"]?.GetValue<string>());
                feedbackHistoryItems.Add(new FeedbackHistoryItem(
                    feedback["title"]?.GetValue<string>() ?? "(无标题)",
                    dateText,
                    replies > 0 ? $"{state} · {replies} 条回复" : state,
                    FeedbackPageUrl));
            }

            feedbackHistoryCountText.Text = feedbackHistoryItems.Count > 0 ? $"{feedbackHistoryItems.Count} 条" : "";
            UpdateFeedbackAuthUi();
        }
        catch (Exception ex)
        {
            Log($"刷新反馈历史失败：{ex.Message}");
            if (showErrors)
                MessageBox.Show(this, $"刷新反馈历史失败：{ex.Message}", "刷新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            feedbackHistoryRefreshButton.IsEnabled = true;
        }
    }

    private static string FeedbackStatusLabel(string? status) => status switch
    {
        "in_progress" => "处理中",
        "resolved" => "已解决",
        "wontfix" => "暂不处理",
        _ => "待处理",
    };

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

    private sealed record FeedbackHistoryItem(string Title, string DateText, string StateText, string Url);
}
