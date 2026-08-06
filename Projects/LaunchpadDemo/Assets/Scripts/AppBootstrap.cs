using System;
using System.Threading;
using Bluecadet.Launchpad;
using Bluecadet.Utils;
using UnityEngine;

namespace LaunchpadDemo
{
	/// <summary>
	/// Composition root: the one place where Utils (environment + settings)
	/// and Launchpad (feed + store + cache + gate + manager) are constructed
	/// and wired together. Everything below it depends only on the pieces it
	/// is handed, so this is also the only file that knows the whole graph.
	/// </summary>
	public sealed class AppBootstrap : MonoBehaviour
	{
		private SettingsFile<AppConfig> _settings;
		private LaunchpadClient _client;
		private ContentManager<Slide> _content;
		private TextureCache _textures;
		private IdleGate _gate;
		private IdleTimeout _idle;
		private CancellationTokenSource _cts;

		private void Awake()
		{
			// 1. Environment first: DataPath honors --assetsPath, MachineId
			//    honors --machineId, so a kiosk install can relocate all
			//    settings + content outside the build without code changes.
			AppEnvironment env = AppEnvironment.Current;

			// 2. Settings via the tiered cascade (settings.json →
			//    settings.<machineId>.json → settings.local.json → --set).
			//    Keep the SettingsFile around — the Bluecadet Project Settings
			//    pane and TierFor() can then tell you where a value came from.
			_settings = env.SettingsFile<AppConfig>();
			AppConfig cfg = _settings.Value;

			// 3. Launchpad graph. contentRoot is relative in settings and
			//    resolved against DataPath here — same relocation story as
			//    the settings files themselves.
			_client = new LaunchpadClient(cfg.controllerUrl, cfg.consumerId);
			var store = new ContentStore<Slide>(
				env.ResolvePath(cfg.contentRoot), cfg.sourceFolders, new SlideMapper());
			_textures = new TextureCache();
			_gate = new IdleGate(TimeSpan.FromSeconds(cfg.maxSwapDeferSeconds));

			_content = new ContentManager<Slide>(_client, store, _textures, _gate,
				slide => string.IsNullOrEmpty(slide.imagePath)
					? Array.Empty<string>()
					: new[] { slide.imagePath });

			_content.OnVersionStaged += v =>
				Debug.Log($"[Demo] Staged {v.VersionId}: +{v.Diff.Added.Count} ~{v.Diff.Changed.Count} -{v.Diff.RemovedIds.Count}");
			_content.OnVersionApplied += v =>
				Debug.Log($"[Demo] Applied {v.VersionId} ({v.Items.Count} slides)");

			// 4. Idle policy: Utils' IdleTimeout drives Launchpad's IdleGate,
			//    so staged content only swaps in while nobody is interacting
			//    (or after maxSwapDeferSeconds as a backstop).
			_idle = gameObject.AddComponent<IdleTimeout>();
			_idle.IdleTimeoutIntervals.Add(cfg.idleAfterSeconds);
			_idle.OnIdleState += _ => _gate.SetSwappable(true);

			// 5. Start. ContentManager cold-boots from whatever is already on
			//    disk, so the app works with the controller unreachable.
			_cts = new CancellationTokenSource();
			_client.Start(_cts.Token);
			_content.Start(_cts.Token);
		}

		private void Update()
		{
			// IdleTimeout doesn't hook input itself; the app decides what
			// counts as activity. Any activity also closes the swap gate.
			if (Input.anyKeyDown || Input.GetMouseButtonDown(0))
			{
				_idle.OnUserActivity();
				_gate.SetSwappable(false);
			}

			_content.TickMainThread();
		}

		private void OnDestroy()
		{
			_cts.Cancel();
			_content.Dispose();
			_client.Dispose();
			_textures.Dispose();
			_cts.Dispose();
		}

		// Diagnostic HUD only — a real app replaces this with its view layer,
		// subscribing to OnVersionApplied and reading _content.Current.
		private void OnGUI()
		{
			AppConfig cfg = _settings.Value;

			GUILayout.BeginArea(new Rect(16, 16, Screen.width - 32, Screen.height - 32));
			GUILayout.Label($"Controller  {cfg.controllerUrl}  [{_client.State}]");
			GUILayout.Label($"Settings    {string.Join("  →  ", _settings.LoadedPaths)}");
			GUILayout.Label($"Content     {_content.State}  current={_content.CurrentVersionId ?? "-"}  staged={_content.StagedVersionId ?? "-"}  textures={_textures.Count}");

			if (_content.StagedVersionId != null && GUILayout.Button("Apply staged now", GUILayout.Width(160)))
			{
				_content.ApplyStagedNow();
			}

			GUILayout.Space(12);
			foreach (ContentItem<Slide> item in _content.Current)
			{
				GUILayout.Label($"•  {item.Data.title} — {item.Data.body}");
			}

			GUILayout.EndArea();
		}
	}
}
