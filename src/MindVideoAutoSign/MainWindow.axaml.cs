using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MindVideoAutoSign.Models;
using MindVideoAutoSign.Services;

namespace MindVideoAutoSign;

public partial class MainWindow : Window
{
    private readonly string _workspace = FindWorkspace();
    private readonly AccountCatalog _accountCatalog;
    private readonly GitHubActionsService _github;
    private readonly MindVideoApiService _api = new();
    private readonly FileAccountStore _localTokens = new();
    private readonly StreakStore _streaks;
    private readonly ChromeProfileStore _chromeProfiles;
    private readonly Dictionary<int, TextBox> _aliasInputs = [];
    private readonly Dictionary<int, string> _aliases;
    private readonly Dictionary<int, string> _tokens = [];
    private readonly Dictionary<int, ChromeProfileConfig> _chromeByAccount = [];
    private bool _chromeUiLoading;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _accountCatalog = AccountCatalog.Load(_workspace);
            _github = new GitHubActionsService(_accountCatalog);
            _streaks = new StreakStore(_accountCatalog);
            _chromeProfiles = new ChromeProfileStore(_workspace);
            foreach (var pair in _chromeProfiles.LoadAll())
            {
                if (_accountCatalog.IsEnabled(pair.Key))
                    _chromeByAccount[pair.Key] = pair.Value;
            }

            _aliases = LoadAliases();
            RefreshAccountComboLabels();

