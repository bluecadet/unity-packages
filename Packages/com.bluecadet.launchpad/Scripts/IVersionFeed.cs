using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// The controller-facing half of the version lifecycle, as ContentManager
	/// sees it: something that announces promoted version ids and accepts
	/// acknowledgements for them. LaunchpadClient is the production
	/// implementation (HTTP/SSE); tests and offline/kiosk-only setups can
	/// substitute their own without dragging in the transport.
	/// </summary>
	public interface IVersionFeed
	{
		/// <summary>
		/// Raised on the main thread for every distinct promoted versionId.
		/// Implementations are expected to dedupe replays of the same id.
		/// </summary>
		event Action<string> OnVersionPromoted;

		/// <summary>
		/// Acknowledges that versionId has been received and prepared.
		/// Must never throw; returns whether the ack was accepted.
		/// </summary>
		Task<bool> AckAsync(string versionId, CancellationToken ct);
	}
}
