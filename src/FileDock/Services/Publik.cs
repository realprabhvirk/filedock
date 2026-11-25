// Publik.cs: Sign in with publik and usage counting for FileDock (C# / WPF port of
// https://publikhq.com/sdk/publik.rs.md). At start-up, before any window of FileDock's own,
// App calls `await Publik.EnsureSignedInAsync()`. It
//   1. loads or provisions this computer's publik key (POST /api/v1/installs);
//   2. shows Sign in with publik until GET /api/v1/wallet says "claimed" (no other way past it);
//   3. starts usage counting: app_opened now, active_session every 30 minutes (POST /api/v1/telemetry).
// After the first sign-in it is remembered on this computer: later starts open at once, also
// offline, and the sign-in is re-checked in the background whenever publik can be reached.
// FileDock calls no AI model (USES_AI = false), so the publik API provider parts are left out.
// PUBLIK_SELF_TEST=1 makes the built app print "publik self-test: ok <slug> <version>" and exit.
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDock.Services;

internal static class Publik
{
    public const string APP_SLUG = "filedock";
    /// <summary>false: FileDock calls no model. It still signs in and counts users; it stores no choice.</summary>
    public const bool USES_AI = false;
    public static readonly string APP_VERSION =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private const string OS = "windows";
    private const string Unreachable = "publik is not reachable. Check your connection.";

    // Request timeouts are set per call; the client itself never gives up first.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string Origin =>
        Environment.GetEnvironmentVariable("PUBLIK_ORIGIN") is { Length: > 0 } o ? o.TrimEnd('/') : "https://publikhq.com"; // tests only

    private static string DefaultBaseUrl => Origin + "/api/v1";

    /// <summary>"arm64" or "x64", as publik names them; null for any other chip.</summary>
    private static string? Arch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    private static string DeviceName
    {
        get
        {
            string name = Environment.MachineName ?? "";
            return name.Length > 120 ? name[..120] : name;
        }
    }

    // ---------- App token ----------

