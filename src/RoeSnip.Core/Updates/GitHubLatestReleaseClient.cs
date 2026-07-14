using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RoeSnip.Core.Updates;

/// <summary>Outcome of one <see cref="GitHubLatestReleaseClient.ProbeAsync"/> call.
/// <see cref="Payload"/> is a real 200 with a parseable body; <see cref="NotModified"/> is a 304
/// answered against a previously committed ETag (no body was read); <see cref="RateLimited"/> is a
/// 403/429 (or a live backoff window from an earlier one); <see cref="Failed"/> covers every other
/// non-success status or exception. Callers treat everything except Payload as "nothing new right
/// now" and keep whatever update state they already had.</summary>
public enum ProbeStatus
{
    Payload,
    NotModified,
    RateLimited,
    Failed,
}

/// <summary>One probe's result. <see cref="Json"/> is only non-null for <see cref="ProbeStatus.Payload"/>
/// and is the caller's to dispose (a JsonDocument holds pooled buffers). <see cref="ETag"/> is the
/// response's ETag header value on a Payload result — the caller passes it to
/// <see cref="GitHubLatestReleaseClient.CommitETag"/> once it has decided what the payload meant,
/// never here automatically (see that method's doc comment for why).</summary>
public sealed record ProbeResult(ProbeStatus Status, JsonDocument? Json, string? ETag);

/// <summary>Conditional-GET wrapper around GitHub's "releases/latest" REST endpoint — the whole
/// network-thrift strategy for periodic update checking. GitHub's REST docs guarantee a conditional
/// request answered 304 does NOT count against the unauthenticated rate limit, so in steady state
/// (no new release published) every periodic check after the first is a free 304: this class exists
/// to make that the easy, default path rather than something each call site has to remember to do.
///
/// ETag state is IN-MEMORY ONLY, one instance per process (each app's UpdateManager holds a single
/// static instance) — there is no persistence across launches. That is deliberate, not an
/// oversight: the periodic loop this feeds lives inside one long-running tray resident, so the
/// in-memory ETag already covers the entire window where conditional requests matter. After a real
/// update is applied the process restarts anyway (a fresh 200 on the next launch), which is the
/// correct, negligible cost — persisting the ETag to disk would add a file and a synchronization
/// concern for a case (saving one HTTP round trip once per app launch) that doesn't need it.
///
/// LOAD-BEARING INVARIANT: <see cref="ProbeAsync"/> never auto-stores the ETag it receives — only
/// <see cref="CommitETag"/> does, and callers must call it ONLY when their parse of the payload
/// concluded "no update available". If a check finds an update but the subsequent download/apply
/// then fails, the ETag must NOT be committed: committing it would make every later probe answer
/// 304 ("nothing changed") even though the caller never actually applied anything, silently
/// disabling retry until the NEXT release ships and changes the underlying resource. Storing the
/// ETag only on a no-update outcome keeps the invariant "304 always means still no update" true.</summary>
public sealed class GitHubLatestReleaseClient
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly Func<DateTime> _utcNow;

    private string? _etag;
    private DateTime? _backoffUntilUtc;

    /// <param name="utcNow">Injectable clock, purely so backoff-window tests can control elapsed
    /// time without a real Task.Delay. Defaults to <see cref="DateTime.UtcNow"/>.</param>
    public GitHubLatestReleaseClient(string owner, string repo, Func<DateTime>? utcNow = null)
    {
        _owner = owner;
        _repo = repo;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>GETs GitHub's "releases/latest" endpoint, conditionally (via If-None-Match) once an
    /// ETag has been committed. <paramref name="bypassBackoff"/> forces a real network attempt even
    /// inside an active rate-limit backoff window — reserved for a deliberate user-initiated "Check
    /// for updates" click, which deserves a real answer; one extra request in that case is
    /// negligible against the 60/hour unauthenticated budget. Never throws: any transport failure
    /// or unexpected status maps to <see cref="ProbeStatus.Failed"/>.</summary>
    public async Task<ProbeResult> ProbeAsync(HttpClient client, bool bypassBackoff = false)
    {
        if (!bypassBackoff && _backoffUntilUtc is DateTime backoffUntil && _utcNow() < backoffUntil)
        {
            // Still inside a rate-limit backoff window from an earlier 403/429 — answer without
            // touching the network at all, which is the whole point of backing off in the first
            // place (a probe that itself gets rate-limited teaches nothing new).
            return new ProbeResult(ProbeStatus.RateLimited, Json: null, ETag: null);
        }

        try
        {
            string url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            }

            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);

            // 304 must be branched BEFORE any IsSuccessStatusCode check: it is a non-2xx status, so
            // treating it through the generic success/failure split below would misfile a correct,
            // expected "nothing changed" answer as an error.
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new ProbeResult(ProbeStatus.NotModified, Json: null, ETag: null);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            {
                TimeSpan backoff = TimeSpan.FromHours(1);
                if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta)
                {
                    backoff = retryAfterDelta;
                }
                else if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAfterDate)
                {
                    backoff = retryAfterDate.UtcDateTime - _utcNow();
                }

                if (backoff < TimeSpan.Zero)
                {
                    backoff = TimeSpan.Zero;
                }

                _backoffUntilUtc = _utcNow() + backoff;
                return new ProbeResult(ProbeStatus.RateLimited, Json: null, ETag: null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(ProbeStatus.Failed, Json: null, ETag: null);
            }

            string? etag = response.Headers.ETag?.Tag;
            await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            JsonDocument document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            return new ProbeResult(ProbeStatus.Payload, document, etag);
        }
        catch (Exception)
        {
            // A network failure or malformed response means the same thing to every caller: nothing
            // actionable right now. Never throw out of a periodic background loop.
            return new ProbeResult(ProbeStatus.Failed, Json: null, ETag: null);
        }
    }

    /// <summary>Stores <paramref name="etag"/> so the NEXT <see cref="ProbeAsync"/> sends it as
    /// If-None-Match. Callers must only call this after concluding the just-probed payload meant
    /// "no update available" — see this class's own doc comment for why committing on a
    /// found-but-failed-to-apply update would be wrong. A null/empty value clears the stored ETag
    /// (falls back to an unconditional GET next time), which is harmless — GitHub simply answers
    /// with a fresh 200.</summary>
    public void CommitETag(string? etag) => _etag = etag;
}
