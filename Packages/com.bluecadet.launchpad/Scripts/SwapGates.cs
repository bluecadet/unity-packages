using System;
using UnityEngine;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Policy controlling when ContentManager may commit a staged version
	/// onto Current. Implementations are consulted from the main thread only.
	/// </summary>
	public interface ISwapGate
	{
		bool CanSwapNow { get; }

		/// <summary>
		/// Called by ContentManager when a version enters Staged. Gates with
		/// a deferral policy start their countdown here; stateless gates can
		/// no-op.
		/// </summary>
		void NotifyStagedPending();

		/// <summary>
		/// Called by ContentManager after a staged version is committed or
		/// discarded, so pending state never leaks into the next version.
		/// </summary>
		void ClearPending();
	}

	/// <summary>Always allows an immediate swap; no deferral policy.</summary>
	public sealed class ImmediateGate : ISwapGate
	{
		public bool CanSwapNow => true;

		public void NotifyStagedPending()
		{
		}

		public void ClearPending()
		{
		}
	}

	/// <summary>
	/// Gates content swaps behind an app-driven "safe to swap" flag (e.g. only
	/// while idle/attract), but force-commits staged content after maxDefer
	/// elapses so content never goes stale indefinitely behind a busy UI.
	/// Main-thread only; uses Time.realtimeSinceStartup, which is safe to
	/// read/write only from the main thread anyway.
	/// </summary>
	public sealed class IdleGate : ISwapGate
	{
		private readonly float _maxDeferSeconds;
		private bool _swappable;
		private bool _pending;
		private float _pendingSinceRealtime;

		public IdleGate(TimeSpan maxDefer)
		{
			_maxDeferSeconds = (float)maxDefer.TotalSeconds;
		}

		public bool CanSwapNow
		{
			get
			{
				if (_swappable)
				{
					return true;
				}

				return _pending && (Time.realtimeSinceStartup - _pendingSinceRealtime) > _maxDeferSeconds;
			}
		}

		/// <summary>App drives this from its state machine (e.g. true while in attract state).</summary>
		public void SetSwappable(bool canSwap)
		{
			_swappable = canSwap;
		}

		/// <summary>
		/// Manager calls this when a version enters Staged; starts the
		/// max-defer countdown. Only stamps the pending timestamp on the
		/// first call while already pending — otherwise back-to-back stages
		/// (a new version superseding one already Staged) would keep pushing
		/// the max-defer deadline out forever and a never-idle app would
		/// never be force-committed.
		/// </summary>
		public void NotifyStagedPending()
		{
			if (!_pending)
			{
				_pending = true;
				_pendingSinceRealtime = Time.realtimeSinceStartup;
			}
		}

		/// <summary>Manager calls this after commit.</summary>
		public void ClearPending()
		{
			_pending = false;
		}
	}
}
