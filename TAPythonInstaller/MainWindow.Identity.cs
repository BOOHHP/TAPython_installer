using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TAPythonInstaller;

public partial class MainWindow
{
    private const int LoginBannerMaxLaunches = 3;

    private HubAccount hubAccount = null!;
    private HubTelemetry hubTelemetry = null!;
    private CancellationTokenSource? hubLoginCancellation;
    private bool hubLoginEnabled;

    private void InitializeIdentity()
    {
        hubAccount = new HubAccount(httpClient, ToolHubBaseUrl);
        hubTelemetry = new HubTelemetry(httpClient, ToolHubBaseUrl, hubAccount, GetCurrentInstallerVersion(), () => detectedEngineVersion);
        hubAccount.Changed += () => Dispatcher.BeginInvoke(new Action(UpdateAccountUi));
        hubAccount.LoadFromDisk();
        UpdateAccountUi();
        Loaded += async (_, _) => await StartIdentityAsync();
    }

    private async Task StartIdentityAsync()
    {
        try
        {
            hubTelemetry.Track("app.launch");
            var config = await hubAccount.GetAuthConfigAsync();
            hubLoginEnabled = config?.Enabled == true;
            if (hubAccount.IsSignedIn) await hubAccount.RefreshProfileAsync();
            UpdateAccountUi();
            ShowLoginBannerIfNeeded();
            _ = hubTelemetry.FlushAsync();
        }
        catch (Exception ex)
        {
            Log($"Tool Hub 账号初始化失败：{ex.Message}");
        }
    }

    // Shown on the first few launches only, never as a modal dialog, and not again once dismissed.
    private void ShowLoginBannerIfNeeded()
    {
        if (!hubLoginEnabled || hubAccount.IsSignedIn) return;
        var prefs = HubStorage.LoadPrefs();
        var launches = prefs["loginBannerLaunches"]?.GetValue<int>() ?? 0;
        if (prefs["loginBannerDismissed"]?.GetValue<bool>() == true || launches >= LoginBannerMaxLaunches) return;
        prefs["loginBannerLaunches"] = launches + 1;
        HubStorage.SavePrefs(prefs);
        accountBanner.Visibility = Visibility.Visible;
    }

    private void UpdateAccountUi()
    {
        var user = hubAccount.User;
        accountGlyph.Text = user != null && user.Name.Length > 0 ? user.Name[..1] : "\uE77B";
        accountGlyph.FontFamily = user != null ? new System.Windows.Media.FontFamily("Microsoft YaHei UI") : new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
        accountGlyph.FontSize = user != null ? 14 : 15;
        accountButton.ToolTip = user != null ? $"{user.Name}（{FormatRoles(user)}）" : "飞书登录";
        if (user != null) accountBanner.Visibility = Visibility.Collapsed;
    }

    private static string FormatRoles(HubUser user)
    {
        var labels = user.Roles.Select(role => role == "admin" ? "管理员" : role == "reviewer" ? "评审员" : role).ToArray();
        return labels.Length == 0 ? "成员" : string.Join("、", labels);
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = accountButton, Placement = PlacementMode.Bottom };
        if (hubLoginCancellation != null)
        {
            menu.Items.Add(MenuItemFor("取消登录", () => hubLoginCancellation?.Cancel()));
        }
        else if (hubAccount.User is { } user)
        {
            menu.Items.Add(new MenuItem { Header = $"{user.Name}（{FormatRoles(user)}）", IsEnabled = false });
            if (!string.IsNullOrWhiteSpace(user.Department)) menu.Items.Add(new MenuItem { Header = user.Department, IsEnabled = false });
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("退出登录", () => _ = hubAccount.LogoutAsync()));
        }
        else
        {
            menu.Items.Add(MenuItemFor("飞书登录", () => _ = SignInAsync()));
        }

        menu.Items.Add(MenuItemFor("重置设备 ID", ResetHubDeviceId));
        menu.IsOpen = true;
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task SignInAsync()
    {
        if (hubLoginCancellation != null) return;
        hubLoginCancellation = new CancellationTokenSource();
        accountBanner.Visibility = Visibility.Collapsed;
        accountButton.ToolTip = "正在等待浏览器完成飞书授权…（点击可取消）";
        try
        {
            await hubAccount.LoginAsync(hubLoginCancellation.Token);
            Log($"已登录 Tool Hub：{hubAccount.User?.Name}");
            _ = hubTelemetry.FlushAsync();
        }
        catch (OperationCanceledException)
        {
            Log("已取消 Tool Hub 登录。");
        }
        catch (HubLoginException ex)
        {
            Log($"Tool Hub 登录失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "飞书登录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log($"Tool Hub 登录失败：{ex.Message}");
            MessageBox.Show(this, $"登录失败：{ex.Message}", "飞书登录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            hubLoginCancellation?.Dispose();
            hubLoginCancellation = null;
            UpdateAccountUi();
        }
    }

    private void ResetHubDeviceId()
    {
        var confirm = MessageBox.Show(this, "重置后，这台电脑会被当作一台新设备统计，之前匿名上报的记录不会再关联到后续使用。确定重置设备 ID？", "重置设备 ID", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        HubStorage.ResetDeviceId();
        Log("已重置 Tool Hub 设备 ID。");
    }

    private void LoginBannerSignIn_Click(object sender, RoutedEventArgs e) => _ = SignInAsync();

    private void LoginBannerLater_Click(object sender, RoutedEventArgs e) => accountBanner.Visibility = Visibility.Collapsed;

    private void LoginBannerNever_Click(object sender, RoutedEventArgs e)
    {
        var prefs = HubStorage.LoadPrefs();
        prefs["loginBannerDismissed"] = true;
        HubStorage.SavePrefs(prefs);
        accountBanner.Visibility = Visibility.Collapsed;
    }

    private void TrackHubOperation(string type, HubToolInfo tool)
    {
        hubTelemetry.Track(type, tool.Slug, tool.LatestVersion);
        _ = hubTelemetry.FlushAsync();
    }
}
