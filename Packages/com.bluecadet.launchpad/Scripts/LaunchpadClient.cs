using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Bluecadet.Launchpad
{
	public enum ConnectionState
	{
		Connecting,
		Connected,
		Disconnected
	}

	/// <summary>
	/// Protocol-only client for the Launchpad controller HTTP API: an SSE
	/// listener for "content:version:promoted" events, a 10s fallback poll of
	/// content.manifest.read, and ack sending. Has zero knowledge of content
	/// shape — it only ever surfaces versionId strings. All parsing happens
	/// off the main thread; OnVersionPromoted is always raised on the main
	/// thread via Awaitable.MainThreadAsync().
	/// </summary>
	public sealed class LaunchpadClient : IVersionFeed, IDisposable
	{
		private const float FallbackIntervalSeconds = 10f;
		private const int FallbackRequestTimeoutSeconds = 12;
		private const int SseReconnectDelayMs = 2000;
		private static readonly int[] AckBackoffMs = { 1000, 2000, 4000 };
		private static readonly int AckMaxAttempts = AckBackoffMs.Length + 1;

		private readonly string _controllerUrl;
		private readonly string _consumerId;

		// Default HttpClient.Timeout is 100s, which would kill the
		// long-lived SSE connection (/events is expected to stay open
		// indefinitely between promotions). Lifetime is instead bounded by
		// this client's own CancellationTokenSource.
		private readonly HttpClient _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		private readonly System.Random _random = new System.Random();

		private CancellationTokenSource _cts;

		// Last versionId raised via OnVersionPromoted, forever (not per SSE
		// connection). Read and written on the main thread only, after
		// RaiseVersionPromotedAsync's hop, so the SSE loop and the fallback
		// loop can't both raise the same id. Intentionally sticky rather
		// than reset on reconnect, because the server replays the last
		// promoted frame on every reconnect and there is no way to tell that
		// replay apart from a genuine re-promotion of the same id. Net
		// effect: a same-id re-promotion with no other version in between is
		// silently suppressed.
		private string _lastRaisedVersionId;
		private bool _disposed;

		public ConnectionState State { get; private set; } = ConnectionState.Connecting;
		public DateTime LastEventUtc { get; private set; }

		/// <summary>
		/// Fired on the main thread for every distinct versionId seen. The
		/// server replays the last promoted SSE frame on every reconnect, so
		/// this dedupes against the last-raised versionId to avoid re-firing
		/// for the same replayed value. Known limitation: a legitimate
		/// re-promotion of the exact same versionId with nothing in between
		/// is indistinguishable from a replay at this layer, so it is also
		/// suppressed. Downstream (ContentManager) treats re-raises of the
		/// current/staged versionId as a no-op anyway, so this is low-risk.
		/// </summary>
		public event Action<string> OnVersionPromoted;

		public LaunchpadClient(string controllerUrl, string consumerId)
		{
			_controllerUrl = controllerUrl;
			_consumerId = consumerId;
		}

		/// <summary>Starts the SSE loop and the 10s manifest fallback poll as background tasks.</summary>
		public void Start(CancellationToken externalToken)
		{
			_cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
			_ = RunSseLoopAsync(_cts.Token);
			_ = RunFallbackLoopAsync(_cts.Token);
		}

		private async Task RunSseLoopAsync(CancellationToken token)
		{
			string url = CombineUrl(_controllerUrl, "/events");

			while (!token.IsCancellationRequested)
			{
				HttpResponseMessage response = null;
				CancellationTokenRegistration unblockRegistration = default;

				try
				{
					State = ConnectionState.Connecting;

					response = await _httpClient
						.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
						.ConfigureAwait(false);

					// .NET Standard 2.1's StreamReader.ReadLineAsync() has no
					// CancellationToken overload, so a pending read would
					// otherwise block past Dispose()/cancellation forever.
					// Registering a stream/response dispose against the token
					// makes the pending read throw instead, which the catch
					// below treats as a normal loop exit.
					unblockRegistration = token.Register(() => response.Dispose());

					using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
					using (var reader = new StreamReader(stream))
					{
						State = ConnectionState.Connected;

						string eventName = null;
						var dataBuilder = new StringBuilder();

						while (!token.IsCancellationRequested)
						{
							string line = await reader.ReadLineAsync().ConfigureAwait(false);
							if (line == null)
							{
								// Stream closed by server; fall through to reconnect.
								break;
							}

							if (line.Length == 0)
							{
								if (dataBuilder.Length > 0)
								{
									await HandleSseEventAsync(eventName, dataBuilder.ToString()).ConfigureAwait(false);
								}

								eventName = null;
								dataBuilder.Clear();
								continue;
							}

							if (line.StartsWith("event:", StringComparison.Ordinal))
							{
								eventName = line.Substring(6).Trim();
							}
							else if (line.StartsWith("data:", StringComparison.Ordinal))
							{
								if (dataBuilder.Length > 0)
								{
									dataBuilder.Append('\n');
								}

								dataBuilder.Append(line.Substring(5).Trim());
							}

							// Ignore comment lines (":") and other SSE fields (id:, retry:).
						}
					}
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					if (token.IsCancellationRequested)
					{
						// Dispose()/cancellation unblocked the pending read above
						// (via the token.Register callback disposing the
						// response); this is the expected shutdown path, not a
						// connection error, so exit quietly instead of logging.
						break;
					}

					State = ConnectionState.Disconnected;
					Debug.LogWarning($"[LaunchpadClient] SSE error: {ex.Message}");
				}
				finally
				{
					unblockRegistration.Dispose();
					response?.Dispose();
				}

				if (token.IsCancellationRequested)
				{
					break;
				}

				State = ConnectionState.Disconnected;

				try
				{
					// +/-20% jitter so a controller restart doesn't get hammered
					// by every consumer reconnecting in lockstep.
					double jitterFactor = 1.0 + ((_random.NextDouble() * 0.4) - 0.2);
					int delayMs = Math.Max(0, (int)(SseReconnectDelayMs * jitterFactor));
					await Task.Delay(delayMs, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}

		private async Task HandleSseEventAsync(string eventName, string data)
		{
			LastEventUtc = DateTime.UtcNow;

			if (!string.Equals(eventName, "content:version:promoted", StringComparison.Ordinal))
			{
				return;
			}

			await RaisePromotedFromJsonAsync(data, "SSE payload").ConfigureAwait(false);
		}

		/// <summary>
		/// Pulls the versionId out of a controller payload and raises it. A
		/// parse failure is warned about (tagged with <paramref name="source"/>)
		/// and otherwise ignored, since both callers are loops that must keep
		/// running.
		/// </summary>
		private async Task RaisePromotedFromJsonAsync(string json, string source)
		{
			string versionId = null;
			try
			{
				versionId = LaunchpadJson.ExtractVersionId(JToken.Parse(json));
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[LaunchpadClient] Failed to parse {source}: {ex.Message}");
			}

			await RaiseVersionPromotedAsync(versionId).ConfigureAwait(false);
		}

		private async Task RunFallbackLoopAsync(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					await Task.Delay(TimeSpan.FromSeconds(FallbackIntervalSeconds), token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}

				try
				{
					string responseText;

					// _httpClient.Timeout is infinite (kept open for the SSE
					// stream), so this request needs its own bound or a
					// hung POST silently disables the fallback safety net.
					using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token))
					{
						requestCts.CancelAfter(TimeSpan.FromSeconds(FallbackRequestTimeoutSeconds));

						using (var response = await PostCommandAsync("content.manifest.read", requestCts.Token).ConfigureAwait(false))
						{
							responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
						}
					}

					LastEventUtc = DateTime.UtcNow;

					await RaisePromotedFromJsonAsync(responseText, "manifest.read response").ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					break;
				}
				catch (OperationCanceledException)
				{
					// requestCts.CancelAfter fired, not the loop token: the
					// request timed out rather than the loop being torn down.
					// Log and let the loop retry on the next interval.
					Debug.LogWarning($"[LaunchpadClient] Fallback manifest.read timed out after {FallbackRequestTimeoutSeconds}s.");
				}
				catch (Exception ex)
				{
					Debug.LogWarning($"[LaunchpadClient] Fallback manifest.read failed: {ex.Message}");
				}
			}
		}

		private async Task RaiseVersionPromotedAsync(string versionId)
		{
			if (string.IsNullOrEmpty(versionId))
			{
				return;
			}

			// Hop before the dedupe check so both it and the event happen on
			// the main thread: the SSE loop and the fallback loop can
			// otherwise observe the same new versionId concurrently and both
			// raise it.
			await Awaitable.MainThreadAsync();

			if (versionId == _lastRaisedVersionId)
			{
				// Deliberately silent: the 10s fallback poll re-reads the
				// current version forever, so this is the steady state, not
				// an event.
				return;
			}

			_lastRaisedVersionId = versionId;
			Debug.Log($"[LaunchpadClient] Version promoted: '{versionId}'.");
			OnVersionPromoted?.Invoke(versionId);
		}

		/// <summary>
		/// Sends a content.ack command. Retries up to 3 total attempts with
		/// 1s/2s backoff between them; never throws, returns whether any
		/// attempt succeeded.
		/// </summary>
		public async Task<bool> AckAsync(string versionId, CancellationToken ct)
		{
			for (int attempt = 0; attempt < AckMaxAttempts; attempt++)
			{
				if (attempt > 0)
				{
					try
					{
						await Task.Delay(AckBackoffMs[attempt - 1], ct).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						return false;
					}
				}

				try
				{
					var fields = new JObject
					{
						["consumerId"] = _consumerId,
						["versionId"] = versionId
					};

					using (var response = await PostCommandAsync("content.ack", ct, fields).ConfigureAwait(false))
					{
						if (response.IsSuccessStatusCode)
						{
							return true;
						}

						string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
						Debug.LogWarning(
							$"[LaunchpadClient] Ack for version '{versionId}' attempt {attempt + 1}/{AckMaxAttempts} returned {(int)response.StatusCode}: {text}");
					}
				}
				catch (OperationCanceledException)
				{
					return false;
				}
				catch (Exception ex)
				{
					Debug.LogWarning(
						$"[LaunchpadClient] Ack for version '{versionId}' attempt {attempt + 1}/{AckMaxAttempts} failed: {ex.Message}");
				}
			}

			return false;
		}

		/// <summary>
		/// POSTs one command envelope to the controller: <c>{"type": ...}</c>
		/// plus whatever <paramref name="fields"/> adds. The caller owns the
		/// returned response and must dispose it; the request body is disposed
		/// here, which is safe because PostAsync has already buffered the
		/// response by the time it returns.
		/// </summary>
		private async Task<HttpResponseMessage> PostCommandAsync(string type, CancellationToken ct, JObject fields = null)
		{
			var body = new JObject { ["type"] = type };
			if (fields != null)
			{
				body.Merge(fields);
			}

			string url = CombineUrl(_controllerUrl, "/command");
			using (var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"))
			{
				return await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
			}
		}

		private static string CombineUrl(string baseUrl, string path)
		{
			if (string.IsNullOrEmpty(baseUrl))
			{
				return path;
			}

			return baseUrl.TrimEnd('/') + path;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;

			try
			{
				_cts?.Cancel();
				_cts?.Dispose();
			}
			catch
			{
				// Best-effort teardown.
			}

			_httpClient?.Dispose();
		}
	}
}
