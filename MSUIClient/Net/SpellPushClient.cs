using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MSUIClient.Net;

// ─────────────────────────────────────────────────────────────────────────────
// The creator's design→data handoff, over the wire.
//
// The spell session still lands in spell-session.json next to the client - that
// file is the offline record, and the Spell Completer still accepts it by hand.
// This is the direct path: POST one finished design to MangosSuperUI and it
// appears in the Completer's inbox, ready to be named, costed and built.
//
// THREADING CONTRACT: the post runs on a background Task; the game thread only
// ever reads the volatile result reference, which is immutable once published.
// Nothing here may touch EntityStore, Settings or any renderer - it is handed a
// finished JSON document and a URL, and that is all it knows.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Published outcome of one push. Ok=false means the design did not
/// land; Error says why in the words the workshop shows the user.</summary>
public sealed record SpellPushResult(bool Ok, string TempName, int Accepted, string? Error,
    DateTime CompletedUtc);

public sealed class SpellPushClient
{
    // Design payloads run to megabytes (patched M2s, recolored BLPs, audio), and
    // the server writes them to disk before answering - well past the 15 s the
    // read-only dev fetches use.
    private readonly HttpClient _http = WebAppHttp.Create(TimeSpan.FromMinutes(2));

    private volatile SpellPushResult? _result;
    private Task? _pushing;

    /// <summary>The latest completed push, or null until one finishes.</summary>
    public SpellPushResult? Result => _result;

    public bool Pushing => _pushing is { IsCompleted: false };

    /// <summary>Clear the last outcome - the workshop calls this when the user
    /// starts editing again, so a stale banner cannot outlive what it described.</summary>
    public void ClearResult() => _result = null;

    /// <summary>Send one design to the Spell Completer's inbox. No-op while a push
    /// is already in flight, so a double-click cannot race two uploads of the
    /// same temp name.</summary>
    public void BeginPush(string baseUrl, JsonObject spell)
    {
        if (Pushing) return;
        _result = null;
        string tempName = spell["tempName"]?.GetValue<string>() ?? "";
        // Serialize on THIS thread: the caller owns the document and is free to
        // mutate it the moment this returns.
        string json = spell.ToJsonString();
        _pushing = Task.Run(() => Push(baseUrl, tempName, json));
    }

    private async Task Push(string baseUrl, string tempName, string json)
    {
        try
        {
            string url = $"{baseUrl.TrimEnd('/')}/SpellCompleter/Push";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // 413 is the one failure worth naming: it means the design got
                // through the network and was refused for its size, which is a
                // server setting, not something retrying will fix.
                string detail = resp.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge
                    ? "the server refused the design as too large"
                    : $"HTTP {(int)resp.StatusCode}: {Trim(body)}";
                Publish(false, tempName, 0, detail);
                return;
            }

            PushDto? dto = JsonSerializer.Deserialize<PushDto>(body, Json);
            if (dto is null || !dto.Success)
            {
                Publish(false, tempName, 0, dto?.Error ?? $"unreadable reply: {Trim(body)}");
                return;
            }
            Publish(true, tempName, dto.Accepted, null);
        }
        catch (Exception ex)
        {
            Publish(false, tempName, 0, ex.Message);
        }
    }

    private void Publish(bool ok, string tempName, int accepted, string? error)
    {
        _result = new SpellPushResult(ok, tempName, accepted, error, DateTime.UtcNow);
        Console.WriteLine(ok
            ? $"[spell-push] '{tempName}' accepted ({accepted} spell(s))"
            : $"[spell-push] '{tempName}' failed: {error}");
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class PushDto
    {
        public bool Success { get; set; }
        public int Accepted { get; set; }
        public string? Error { get; set; }
    }
}