            BuildAliasList();
            if (ConfiguredMetric is not null)
                ConfiguredMetric.Text = $"0/{_accountCatalog.EnabledCount} 個";
            _ = LoadLocalTokensAsync();
            UpdateAccountDisplay();
            LoadCachedStreakDashboard();
            Opened += (_, _) =>
            {
                try
                {
                    var folder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MindVideo Auto Sign",
                        "logs");
                    Directory.CreateDirectory(folder);
                    File.AppendAllText(
                        Path.Combine(folder, "startup-crash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainWindow opened{Environment.NewLine}");
                }
                catch
                {
                    // ignore
                }
            };
        }
        catch (Exception ex)
        {
            // Keep window open with a visible error instead of process exit.
            if (LoginStatus is not null)
                LoginStatus.Text = $"介面初始化失敗：{ex.Message}";
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MindVideo Auto Sign",
                    "logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(
                    Path.Combine(folder, "startup-crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainWindow init{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // ignore
            }
            throw;
        }
    }

    private IReadOnlyList<AccountDefinition> EnabledAccounts => _accountCatalog.EnabledAccounts;

    private int AccountNumber
    {
        get
        {
            var index = Math.Clamp(AccountComboBox?.SelectedIndex ?? 0, 0, EnabledAccounts.Count - 1);
            return EnabledAccounts[index].Number;
        }
    }
    private string SecretName => $"MINDVIDEO_TOKEN{AccountNumber}";
    /// <summary>Preferred local capture path: mindvideo-token-01-alias.txt (alias suffix when set).</summary>
    private string TokenFile => GetPreferredTokenFilePath(AccountNumber);
    private static string AliasFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MindVideoFlow",
        "account-aliases.json");

    private string LogsDir => Path.Combine(_workspace, "logs");

    /// <summary>
    /// Local token files use an account-alias suffix when available, e.g.
    /// <c>mindvideo-token-01-feng33feng35feng3.txt</c>. Falls back to
    /// <c>mindvideo-token-01.txt</c> when no alias is set.
    /// </summary>
    private string GetPreferredTokenFilePath(int accountNumber)
    {
        var prefix = $"mindvideo-token-{accountNumber:00}";
        var suffix = SanitizeFileSuffix(_aliases.GetValueOrDefault(accountNumber));
        var fileName = string.IsNullOrWhiteSpace(suffix)
            ? $"{prefix}.txt"
            : $"{prefix}-{suffix}.txt";
        return Path.Combine(LogsDir, fileName);
    }

    /// <summary>Find an existing token file for the account (suffix form preferred, then legacy).</summary>
    private string? FindExistingTokenFile(int accountNumber)
    {
        var preferred = GetPreferredTokenFilePath(accountNumber);
        if (File.Exists(preferred)) return preferred;

        if (!Directory.Exists(LogsDir)) return null;

        var prefix = $"mindvideo-token-{accountNumber:00}";
        // Prefer newest match among mindvideo-token-NN*.txt (covers alias renames + legacy).
        return Directory.EnumerateFiles(LogsDir, $"{prefix}*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void ClearStaleTokenFiles(int accountNumber, string keepPath)
    {
        if (!Directory.Exists(LogsDir)) return;
        var prefix = $"mindvideo-token-{accountNumber:00}";
        foreach (var path in Directory.EnumerateFiles(LogsDir, $"{prefix}*.txt"))
        {
            if (string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Delete(path); }
            catch { /* ignore locked/legacy */ }
        }
    }

    private static string? SanitizeFileSuffix(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        var trimmed = alias.Trim();
        // Skip placeholder aliases like account-30
        if (System.Text.RegularExpressions.Regex.IsMatch(
                trimmed, @"^account[-_]?\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed
            .Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' or ' ' ? '_' : c)
            .ToArray();
        var cleaned = new string(chars).Trim('_', '-', '.');
        // Collapse repeated underscores
        while (cleaned.Contains("__", StringComparison.Ordinal))
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private void DashboardNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(DashboardView);
    private void AccountsNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(AccountsView);
    private void LoginNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(LoginView);

    private void ShowView(Control view)
    {
        DashboardView.IsVisible = view == DashboardView;
        AccountsView.IsVisible = view == AccountsView;
        LoginView.IsVisible = view == LoginView;
        view.BringIntoView();
    }

    private void AccountComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateAccountDisplay();

    private void UpdateAccountDisplay()
    {
        if (AccountComboBox is null || AccountComboBox.SelectedIndex < 0) return;
        if (SecretNameText is null || TokenBox is null) return;

        var label = _aliases.GetValueOrDefault(AccountNumber);
        var chrome = GetChromeConfig(AccountNumber);
        SecretNameText.Text = BuildSecretNameText(chrome);
        TokenBox.Text = _tokens.GetValueOrDefault(AccountNumber) ?? string.Empty;
        if (CopyTokenButton is not null)
            CopyTokenButton.IsEnabled = !string.IsNullOrWhiteSpace(TokenBox.Text);
        if (PointsStatus is not null)
        {
            PointsStatus.Text = string.IsNullOrWhiteSpace(TokenBox.Text)
                ? "需要先設定此帳號的 Token。"
                : "已載入本機 Token，可讀取狀態或直接簽到。";
        }
        LoadChromeFields(chrome);
    }

    private ChromeProfileConfig GetChromeConfig(int accountNumber)
    {
        if (_chromeByAccount.TryGetValue(accountNumber, out var config))
            return ChromeProfileStore.Normalize(config, accountNumber);
        return ChromeProfileStore.CreateDefault(accountNumber);
    }

    private string SelectedBrowserEngine()
    {
        if (BrowserEngineComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return ChromeProfileStore.NormalizeBrowser(tag, null);
        return ChromeProfileStore.BrowserChrome;
    }

    private void SetBrowserEngineCombo(string browser)
    {
        if (BrowserEngineComboBox is null) return;
        var want = ChromeProfileStore.NormalizeBrowser(browser, null);
        for (var i = 0; i < BrowserEngineComboBox.Items.Count; i++)
        {
            if (BrowserEngineComboBox.Items[i] is ComboBoxItem cbi &&
                string.Equals(cbi.Tag as string, want, StringComparison.OrdinalIgnoreCase))
            {
                BrowserEngineComboBox.SelectedIndex = i;
                return;
            }
        }
        BrowserEngineComboBox.SelectedIndex = 0;
    }

    private void ApplyBrowserFieldHints(ChromeProfileConfig config)
    {
        var firefox = ChromeProfileStore.IsFirefox(config);
        if (ChromeExeBox is not null)
            ChromeExeBox.Watermark = firefox
                ? "留空 = 使用 Playwright 內建 Firefox（請勿填系統 firefox.exe）"
                : @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        if (ChromeExeLabel is not null)
            ChromeExeLabel.Text = firefox
                ? "瀏覽器執行檔（Firefox 請留空；系統 firefox.exe 與 Playwright 不相容）"
                : "瀏覽器執行檔（chrome.exe / msedge.exe）";
        if (ChromeUserDataBox is not null)
            ChromeUserDataBox.Watermark = firefox
                ? @"%LOCALAPPDATA%\MindVideo Auto Sign\firefox-profiles\account-01"
                : @"%LOCALAPPDATA%\MindVideo Auto Sign\chrome-cdp\account-01";
        if (ChromeUserDataLabel is not null)
            ChromeUserDataLabel.Text = firefox
                ? "Firefox profile 資料夾（建議獨立；可填系統 Profiles\\xxx，需先關閉 Firefox）"
                : "CDP user-data-dir（必須獨立資料夾；禁止 Chrome\\User Data）";
        if (ChromeProfileDirLabel is not null)
            ChromeProfileDirLabel.Text = firefox
                ? "備註（選填：系統 Firefox 設定檔名稱）"
                : "備註（選填：系統 Profile 名稱；不影響 CDP）";
        if (BrowserSettingsTitle is not null)
            BrowserSettingsTitle.Text = firefox ? "Firefox Playwright 啟動設定" : "Chrome CDP 啟動設定";
    }

    private void LoadChromeFields(ChromeProfileConfig config)
    {
        if (ChromeExeBox is null) return;
        _chromeUiLoading = true;
        try
        {
            var normalized = ChromeProfileStore.Normalize(config, AccountNumber);
            SetBrowserEngineCombo(normalized.Browser);
            ChromeExeBox.Text = normalized.ExecutablePath;
            ChromeProfileDirBox.Text = normalized.ProfileDirectory;
            ChromeUserDataBox.Text = ChromeProfileStore.ResolveProfileDir(normalized, AccountNumber);
            ChromeCommandPreview.Text = ChromeProfileStore.FormatCommandPreview(normalized, AccountNumber);
            ApplyBrowserFieldHints(normalized);
        }
        finally
        {
            _chromeUiLoading = false;
        }
    }

    private ChromeProfileConfig ReadChromeFieldsFromUi()
    {
        var browser = SelectedBrowserEngine();
        var userData = string.IsNullOrWhiteSpace(ChromeUserDataBox.Text)
            ? null
            : ChromeUserDataBox.Text.Trim().Trim('"');

        var exe = string.IsNullOrWhiteSpace(ChromeExeBox.Text)
            ? (browser == ChromeProfileStore.BrowserFirefox
                ? string.Empty
                : ChromeProfileStore.DefaultExecutablePath)
            : ChromeExeBox.Text.Trim().Trim('"');

        // Infer browser from executable if user typed firefox path while combo is chrome.
        browser = ChromeProfileStore.NormalizeBrowser(browser, exe);

        // Stock Mozilla firefox.exe hangs under Playwright — always fall back to bundled.
        if (browser == ChromeProfileStore.BrowserFirefox &&
            ChromeProfileStore.IsStockMozillaFirefoxPath(exe))
        {
            exe = string.Empty;
        }

        if (browser != ChromeProfileStore.BrowserFirefox &&
            ChromeProfileStore.IsForbiddenSystemChromeUserDataDir(userData))
        {
            userData = ChromeProfileStore.DefaultCdpUserDataDir(AccountNumber);
        }

        return ChromeProfileStore.Normalize(new ChromeProfileConfig
        {
            Browser = browser,
            ExecutablePath = exe,
            ProfileDirectory = string.IsNullOrWhiteSpace(ChromeProfileDirBox.Text)
                ? (browser == ChromeProfileStore.BrowserFirefox ? "firefox" : ChromeProfileStore.DefaultProfileDirectory)
                : ChromeProfileDirBox.Text.Trim().Trim('"'),
            UserDataDir = userData
        }, AccountNumber);
    }

    private void BrowserEngineComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_chromeUiLoading || BrowserEngineComboBox is null) return;
        var browser = SelectedBrowserEngine();
        var current = ReadChromeFieldsFromUi();
        if (ChromeProfileStore.NormalizeBrowser(current.Browser, current.ExecutablePath) == browser)
        {
            ApplyBrowserFieldHints(current);
            return;
        }

        // Switch engine defaults for the new selection while keeping account number.
        var next = browser == ChromeProfileStore.BrowserFirefox
            ? ChromeProfileStore.CreateFirefoxDefault(AccountNumber)
            : ChromeProfileStore.CreateDefault(AccountNumber);
        _chromeByAccount[AccountNumber] = next;
        LoadChromeFields(next);
        SecretNameText.Text = BuildSecretNameText(next);
    }

    private void ChromeSettings_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_chromeUiLoading) return;
        var config = ReadChromeFieldsFromUi();
        _chromeByAccount[AccountNumber] = config;
        if (ChromeUserDataBox is not null)
            ChromeUserDataBox.Text = ChromeProfileStore.ResolveProfileDir(config, AccountNumber);
        ChromeCommandPreview.Text = ChromeProfileStore.FormatCommandPreview(config, AccountNumber);
        ApplyBrowserFieldHints(config);
        SecretNameText.Text = BuildSecretNameText(config);
    }

    private string BuildSecretNameText(ChromeProfileConfig chrome)
    {
        var label = _aliases.GetValueOrDefault(AccountNumber);
        var dataDir = ChromeProfileStore.ResolveProfileDir(chrome, AccountNumber);
        var engine = ChromeProfileStore.IsFirefox(chrome) ? "Firefox" : "CDP";
        var hint = $"{engine} {Path.GetFileName(dataDir)}";
        return string.IsNullOrWhiteSpace(label)
            ? $"{SecretName}  ·  {hint}"
            : $"{SecretName}  ·  {label}  ·  {hint}";
    }

    private async void SaveChromeProfileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var config = ReadChromeFieldsFromUi();
            if (ChromeProfileStore.IsFirefox(config))
            {
                // Stock Mozilla path is normalized away; bundled needs no file check.
                var firefoxExe = ChromeProfileStore.ResolveFirefoxExecutableForLaunch(config.ExecutablePath);
                if (!string.IsNullOrEmpty(firefoxExe) && !File.Exists(firefoxExe))
                {
                    LoginStatus.Text = $"找不到 Firefox 執行檔：{firefoxExe}";
                    return;
                }
                config.ExecutablePath = firefoxExe;
            }
            else if (string.IsNullOrWhiteSpace(config.ExecutablePath) || !File.Exists(config.ExecutablePath))
            {
                LoginStatus.Text = $"找不到瀏覽器執行檔：{config.ExecutablePath}";
                return;
            }

            if (ChromeProfileStore.IsFirefox(config) &&
                ChromeProfileStore.IsSystemFirefoxProfilesPath(config.UserDataDir))
            {
                LoginStatus.Text =
                    "警告：正在使用系統 Firefox Profiles 路徑。擷取前請完全關閉 Firefox，否則可能失敗或鎖檔。";
            }

            _chromeByAccount[AccountNumber] = config;
            await _chromeProfiles.SaveAccountAsync(AccountNumber, config);
            await _chromeProfiles.SyncWorkspaceFileAsync(_chromeByAccount, _aliases);
            ChromeUserDataBox.Text = config.UserDataDir;
            ChromeCommandPreview.Text = ChromeProfileStore.FormatCommandPreview(config, AccountNumber);
            SecretNameText.Text = BuildSecretNameText(config);
            LoginStatus.Text =
                $"已儲存帳號 {AccountNumber:00} 的瀏覽器設定：{ChromeProfileStore.FormatCommandPreview(config, AccountNumber)}";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"儲存瀏覽器設定失敗：{ex.Message}";
        }
    }

    private async void ResetChromeProfileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var config = ChromeProfileStore.CreateDefault(AccountNumber);
            _chromeByAccount[AccountNumber] = config;
            await _chromeProfiles.SaveAccountAsync(AccountNumber, config);
            await _chromeProfiles.SyncWorkspaceFileAsync(_chromeByAccount, _aliases);
            LoadChromeFields(config);
            SecretNameText.Text = BuildSecretNameText(config);
            LoginStatus.Text =
                $"已還原帳號 {AccountNumber:00} Chrome 預設：{ChromeProfileStore.FormatCommandPreview(config, AccountNumber)}";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"還原 Chrome 預設失敗：{ex.Message}";
        }
    }

    private async void ResetFirefoxProfileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var config = ChromeProfileStore.CreateFirefoxDefault(AccountNumber);
            _chromeByAccount[AccountNumber] = config;
            await _chromeProfiles.SaveAccountAsync(AccountNumber, config);
            await _chromeProfiles.SyncWorkspaceFileAsync(_chromeByAccount, _aliases);
            LoadChromeFields(config);
            SecretNameText.Text = BuildSecretNameText(config);
            LoginStatus.Text =
                $"已還原帳號 {AccountNumber:00} Firefox 預設：{ChromeProfileStore.FormatCommandPreview(config, AccountNumber)}";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"還原 Firefox 預設失敗：{ex.Message}";
        }
    }

    private async Task LoadLocalTokensAsync()
    {
        try
        {
            var profiles = await _localTokens.LoadAsync();
            foreach (var profile in profiles)
            {
                if (TryResolveEnabledAccountNumber(profile.Name, profile.Id, out var number))
                {
                    if (!string.IsNullOrWhiteSpace(profile.Token))
                        _tokens[number] = profile.Token.Trim();
                    if (!string.IsNullOrWhiteSpace(profile.Name) && !profile.Name.StartsWith("MindVideo", StringComparison.Ordinal))
                        _aliases[number] = profile.Name.Trim();
                }
            }

            // Also load any previously captured token files (alias-suffix or legacy).
            foreach (var account in EnabledAccounts)
            {
                var i = account.Number;
                if (_tokens.ContainsKey(i)) continue;
                var file = FindExistingTokenFile(i);
                if (file is null) continue;
                var token = (await File.ReadAllTextAsync(file)).Trim();
                if (!string.IsNullOrWhiteSpace(token))
                    _tokens[i] = token;
            }

            UpdateAccountDisplay();
            RefreshAccountComboLabels();
            UpdateConfiguredMetric();
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"讀取本機 Token 失敗：{ex.Message}";
        }
    }

    private async void StartLoginButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StartLoginButton.IsEnabled = false;
        CopyTokenButton.IsEnabled = false;
        try
        {
            var chrome = ReadChromeFieldsFromUi();
            var isFirefox = ChromeProfileStore.IsFirefox(chrome);
            LoginStatus.Text = isFirefox
                ? "正在確認 Node.js 相依套件與 Playwright Firefox…"
                : "正在確認 Node.js 相依套件與 Chromium…";
            await RunProcessAsync("npm", ["install"]);
            await RunProcessAsync("npx", ["playwright", "install", isFirefox ? "firefox" : "chromium"]);

            var tokenFile = GetPreferredTokenFilePath(AccountNumber);
            Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
            ClearStaleTokenFiles(AccountNumber, tokenFile);
            if (File.Exists(tokenFile)) File.Delete(tokenFile);

            var profileDir = ChromeProfileStore.ResolveProfileDir(chrome, AccountNumber);
            chrome.UserDataDir = profileDir;
            _chromeByAccount[AccountNumber] = chrome;
            await _chromeProfiles.SaveAccountAsync(AccountNumber, chrome);
            await _chromeProfiles.SyncWorkspaceFileAsync(_chromeByAccount, _aliases);
            Directory.CreateDirectory(profileDir);

            // Chrome needs a real executable; Firefox uses Playwright bundled when path empty/stock.
            if (!isFirefox)
            {
                if (string.IsNullOrWhiteSpace(chrome.ExecutablePath) || !File.Exists(chrome.ExecutablePath))
                    throw new FileNotFoundException($"找不到瀏覽器執行檔：{chrome.ExecutablePath}");
            }
            else
            {
                var firefoxExe = ChromeProfileStore.ResolveFirefoxExecutableForLaunch(chrome.ExecutablePath);
                if (!string.IsNullOrEmpty(firefoxExe) && !File.Exists(firefoxExe))
                    throw new FileNotFoundException($"找不到 Firefox 執行檔：{firefoxExe}");
                // Clear stock Mozilla path so we do not pass a hanging executable.
                chrome.ExecutablePath = firefoxExe;
            }

            if (isFirefox)
            {
                // Fail early with clear ZH guidance; also clears stale parent.lock.
                ChromeProfileStore.PrepareFirefoxProfileOrThrow(profileDir);
                LoginStatus.Text =
                    "將啟動 Playwright Firefox 並跳轉 https://www.mindvideo.ai/auth/signin/；" +
                    "頁面頂部會有登入提示橫幅。請用「Login with Google」登入，維持 ≥5 秒後擷取 Token。 " +
                    ChromeProfileStore.FormatCommandPreview(chrome, AccountNumber);
            }
            else
            {
                LoginStatus.Text =
                    $"將啟動：{ChromeProfileStore.FormatCommandPreview(chrome, AccountNumber)}。首次請 Google 登入；維持 ≥5 秒後擷取 Token → {Path.GetFileName(tokenFile)}。";
            }

            var captureArgs = new List<string>
            {
                "scripts/capture-token-gui.mjs",
                "--account", AccountNumber.ToString(),
                "--output", tokenFile,
                "--browser", ChromeProfileStore.NormalizeBrowser(chrome.Browser, chrome.ExecutablePath),
                "--user-data-dir", profileDir,
                "--url", "https://www.mindvideo.ai/auth/signin/"
            };
            // Only pass executable for Chrome, or a non-stock Firefox binary.
            if (!string.IsNullOrWhiteSpace(chrome.ExecutablePath))
            {
                captureArgs.Add("--executable-path");
                captureArgs.Add(chrome.ExecutablePath);
            }
            if (!string.IsNullOrWhiteSpace(chrome.ProfileDirectory))
            {
                captureArgs.Add("--profile-directory");
                captureArgs.Add(chrome.ProfileDirectory);
            }

            await RunProcessAsync("node", captureArgs);

            if (!File.Exists(tokenFile))
                throw new InvalidOperationException(
                    "未找到已驗證的 Token 檔。請確認已在瀏覽器中完整登入 MindVideo（非僅停留在登入頁）。");

            var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Token 檔是空的。");

            TokenBox.Text = token;
            _tokens[AccountNumber] = token;
            await PersistLocalTokensAsync();
            CopyTokenButton.IsEnabled = true;
            LoginStatus.Text =
                $"完成。已確認登入維持 ≥5 秒並擷取有效 Token（{MaskToken(token)}）→ {Path.GetFileName(tokenFile)}，可更新到 GitHub Secret {SecretName}。";
            PointsStatus.Text = "Token 已驗證就緒，可讀取狀態或直接簽到。";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"登入狀態更新失敗：{ex.Message}";
        }
        finally
        {
            StartLoginButton.IsEnabled = true;
        }
    }

    private async void CopyTokenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            LoginStatus.Text = "目前帳號尚未有可複製的 Token。";
            return;
        }

        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(token);
        LoginStatus.Text = $"已複製 Token（{token.Length:N0} 字元）。請勿貼到公開聊天、Issue 或螢幕截圖。";
    }

    private async void CopySecretButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(SecretName);
        LoginStatus.Text = $"已複製 {SecretName}。";
    }

    private async void SaveLocalButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            _tokens.Remove(AccountNumber);
        }
        else
        {
            _tokens[AccountNumber] = token;
        }

        try
        {
            await PersistLocalTokensAsync();
            CopyTokenButton.IsEnabled = !string.IsNullOrWhiteSpace(token);
            LoginStatus.Text = string.IsNullOrWhiteSpace(token)
                ? "已清除本機 Token。"
                : $"已儲存本機 Token（{MaskToken(token)}）。路徑：{_localTokens.Location}";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"儲存本機 Token 失敗：{ex.Message}";
        }
    }

    private async void PushSecretButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            LoginStatus.Text = "請先貼上或擷取 Token，再更新 GitHub Secret。";
            return;
        }

        PushSecretButton.IsEnabled = false;
        try
        {
            LoginStatus.Text = $"正在更新 GitHub Secret {SecretName}…";
            var repository = await ResolveRepositoryAsync();
            await _github.SetSecretAsync(repository, SecretName, token);
            _tokens[AccountNumber] = token;
            await PersistLocalTokensAsync();
            LoginStatus.Text = $"已更新 {repository} 的 {SecretName}。";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"更新 GitHub Secret 失敗：{ex.Message}";
        }
        finally
        {
            PushSecretButton.IsEnabled = true;
        }
    }

    private async void ReadStatusButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await WithTokenActionAsync(ReadStatusButton, async token =>
        {
            PointsStatus.Text = "正在讀取 MindVideo 簽到狀態…";
            var result = await _api.RefreshAsync(new AccountProfile
            {
                Name = DisplayAlias(AccountNumber),
                Token = token
            });
            PointsStatus.Text = FormatPointsStatus(result);
            PersistLocalStreak(AccountNumber, DisplayAlias(AccountNumber), result.Streak, result.Status.ToString(), result.TotalCredits);
        });
    }

    private async void LocalCheckInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await WithTokenActionAsync(LocalCheckInButton, async token =>
        {
            PointsStatus.Text = "正在直接簽到…";
            var result = await _api.CheckInAsync(new AccountProfile
            {
                Name = DisplayAlias(AccountNumber),
                Token = token
            });
            PointsStatus.Text = FormatPointsStatus(result);
            PersistLocalStreak(AccountNumber, DisplayAlias(AccountNumber), result.Streak, result.Status.ToString(), result.TotalCredits);
        });
    }

    /// <summary>Current balance first; the lifetime total is not what the user spends.</summary>
    private static string FormatPointsStatus(CheckinResult result) =>
        $"{result.Message} · 當前點數 {Display(result.RemainingCredits)}"
        + $" · 已使用點數 {Display(result.UsedCredits)}"
        + $" · GPT Image 2 {Display(result.GptImage2Credits)}"
        + $" · 連續簽到 {Display(result.Streak)} 天";

    private async Task WithTokenActionAsync(Button button, Func<string, Task> action)
    {
        var token = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            PointsStatus.Text = "需要先設定此帳號的 Token。";
            return;
        }

        button.IsEnabled = false;
        try
        {
            await action(token);
        }
        catch (Exception ex)
        {
            PointsStatus.Text = $"操作失敗：{ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void TriggerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await WithDashboardBusy(async () =>
        {
            DashboardStatus.Text = "正在觸發 MindVideo Daily Check-in…";
            var repository = await ResolveRepositoryAsync();
            await _github.TriggerAsync(repository);
            DashboardStatus.Text = "已送出簽到工作；稍後按「更新執行結果」查看狀態。";
        });
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await WithDashboardBusy(async () =>
        {
            DashboardStatus.Text = "正在讀取 GitHub Actions…";
            var repository = await ResolveRepositoryAsync();
            var runs = await _github.GetRecentRunsAsync(repository);
            var run = runs.FirstOrDefault();
            if (run is null)
            {
                RunMetric.Text = "尚無執行紀錄";
                StreakMetric.Text = "—";
                RunTimeMetric.Text = "—";
                ConfiguredMetric.Text = "—";
                ResetActionHistoryMetrics();
                AccountsPanel.Children.Clear();
                DashboardStatus.Text = "尚未找到 MindVideo Daily Check-in 執行紀錄。";
                return;
            }

            var actionHistory = GitHubActionsService.SummarizeRuns(runs, GetTaipeiZone());
            RunMetric.Text = string.IsNullOrWhiteSpace(run.Conclusion) ? run.Status : run.Conclusion!;
            RunTimeMetric.Text = TimeZoneInfo.ConvertTime(run.UpdatedAt ?? run.CreatedAt, GetTaipeiZone())
                .ToString("MM/dd HH:mm");
            UpdateActionHistoryMetrics(
                actionHistory,
                string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase));

            if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                ConfiguredMetric.Text = "執行中";
                StreakMetric.Text = "完成後再更新";
                AccountsPanel.Children.Clear();
                DashboardStatus.Text = $"工作流程尚未完成：{run.Url}";
                return;
            }

            var accounts = await _github.GetAccountStatusesAsync(repository, run.DatabaseId);
            var configured = accounts.Count(account => account.IsConfigured);
            var withStreak = accounts.Count(account => account.Streak is > 0);
            var maxStreak = accounts.Where(a => a.Streak is > 0).Select(a => a.Streak!.Value).DefaultIfEmpty(0).Max();
            ConfiguredMetric.Text = $"{configured} 個";
            StreakMetric.Text = withStreak > 0
                ? $"最高 {maxStreak} 天 · {withStreak}/{configured} 已紀錄"
                : $"{Math.Max(0, _accountCatalog.EnabledCount - configured)} 未設定";
            PersistWorkflowStreaks(accounts, run.Url);
            RenderAccounts(accounts);
            DashboardStatus.Text = $"最近執行：{run.Url} · Action 歷史與連續成功天數已同步";
        });
    }

    private void UpdateActionHistoryMetrics(WorkflowActionSummary summary, bool latestRunCompleted)
    {
        LastSuccessMetric.Text = FormatActionTime(summary.LastSuccessAt);
        LastFailureMetric.Text = FormatActionTime(summary.LastFailureAt);
        ActionStreakMetric.Text = latestRunCompleted
            ? $"{summary.ConsecutiveSuccessDays} 天"
            : "完成後更新";
    }

    private void ResetActionHistoryMetrics()
    {
        LastSuccessMetric.Text = "尚無紀錄";
        LastFailureMetric.Text = "尚無紀錄";
        ActionStreakMetric.Text = "尚無紀錄";
    }

    private static string FormatActionTime(DateTimeOffset? timestamp) =>
        timestamp is DateTimeOffset value
            ? TimeZoneInfo.ConvertTime(value, GetTaipeiZone()).ToString("MM/dd HH:mm")
            : "尚無紀錄";

    private async Task WithDashboardBusy(Func<Task> action)
    {
        TriggerButton.IsEnabled = RefreshButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            DashboardStatus.Text = $"GitHub Actions 操作失敗：{ex.Message}";
        }
        finally
        {
            TriggerButton.IsEnabled = RefreshButton.IsEnabled = true;
        }
    }

    private void RenderAccounts(IEnumerable<WorkflowAccountStatus> accounts)
    {
        AccountsPanel.Children.Clear();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("68,*,150,90") };
        header.Children.Add(new TextBlock
        {
            Text = "#",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.SlateGray
        });
        var hAlias = new TextBlock
        {
            Text = "帳號",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.SlateGray
        };
        Grid.SetColumn(hAlias, 1);
        header.Children.Add(hAlias);
        var hStatus = new TextBlock
        {
            Text = "狀態",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.SlateGray
        };
        Grid.SetColumn(hStatus, 2);
        header.Children.Add(hStatus);
        var hStreak = new TextBlock
        {
            Text = "連續天數",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.SlateGray,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(hStreak, 3);
        header.Children.Add(hStreak);
        AccountsPanel.Children.Add(header);

        foreach (var account in accounts)
        {
            var localAlias = _aliases.GetValueOrDefault(account.Number);
            var alias = !string.IsNullOrWhiteSpace(localAlias) ? localAlias : account.Alias;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("68,*,150,90") };
            row.Children.Add(new TextBlock
            {
                Text = $"#{account.Number:00}",
                FontWeight = FontWeight.SemiBold
            });

            var aliasBlock = new TextBlock
            {
                Text = alias,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(aliasBlock, 1);
            row.Children.Add(aliasBlock);

            var state = new TextBlock
            {
                Text = account.IsConfigured ? account.Status : "未設定",
                Foreground = account.IsConfigured
                    ? (account.IsSuccessful ? Brushes.SeaGreen : Brushes.IndianRed)
                    : Brushes.Gray,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(state, 2);
            row.Children.Add(state);

            var streak = new TextBlock
            {
                Text = account.Streak is null ? "—" : $"{account.Streak} 天",
                Foreground = account.Streak is > 0 ? Brushes.SeaGreen : Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(streak, 3);
            row.Children.Add(streak);

            AccountsPanel.Children.Add(row);
        }
    }

    private void PersistWorkflowStreaks(IReadOnlyList<WorkflowAccountStatus> accounts, string? source)
    {
        try
        {
            var now = DateTimeOffset.Now;
            _streaks.UpsertMany(
                accounts.Select(account => new AccountStreakEntry
                {
                    Account = account.Number,
                    Label = account.Alias,
                    Streak = account.Streak,
                    Status = account.Status,
                    UpdatedAt = now
                }),
                source);
        }
        catch
        {
            // Local streak cache is best-effort.
        }
    }

    private void PersistLocalStreak(int account, string label, int? streak, string? status, int? totalCredits)
    {
        try
        {
            _streaks.UpsertMany(
            [
                new AccountStreakEntry
                {
                    Account = account,
                    Label = label,
                    Streak = streak,
                    Status = status,
                    TotalCredits = totalCredits,
                    UpdatedAt = DateTimeOffset.Now
                }
            ], "local-api");
        }
        catch
        {
            // Local streak cache is best-effort.
        }
    }

    /// <summary>
    /// Show the last known continuous-check-in days from the local streak cache
    /// so the dashboard is useful before the next GitHub Actions refresh.
    /// </summary>
    private void LoadCachedStreakDashboard()
    {
        try
        {
            if (AccountsPanel is null || StreakMetric is null)
                return;

            var snapshot = _streaks.Load();
            if (snapshot.Accounts.Count == 0)
                return;

            var rows = EnabledAccounts
                .Select(account =>
                {
                    var number = account.Number;
                    if (!snapshot.Accounts.TryGetValue(number.ToString(), out var entry))
                        return new WorkflowAccountStatus(number, account.Label, "尚未更新", null, false, false);

                    var alias = !string.IsNullOrWhiteSpace(entry.Label)
                        ? entry.Label!
                        : DisplayAlias(number);
                    var status = string.IsNullOrWhiteSpace(entry.Status) ? "本機快取" : entry.Status!;
                    var configured = entry.Streak is not null ||
                                     (!status.Contains("略過", StringComparison.Ordinal) &&
                                      !status.Contains("尚未設定", StringComparison.Ordinal));
                    var successful = configured &&
                                     !status.Contains("fail", StringComparison.OrdinalIgnoreCase) &&
                                     !status.Contains("失敗", StringComparison.Ordinal);
                    return new WorkflowAccountStatus(number, alias, status, entry.Streak, successful, configured);
                })
                .ToArray();

            var withStreak = rows.Count(account => account.Streak is > 0);
            var maxStreak = rows.Where(a => a.Streak is > 0).Select(a => a.Streak!.Value).DefaultIfEmpty(0).Max();
            var configured = rows.Count(account => account.IsConfigured);
            if (ConfiguredMetric is not null)
                ConfiguredMetric.Text = $"{configured} 個";
            StreakMetric.Text = withStreak > 0
                ? $"最高 {maxStreak} 天 · {withStreak}/{configured} 已紀錄（本機）"
                : "本機尚無連續天數";
            if (RunMetric is not null && (string.IsNullOrWhiteSpace(RunMetric.Text) || RunMetric.Text == "尚未讀取"))
                RunMetric.Text = "本機快取";
            if (RunTimeMetric is not null && snapshot.UpdatedAt is DateTimeOffset updated)
            {
                RunTimeMetric.Text = TimeZoneInfo.ConvertTime(updated, GetTaipeiZone()).ToString("MM/dd HH:mm");
            }
            RenderAccounts(rows);
            if (DashboardStatus is not null)
            {
                var source = string.IsNullOrWhiteSpace(snapshot.Source) ? "本機 streaks.json" : snapshot.Source;
                DashboardStatus.Text = $"已載入本機連續簽到快取（{source}）。按「更新執行結果」可同步最新 GitHub Actions。";
            }
        }
        catch
        {
            // Local streak cache is best-effort.
        }
    }

    private void BuildAliasList()
    {
        foreach (var account in EnabledAccounts)
        {
            var i = account.Number;
            var box = new TextBox
            {
                Width = 350,
                Text = _aliases.GetValueOrDefault(i),
                Watermark = "帳號名稱（僅本機顯示）"
            };
            _aliasInputs[i] = box;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12
            };
            row.Children.Add(new TextBlock
            {
                Text = $"帳號 {i:00}",
                Width = 72,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(box);
            AliasPanel.Children.Add(row);
        }
    }

    private async void SaveAliasesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var (number, input) in _aliasInputs)
        {
            if (string.IsNullOrWhiteSpace(input.Text))
                _aliases[number] = _accountCatalog.LabelFor(number);
            else
                _aliases[number] = input.Text.Trim();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AliasFile)!);
        await File.WriteAllTextAsync(AliasFile, JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = true }));
        RefreshAccountComboLabels();
        UpdateAccountDisplay();
        LoginStatus.Text = "已儲存帳號別名。";
    }

    private void RefreshAccountComboLabels()
    {
        var selectedNumber = AccountComboBox.SelectedIndex >= 0 ? AccountNumber : EnabledAccounts[0].Number;
        AccountComboBox.ItemsSource = EnabledAccounts
            .Select(account =>
            {
                var alias = DisplayAlias(account.Number);
                return string.IsNullOrWhiteSpace(alias)
                    ? $"帳號 {account.Number:00}"
                    : $"帳號 {account.Number:00} · {alias}";
            })
            .ToArray();
        var selectedIndex = EnabledAccounts
            .Select((account, index) => (account.Number, index))
            .FirstOrDefault(item => item.Number == selectedNumber)
            .index;
        AccountComboBox.SelectedIndex = selectedIndex;
    }

    private async Task PersistLocalTokensAsync()
    {
        var profiles = EnabledAccounts
            .Select(account => account.Number)
            .Where(number => _tokens.ContainsKey(number) && !string.IsNullOrWhiteSpace(_tokens[number]))
            .Select(number => new AccountProfile
            {
                Id = number.ToString(),
                Name = DisplayAlias(number),
                Token = _tokens[number]
            })
            .ToList();
        await _localTokens.SaveAsync(profiles);
    }

    private async Task<string> ResolveRepositoryAsync()
    {
        try
        {
            return await _github.GetRepositoryAsync();
        }
        catch
        {
            return "huang1988pioneer/AutoSignMindVideo";
        }
    }

    private string DisplayAlias(int number)
    {
        var alias = _aliases.GetValueOrDefault(number);
        return string.IsNullOrWhiteSpace(alias) ? $"account-{number}" : alias;
    }

    private async Task RunProcessAsync(string command, IEnumerable<string> args)
    {
        _ = await RunProcessCaptureAsync(command, args);
    }

    private async Task<string> RunProcessCaptureAsync(string command, IEnumerable<string> args)
    {
        var executable = NodeCommandPath(command);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = _workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        if (!process.Start())
            throw new InvalidOperationException($"無法啟動 {command}。");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            // Prefer stderr, but keep stdout (CDP diagnostics are often on stdout).
            var combined = string.Join(
                "\n",
                new[] { error?.Trim(), output?.Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
            throw new InvalidOperationException(combined.Truncate(4000));
        }
        return output;
    }

    private Dictionary<int, string> LoadAliases()
    {
        var aliases = EnabledAccounts.ToDictionary(account => account.Number, account => account.Label);
        try
        {
            if (!File.Exists(AliasFile)) return aliases;
            var saved = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(AliasFile)) ?? [];
            foreach (var (number, name) in saved)
            {
                if (_accountCatalog.IsEnabled(number) && !string.IsNullOrWhiteSpace(name))
                    aliases[number] = name.Trim();
            }
        }
        catch (JsonException)
        {
            // Keep repository labels when an old local alias file is malformed.
        }
        return aliases;
    }

    private void UpdateConfiguredMetric()
    {
        if (ConfiguredMetric is null) return;
        var configured = EnabledAccounts.Count(account =>
            _tokens.TryGetValue(account.Number, out var token) && !string.IsNullOrWhiteSpace(token));
        ConfiguredMetric.Text = $"{configured}/{_accountCatalog.EnabledCount} 個";
    }

    private bool TryResolveEnabledAccountNumber(string? first, string? second, out int number)
    {
        if (TryParseAccountNumber(first, out number) && _accountCatalog.IsEnabled(number))
            return true;
        if (TryParseAccountNumber(second, out number) && _accountCatalog.IsEnabled(number))
            return true;
        number = 0;
        return false;
    }

    private bool TryParseAccountNumber(string? value, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (int.TryParse(value, out number) && number >= 1 && number <= _accountCatalog.SlotCount)
            return true;
        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"(?:account[-_]?|MindVideo(?:[_\s-]*TOKEN)?\s*)(?<n>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["n"].Value, out number) &&
            number >= 1 && number <= _accountCatalog.SlotCount)
            return true;
        return false;
    }

    private static string FindWorkspace()
    {
        string? workspace = null;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "package.json")) &&
                    (File.Exists(Path.Combine(dir.FullName, "checkin.js")) ||
                     File.Exists(Path.Combine(dir.FullName, "scripts", "capture-mindvideo-tokens.js"))))
                {
                    workspace = dir.FullName;
                }
            }
        }
        return workspace ?? Environment.CurrentDirectory;
    }

    private static TimeZoneInfo GetTaipeiZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); }
    }

    private static string NodeCommandPath(string command)
    {
        if (!OperatingSystem.IsWindows()) return command;
        if (command == "node")
        {
            var nodeExecutable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs",
                "node.exe");
            return File.Exists(nodeExecutable) ? nodeExecutable : "node";
        }

        var systemCommand = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "nodejs",
            $"{command}.cmd");
        return File.Exists(systemCommand) ? systemCommand : $"{command}.cmd";
    }

    private static string MaskToken(string token) =>
        token.Length <= 16 ? "***" : $"{token[..6]}...{token[^6..]}";

    private static string Display(int? value) => value?.ToString() ?? "—";
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