    /// <summary>
    /// Public by design: it can only mint capped keys attributed to this app, and every user who
    /// builds from source needs it, so publik-app-token.txt lives in the repo root and is embedded
    /// into the build. PUBLIK_APP_TOKEN at run time overrides it.
    /// </summary>
    private static string AppToken()
    {
        string runtime = (Environment.GetEnvironmentVariable("PUBLIK_APP_TOKEN") ?? "").Trim();
        if (runtime.Length > 0)
            return runtime;
        using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("publik-app-token.txt");
        if (s is null)
            return "";
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    // ---------- Credential (%LOCALAPPDATA%\publik, shared by every publik app) ----------

    internal static string PublikDir
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            return Path.Combine(local, "publik");
        }
    }

    public static string CredentialPath => Path.Combine(PublikDir, "apps", APP_SLUG + ".json");

    public static JsonObject? ReadCredential()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(CredentialPath)) is JsonObject c && Str(c, "key") is { } k && k.StartsWith("pk_", StringComparison.Ordinal))
                return c;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        return null;
    }

    private static void WriteCredential(JsonObject c)
    {
        string path = CredentialPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // %LOCALAPPDATA% is already private to the Windows user account.
        File.WriteAllText(path, c.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------- HTTP ----------

    private static async Task<(int Code, JsonObject Body)> SendAsync(HttpMethod method, string url, string? key, string? json, int timeoutSeconds)
    {
        using var request = new HttpRequestMessage(method, url);
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            JsonObject body;
            try
            {
                body = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                body = new JsonObject();
            }
            return ((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new PublikException(Unreachable);
        }
    }

    private static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string? ErrorMessage(JsonObject body) => Str(body["error"], "message");

    // ---------- Install key and wallet ----------

    /// <summary>POST /api/v1/installs: this computer's publik key. Throws <see cref="PublikException"/> with a readable message.</summary>
    public static async Task<JsonObject> ProvisionAsync()
    {
        string token = AppToken();
        if (!token.StartsWith("pat_", StringComparison.Ordinal))
            throw new PublikException("This build has no publik app token. Ask the developer for a new build.");
        JsonObject prior = ReadCredential() ?? new JsonObject();
        string installId = Str(prior, "install_id") ?? Guid.NewGuid().ToString();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var body = new JsonObject
            {
                ["app_token"] = token,
                ["app_slug"] = APP_SLUG,
                ["app_version"] = APP_VERSION,
                ["os"] = OS,
                ["device_name"] = DeviceName,
                ["install_id"] = installId,
                ["disclosure_version"] = 1,
                ["dialects"] = new JsonArray("chat_completions"),
            };
            if (Arch is { } arch)
                body["arch"] = arch;
            (int code, JsonObject res) = await SendAsync(HttpMethod.Post, Origin + "/api/v1/installs", null, body.ToJsonString(), 20).ConfigureAwait(false);
            string? key = Str(res, "key") ?? (code == 200 ? Str(prior, "key") : null);
            if (code is 200 or 201 && key is not null)
            {
                var c = new JsonObject
                {
                    ["key"] = key,
                    ["base_url"] = Str(res, "base_url") ?? DefaultBaseUrl,
                    ["install_id"] = Str(res, "install_id") ?? installId,
                    ["claim_url"] = Str(res, "claim_url"),
                    ["choice"] = Str(prior, "choice"),
                };
                WriteCredential(c); // the key reaches disk before anything else sees it
                return c;
            }
            if (code == 200 || Str(res["error"], "type") == "install_revoked")
            {
                installId = Guid.NewGuid().ToString();
                prior = new JsonObject();
                continue;
            }
            throw new PublikException(ErrorMessage(res) ?? $"publik could not set up this computer ({code}).");
        }
        throw new PublikException("publik could not set up this computer.");
    }

    /// <summary>GET /api/v1/wallet: (status, wallet or null, message). Throws <see cref="PublikException"/> when publik is not reachable.</summary>
    public static async Task<(int Code, JsonObject? Wallet, string? Message)> ReadWalletAsync(string key)
    {
        (int code, JsonObject b) = await SendAsync(HttpMethod.Get, Origin + "/api/v1/wallet", key, null, 15).ConfigureAwait(false);
        if (code != 200 || Str(b, "claim_state") is null)
            return (code, null, ErrorMessage(b));
        return (code, b, null);
    }

    private static bool Claimed(JsonObject w) => Str(w, "claim_state") == "claimed";

    // ---------- Remembered sign-in ----------

    /// <summary>True once this computer has signed in to publik (kept in the credential file).</summary>
    public static bool IsSignedInLocally => Str(ReadCredential(), "signed_in_at") is not null;

    /// <summary>The publik handle shown in Settings, when publik told us one.</summary>
    public static string? SignedInHandle => Str(ReadCredential(), "handle");

    private static void RememberSignIn(JsonObject wallet)
    {
        try
        {
            if (ReadCredential() is not { } c)
                return;
            c["signed_in_at"] ??= DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            c["handle"] = Str(wallet["account"], "handle");
            WriteCredential(c);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("publik: could not remember the sign-in", ex);
        }
    }

    private static void ForgetSignIn()
    {
        if (ReadCredential() is not { } c)
            return;
        c.Remove("signed_in_at");
        c.Remove("handle");
        WriteCredential(c);
    }

    /// <summary>Background check of a remembered sign-in. Offline or any error: nothing changes.</summary>
    private static async Task RecheckSignInAsync()
    {
        try
        {
            string key = Str(ReadCredential(), "key") ?? "";
            (int code, JsonObject? w, _) = await ReadWalletAsync(key).ConfigureAwait(false);
            if (code == 401)
            {
                Log.Info("publik: install key revoked; sign-in will be asked at the next start");
                WriteCredential(new JsonObject());
            }
            else if (w is not null && !Claimed(w))
            {
                Log.Info("publik: signed out on publik; sign-in will be asked at the next start");
                ForgetSignIn();
            }
            else if (w is not null)
            {
                RememberSignIn(w);
            }
        }
        catch (Exception ex) when (ex is PublikException or IOException or UnauthorizedAccessException)
        {
            // Offline: keep the remembered sign-in.
        }
    }

    // ---------- The gate ----------

    internal sealed record Screen(string Id, string Title, string Body, string? Note, (string Id, string Label)[] Buttons);

    /// <summary>The words of the gate. The same in every publik module.</summary>
    internal static Screen GateScreen(string id, string? note, string? message) => id switch
    {
        "unreachable" => new Screen(id, "Sign in with publik", message ?? Unreachable, note, [("retry", "Retry")]),
        _ => new Screen(id, "Sign in with publik",
            "This app uses your publik account. Click Sign in with publik, then sign in in your browser. This window continues when you are signed in.",
            note, [("sign_in", "Sign in with publik")]),
    };

    /// <summary>
    /// The gate (see the top of the file). Runs on the UI thread. Returns true once the person is
    /// signed in, false when they closed the sign-in window (FileDock then exits).
    /// </summary>
    public static async Task<bool> EnsureSignedInAsync()
    {
        // Signed in once on this computer: open at once, also offline. The sign-in is checked again
        // in the background when publik can be reached; a revoked or signed-out install asks again
        // at the next start.
        if (Environment.GetEnvironmentVariable("PUBLIK_API_KEY") is not { Length: > 0 } && IsSignedInLocally)
        {
            Log.Info("publik: signed in locally");
            StartTelemetry();
            _ = Task.Run(RecheckSignInAsync);
            return true;
        }

        GateWindow? ui = null;
        try
        {
            string? signNote = null;
            bool signPoll = true;
            while (true)
            {
                string? envKey = Environment.GetEnvironmentVariable("PUBLIK_API_KEY") is { Length: > 0 } k ? k : null;
                JsonObject c;
                try
                {
                    c = envKey is not null ? new JsonObject { ["key"] = envKey } : ReadCredential() ?? await ProvisionAsync();
                }
                catch (PublikException e)
                {
                    Log.Warn($"publik: {e.Message}");
                    if (await AskAsync(ui ??= new GateWindow(), GateScreen("unreachable", null, e.Message), "", null) == GateWindow.ClosedByUser)
                        return false;
                    continue;
                }

                string key = Str(c, "key") ?? "";
                int code;
                JsonObject? w;
                string? message;
                try
                {
                    (code, w, message) = await ReadWalletAsync(key);
                }
                catch (PublikException)
                {
                    if (await AskAsync(ui ??= new GateWindow(), GateScreen("unreachable", null, null), "", null) == GateWindow.ClosedByUser)
                        return false;
                    continue;
                }
                if (code == 401 && envKey is null)
                {
                    Log.Info("publik: install key revoked; provisioning again");
                    WriteCredential(new JsonObject()); // a revoked key: provision again
                    continue;
                }
                if (w is null)
                {
                    Log.Warn($"publik: wallet answered {code}");
                    if (await AskAsync(ui ??= new GateWindow(), GateScreen("unreachable", null, message), "", null) == GateWindow.ClosedByUser)
                        return false;
                    continue;
                }

                if (!Claimed(w))
                {
                    string a = await AskAsync(ui ??= new GateWindow(), GateScreen("sign_in", signNote, null), key, signPoll ? Claimed : null);
                    if (a == GateWindow.ClosedByUser)
                        return false;
                    if (a == "sign_in")
                    {
                        OpenUrl(Str(w, "claim_url") ?? Str(c, "claim_url") ?? Origin + "/wallet");
                        (signNote, signPoll) = ("Waiting for you to sign in in your browser.", true);
                    }
                    if (a == "timeout")
                        (signNote, signPoll) = ("No sign-in after 10 minutes. Click Sign in with publik to try again.", false);
                    continue;
                }

                Log.Info("publik: signed in");
                if (envKey is null)
                    RememberSignIn(w);
                StartTelemetry();
                return true;
            }
        }
        finally
        {
            ui?.CloseQuietly();
        }
    }

    /// <summary>
    /// Shows a screen and waits for a button. With <paramref name="until"/>, polls GET /wallet every
    /// 3 s for at most 10 minutes: "done" when the poll saw it, "timeout" when it did not. The
    /// unreachable screen also retries by itself every 10 s.
    /// </summary>
    private static async Task<string> AskAsync(GateWindow ui, Screen screen, string key, Func<JsonObject, bool>? until)
    {
        using var cts = new CancellationTokenSource();
        Task<string> pressed = ui.ShowScreen(screen);
        Task<string>? background = until is not null
            ? PollAsync(key, until, cts.Token)
            : screen.Id == "unreachable" ? AutoRetryAsync(cts.Token) : null;
        Task<string> first = background is null ? pressed : await Task.WhenAny(pressed, background);
        cts.Cancel();
        return await first;
    }

    private static async Task<string> PollAsync(string key, Func<JsonObject, bool> until, CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < 200; i++) // 200 x 3 s = 10 minutes
            {
                await Task.Delay(3000, ct);
                try
                {
                    (_, JsonObject? w, _) = await ReadWalletAsync(key);
                    if (w is not null && until(w))
                        return "done";
                }
                catch (PublikException)
                {
                    // Keep polling; the person may be mid sign-in on a flaky connection.
                }
            }
            return "timeout";
        }
        catch (OperationCanceledException)
        {
            return "cancelled";
        }
    }

    private static async Task<string> AutoRetryAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(10_000, ct);
            return "retry";
        }
        catch (OperationCanceledException)
        {
            return "cancelled";
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("publik: could not open the browser", ex);
        }
    }

    // ---------- Self-test ----------

    public static bool SelfTestRequested => Environment.GetEnvironmentVariable("PUBLIK_SELF_TEST") is not null;

    /// <summary>Builder check, run by the BUILT app: PUBLIK_SELF_TEST=1 prints this line and exits.</summary>
    public static async Task<string> SelfTestAsync()
    {
        try
        {
            JsonObject c = await ProvisionAsync().ConfigureAwait(false);
            (int code, JsonObject? w, string? message) = await ReadWalletAsync(Str(c, "key") ?? "").ConfigureAwait(false);
            return w is not null
                ? $"publik self-test: ok {APP_SLUG} {APP_VERSION}"
                : $"publik self-test: failed: {message ?? $"GET /wallet answered {code}"}";
        }
        catch (PublikException e)
        {
            return $"publik self-test: failed: {e.Message}";
        }
    }

    /// <summary>
    /// FileDock is a windowed (WinExe) app with no console, so the line goes to a redirected stdout
    /// when there is one, otherwise to the parent console, and always to
    /// %LOCALAPPDATA%\FileDock\publik-self-test.txt.
    /// </summary>
    public static void RunSelfTestAndExit(string dataDirectory)
    {
        string line = Task.Run(SelfTestAsync).GetAwaiter().GetResult();
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "publik-self-test.txt"), line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        IntPtr stdout = GetStdHandle(STD_OUTPUT_HANDLE);
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1))
            AttachConsole(ATTACH_PARENT_PROCESS);
        try
        {
            var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            writer.WriteLine();
            writer.WriteLine(line);
        }
        catch (IOException)
        {
        }
        Environment.Exit(0);
    }

    private const int STD_OUTPUT_HANDLE = -11;
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    // ---------- Usage counting: /skills/telemetry.md sections 3-5 ----------

    private static int _telemetryStarted;
    private static Timer? _telemetryTimer;

    private static bool UsageOff()
    {
        static string Env(string n) => (Environment.GetEnvironmentVariable(n) ?? "").Trim().ToLowerInvariant();
        if (Env("DO_NOT_TRACK") is not ("" or "0" or "false") || Env("PUBLIK_TELEMETRY") is "0" or "false" or "off")
            return true;
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(PublikDir, "consent.json"))) is JsonObject o
                && o["usage"] is JsonValue v && v.TryGetValue(out bool usage) && !usage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    internal static bool IsUuidV4(string s)
    {
        if (s.Length != 36)
            return false;
        for (int i = 0; i < 36; i++)
        {
            bool ok = i is 8 or 13 or 18 or 23 ? s[i] == '-' : Uri.IsHexDigit(s[i]);
            if (!ok)
                return false;
        }
        return s[14] == '4' && "89abAB".Contains(s[19]);
    }

    /// <summary>One random UUID per computer account, shared by every publik app, in &lt;publik dir&gt;\telemetry.json.</summary>
    private static string TelemetryInstallId()
    {
        string file = Path.Combine(PublikDir, "telemetry.json");
        string? Saved()
        {
            try
            {
                string? id = Str(JsonNode.Parse(File.ReadAllText(file)), "install_id")?.ToLowerInvariant();
                return id is not null && IsUuidV4(id) ? id : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return null;
            }
        }
        if (Saved() is { } saved)
            return saved;
        string fresh = Guid.NewGuid().ToString(); // if the file cannot be saved, this ID lasts for this run only
        try
        {
            Directory.CreateDirectory(PublikDir);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write); // never over another app's file
            using var writer = new StreamWriter(stream);
            writer.WriteLine(new JsonObject { ["install_id"] = fresh, ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") }.ToJsonString());
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Saved() ?? fresh; // another publik app saved its ID first
        }
    }

    private static async Task SendEventAsync(string evt)
    {
        try
        {
            if (UsageOff())
                return;
            string installId = TelemetryInstallId(); // after the opt-out check: no telemetry.json when usage counts are off
            string? key = Environment.GetEnvironmentVariable("PUBLIK_API_KEY") ?? Str(ReadCredential(), "key");
            if (key is not null && !key.StartsWith("pk_", StringComparison.Ordinal))
                key = null;
            var body = new JsonObject
            {
                ["v"] = 1,
                ["app"] = APP_SLUG,
                ["install_id"] = installId,
                ["event"] = evt,
                ["app_version"] = APP_VERSION.TrimStart('v', 'V'),
                ["os"] = OS,
            };
            if (Arch is { } arch)
                body["arch"] = arch;
            string url = Environment.GetEnvironmentVariable("PUBLIK_TELEMETRY_URL") is { Length: > 0 } u ? u : Origin + "/api/v1/telemetry";
            (int code, _) = await SendAsync(HttpMethod.Post, url, key, body.ToJsonString(), 5).ConfigureAwait(false);
            if (Environment.GetEnvironmentVariable("PUBLIK_TELEMETRY_DEBUG") == "1")
                Log.Info($"publik-telemetry: {evt} -> {code}");
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("PUBLIK_TELEMETRY_DEBUG") == "1")
                Log.Info($"publik-telemetry: {evt} -> {ex.Message}");
        }
    }

    /// <summary>Called by EnsureSignedInAsync(). app_opened now, active_session every 30 minutes; never fails the app, never blocks.</summary>
    public static void StartTelemetry()
    {
        if (Interlocked.Exchange(ref _telemetryStarted, 1) == 1)
            return;
        int s = int.TryParse(Environment.GetEnvironmentVariable("PUBLIK_TELEMETRY_INTERVAL_SECONDS"), out int v) ? v : 0; // tests only
        TimeSpan interval = TimeSpan.FromSeconds(s is >= 1 and <= 86_400 ? s : 1_800);
        _ = Task.Run(() => SendEventAsync("app_opened"));
        _telemetryTimer = new Timer(_ => _ = SendEventAsync("active_session"), null, interval, interval);
    }

    // ---------- The sign-in window ----------

    /// <summary>A small window that draws the gate's screens: title, body, note and one button per action.</summary>
    private sealed class GateWindow : Window
    {
        public const string ClosedByUser = "closed";

        private readonly TextBlock _body;
        private readonly TextBlock _note;
        private readonly StackPanel _buttons;
        private TaskCompletionSource<string>? _pending;
        private bool _closing;

        public GateWindow()
        {
            Title = "FileDock";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;
            Background = SystemColors.WindowBrush;
            Foreground = SystemColors.WindowTextBrush;
            try
            {
                Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FileDock.ico"));
            }
            catch (Exception)
            {
                // The window works without its icon.
            }

            var heading = new TextBlock { Text = "Sign in with publik", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
            _body = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
            _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var root = new StackPanel { Margin = new Thickness(24) };
            root.Children.Add(heading);
            root.Children.Add(_body);
            root.Children.Add(_note);
            root.Children.Add(_buttons);
            Content = root;
            Closing += (_, _) =>
            {
                _closing = true;
                _pending?.TrySetResult(ClosedByUser);
            };
        }

        /// <summary>Draws the screen; the task completes with the id of the button pressed, or <see cref="ClosedByUser"/>.</summary>
        public Task<string> ShowScreen(Screen screen)
        {
            _pending?.TrySetResult("cancelled");
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;
            if (_closing)
            {
                pending.TrySetResult(ClosedByUser);
                return pending.Task;
            }

            _body.Text = screen.Body;
            _note.Text = screen.Note ?? "";
            _note.Visibility = screen.Note is null ? Visibility.Collapsed : Visibility.Visible;
            _buttons.Children.Clear();
            foreach ((string id, string label) in screen.Buttons)
            {
                var button = new Button { Content = label, Padding = new Thickness(16, 6, 16, 6), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
                button.Click += (_, _) => pending.TrySetResult(id);
                _buttons.Children.Add(button);
            }
            if (!IsVisible)
                Show();
            Activate();
            return pending.Task;
        }

        public void CloseQuietly()
        {
            if (_closing)
                return;
            _closing = true;
            Close();
        }
    }
}

internal sealed class PublikException(string message) : Exception(message);
