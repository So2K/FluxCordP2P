using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxCord;

/// <summary>
/// Discord Remote Auth Gateway v2 (QR Code Login).
/// Full WebSocket EndOfMessage reassembly + serialized sends + clean lifecycle disposal.
/// </summary>
public sealed class RemoteAuthQr
{
    public static readonly RemoteAuthQr Instance = new();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly object _lifeLock = new();
    private CancellationTokenSource? _cts;

    public bool[,]? Matrix { get; private set; }
    public string Status { get; private set; } = "Генерация QR-кода...";
    public string ScannedUser { get; private set; } = "";
    public bool IsExpired { get; private set; }
    public bool IsRunning { get; private set; }
    public long ExpiresAtTick { get; private set; }

    public event Action? Changed;

    private RemoteAuthQr()
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://discord.com");
    }

    private void Notify() => Changed?.Invoke();

    public void Start()
    {
        CancellationToken ct;
        lock (_lifeLock)
        {
            StopLocked();
            IsRunning = true;
            IsExpired = false;
            ScannedUser = "";
            Matrix = null;
            Status = "Подключение к Discord...";
            ExpiresAtTick = 0;
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
        }
        Notify();
        _ = Task.Run(() => RunAsync(ct), ct);
    }

    public void Stop()
    {
        lock (_lifeLock) StopLocked();
    }

    private void StopLocked()
    {
        IsRunning = false;
        var old = _cts;
        _cts = null;
        if (old != null)
        {
            try { old.Cancel(); } catch { }
            try { old.Dispose(); } catch { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var rsa = RSA.Create(2048);
        using var ws = new ClientWebSocket();
        using var sendLock = new SemaphoreSlim(1, 1);

        async Task SendJsonAsync(string json)
        {
            if (ws.State != WebSocketState.Open || ct.IsCancellationRequested) return;
            await sendLock.WaitAsync(ct);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
            }
            finally { sendLock.Release(); }
        }

        try
        {
            string pubKeyB64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            ws.Options.SetRequestHeader("Origin", "https://discord.com");
            ws.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            await ws.ConnectAsync(new Uri("wss://remote-auth-gateway.discord.gg/?v=2"), ct);

            var buf = new byte[16384];
            using var msgStream = new MemoryStream();
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                msgStream.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        if (!DiscordClient.Instance.IsConnected)
                        {
                            IsExpired = true;
                            Status = "QR-код истёк — нажми «Обновить»";
                            Notify();
                        }
                        return;
                    }
                    msgStream.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                if (msgStream.Length == 0) continue;
                using var doc = JsonDocument.Parse(msgStream.GetBuffer().AsMemory(0, (int)msgStream.Length));
                var root = doc.RootElement;
                string op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";

                switch (op)
                {
                    case "hello":
                    {
                        int hbMs = root.TryGetProperty("heartbeat_interval", out var hb) ? hb.GetInt32() : 41250;
                        int timeoutMs = root.TryGetProperty("timeout_ms", out var tm) ? tm.GetInt32() : 120000;
                        ExpiresAtTick = Environment.TickCount64 + timeoutMs;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                                {
                                    await Task.Delay(hbMs, ct);
                                    await SendJsonAsync("{\"op\":\"heartbeat\"}");
                                }
                            }
                            catch { }
                        }, ct);
                        _ = CountdownTickAsync(ct);
                        await SendJsonAsync(BuildJsonObj("op", "init", "encoded_public_key", pubKeyB64));
                        break;
                    }
                    case "nonce_proof":
                    {
                        string encNonce = root.GetProperty("encrypted_nonce").GetString() ?? "";
                        byte[] dec = rsa.Decrypt(Convert.FromBase64String(encNonce), RSAEncryptionPadding.OaepSHA256);
                        string proof = Base64Url(SHA256.HashData(dec));
                        await SendJsonAsync(BuildJsonObj("op", "nonce_proof", "proof", proof));
                        break;
                    }
                    case "pending_remote_init":
                    {
                        string fp = root.GetProperty("fingerprint").GetString() ?? "";
                        Matrix = QrCode.Encode("https://discord.com/ra/" + fp);
                        Status = "Отсканируй в Discord на телефоне";
                        SettingsStore.Log("QR ready: fingerprint=" + fp);
                        Notify();
                        break;
                    }
                    case "pending_ticket":
                    case "pending_finish":
                    {
                        if (root.TryGetProperty("encrypted_user_payload", out var eup) && eup.ValueKind == JsonValueKind.String)
                        {
                            byte[] dec = rsa.Decrypt(Convert.FromBase64String(eup.GetString()!), RSAEncryptionPadding.OaepSHA256);
                            var parts = Encoding.UTF8.GetString(dec).Split(':');
                            ScannedUser = parts.Length >= 4 ? string.Join(':', parts[3..]) : "аккаунт";
                        }
                        Status = $"Отсканировано: {ScannedUser} — подтверди на телефоне!";
                        Notify();
                        break;
                    }
                    case "pending_login":
                    {
                        string ticket = root.GetProperty("ticket").GetString() ?? "";
                        Status = "Получение токена...";
                        Notify();
                        string token = await ExchangeTicketAsync(rsa, ticket, ct);
                        if (!string.IsNullOrEmpty(token))
                        {
                            Status = "Успешно! Подключаемся...";
                            Notify();
                            Stop();
                            _ = DiscordClient.Instance.ConnectAsync(token);
                            return;
                        }
                        IsExpired = true;
                        Status = "Не удалось завершить вход — обнови QR";
                        Notify();
                        return;
                    }
                    case "finish":
                    {
                        string encTok = root.GetProperty("encrypted_token").GetString() ?? "";
                        byte[] dec = rsa.Decrypt(Convert.FromBase64String(encTok), RSAEncryptionPadding.OaepSHA256);
                        string token = Encoding.UTF8.GetString(dec);
                        Status = "Успешно! Подключаемся...";
                        Notify();
                        Stop();
                        _ = DiscordClient.Instance.ConnectAsync(token);
                        return;
                    }
                    case "cancel":
                    {
                        IsExpired = true;
                        ScannedUser = "";
                        Status = "Вход отменён — нажми «Обновить»";
                        Notify();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                IsExpired = true;
                Status = "Ошибка QR: " + ex.Message;
                SettingsStore.Log("QR error: " + ex);
                Notify();
            }
        }
    }

    private async Task<string> ExchangeTicketAsync(RSA rsa, string ticket, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/v9/users/@me/remote-auth/login");
        req.Content = new StringContent(BuildJsonObj("ticket", ticket), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            SettingsStore.Log($"QR ticket exchange HTTP {(int)resp.StatusCode}");
            return "";
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("encrypted_token", out var et)) return "";
        byte[] dec = rsa.Decrypt(Convert.FromBase64String(et.GetString()!), RSAEncryptionPadding.OaepSHA256);
        return Encoding.UTF8.GetString(dec);
    }

    private static string BuildJsonObj(params string[] kv)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            for (int i = 0; i + 1 < kv.Length; i += 2) w.WriteString(kv[i], kv[i + 1]);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task CountdownTickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsRunning && !IsExpired)
            {
                await Task.Delay(1000, ct);
                Notify();
            }
        }
        catch { }
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
